using System.Net;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RobustClientTests
{
    private static readonly LauncherSettings Defaults =
        new SettingsStore(Path.Combine(Path.GetTempPath(), "dg-robust-" + Guid.NewGuid().ToString("N"), "settings.json")).LoadOrCreate();
    private const string Pid = "11111111-2222-3333-4444-555555555555";

    private static (RobustClient Client, StubHandler Handler) Make(Func<RecordedRequest, (HttpStatusCode, string)> reply)
    {
        var h = new StubHandler(reply);
        return (new RobustClient(new HttpClient(h), () => Defaults), h);
    }

    [Fact]
    public async Task Port_change_redirects_next_call()
    {
        var h = new StubHandler(r => (HttpStatusCode.OK, "<gridinfo></gridinfo>"));
        var settings = Defaults;
        var c = new RobustClient(new HttpClient(h), () => settings);

        Assert.True(await c.IsReadyAsync(CancellationToken.None));
        settings = settings with { RobustPublicPort = 8102, RobustPrivatePort = 8103 };
        Assert.True(await c.IsReadyAsync(CancellationToken.None));
        await c.AuthenticateAsync(Guid.Parse(Pid), "x", CancellationToken.None);

        Assert.Equal([8002, 8102, 8103], h.Requests.Select(q => q.Uri.Port));
    }

    [Fact]
    public async Task Region_server_asks_the_grid_asset_server()
    {
        var h = new StubHandler(r => (HttpStatusCode.OK, "<ServerResponse><result>null</result></ServerResponse>"));
        var region = Defaults with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006),
        };
        var local = Defaults with { Plan = OwnerPlan.Local };

        await new RobustClient(new HttpClient(h), () => region).GetAccountAsync("Ann", "Lee", CancellationToken.None);
        await new RobustClient(new HttpClient(h), () => local).GetAccountAsync("Ann", "Lee", CancellationToken.None);

        Assert.Equal(["http://192.168.1.5:8003/accounts", $"http://127.0.0.1:{local.RobustPrivatePort}/accounts"],
            h.Requests.Select(q => q.Uri.ToString()));
    }

    [Fact]
    public async Task Region_server_reaches_the_grid_asset_server_at_the_typed_address_not_its_public_name()
    {
        var h = new StubHandler(r => (HttpStatusCode.OK, "<gridinfo></gridinfo>"));
        var region = Defaults with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://grid.example.org:8002/", "7.1.0", false, 8004, 8006),
        };

        await new RobustClient(new HttpClient(h), () => region).IsReadyAsync(CancellationToken.None);

        Assert.Equal(["http://192.168.1.5:8002/get_grid_info"], h.Requests.Select(q => q.Uri.ToString()));
    }

    [Fact]
    public async Task Connected_asks_each_service_where_it_lives()
    {
        var h = new StubHandler(r => (HttpStatusCode.OK, "<gridinfo></gridinfo>"));
        var osgrid = Defaults with
        {
            Plan = OwnerPlan.Connected,
            Connected = new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org",
                "http://grid.osgrid.org/", "http://hg.osgrid.org/"),
        };
        var other = Defaults with
        {
            Plan = OwnerPlan.Connected,
            Connected = new ConnectedGrid(null, "Elsewhere", "http://10.0.0.9:8002/", "http://grid.example.org:8002/",
                "http://grid.example.org:8003/", "http://grid.example.org:8002/"),
        };

        foreach (var s in new[] { osgrid, other })
        {
            var c = new RobustClient(new HttpClient(h), () => s);
            await c.GetAccountAsync("Ann", "Lee", CancellationToken.None);
            await c.AuthenticateAsync(Guid.Parse(Pid), "x", CancellationToken.None);
            await c.GetMapRegionsAsync(CancellationToken.None);
        }
        await new RobustClient(new HttpClient(h), () => osgrid).IsReadyAsync(CancellationToken.None);

        Assert.Equal(
        [
            "http://users.osgrid.org/accounts", "http://login.osgrid.org/auth/plain", "http://grid.osgrid.org/grid",
            "http://grid.example.org:8003/accounts", "http://grid.example.org:8003/auth/plain", "http://grid.example.org:8003/grid",
            "http://login.osgrid.org/get_grid_info",
        ], h.Requests.Select(q => q.Uri.ToString()));
    }

    [Fact]
    public async Task Authenticate_sends_md5_hex_of_password()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK, "<ServerResponse><Result>Success</Result><Token>tok-1</Token></ServerResponse>"));

        Assert.True(await c.AuthenticateAsync(Guid.Parse(Pid), "secret", CancellationToken.None));

        var req = Assert.Single(h.Requests);
        Assert.Equal("/auth/plain", req.Uri.AbsolutePath); // AuthenticationServerPostHandler dispatches on the "plain" segment
        Assert.Equal(8003, req.Uri.Port);
        Assert.Equal("authenticate", req.Form("METHOD"));
        Assert.Equal(Pid, req.Form("PRINCIPAL"));
        Assert.Equal("5ebe2294ecd0e0f08eab7690d2a6ee69", req.Form("PASSWORD"));
    }

    [Fact]
    public async Task Authenticate_is_false_on_failure_reply_without_token()
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, "<ServerResponse><Result>Failure</Result></ServerResponse>"));

        Assert.False(await c.AuthenticateAsync(Guid.Parse(Pid), "nope", CancellationToken.None));
    }

    [Fact]
    public async Task GetAccount_parses_PrincipalID_and_UserLevel()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK,
            "<?xml version=\"1.0\"?><ServerResponse><result type=\"List\"><FirstName>Test</FirstName><LastName>Owner</LastName>" +
            "<Email></Email><PrincipalID>" + Pid + "</PrincipalID><ScopeID>00000000-0000-0000-0000-000000000000</ScopeID>" +
            "<Created>1700000000</Created><UserLevel>200</UserLevel><UserFlags>0</UserFlags><UserTitle></UserTitle>" +
            "<LocalToGrid>True</LocalToGrid></result></ServerResponse>"));

        var a = await c.GetAccountAsync("Test", "Owner", CancellationToken.None);

        Assert.NotNull(a);
        Assert.Equal(Guid.Parse(Pid), a.PrincipalId);
        Assert.Equal(200, a.UserLevel);
        Assert.Equal("Test", a.FirstName);
        Assert.Equal("Owner", a.LastName);
        Assert.Equal("", a.Email);
        var req = Assert.Single(h.Requests);
        Assert.Equal("/accounts", req.Uri.AbsolutePath);
        Assert.Equal("getaccount", req.Form("METHOD"));
        Assert.Equal("Test", req.Form("FirstName"));
        Assert.Equal("Owner", req.Form("LastName"));
    }

    [Fact]
    public async Task GetAccount_returns_null_on_result_null()
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, "<ServerResponse><result>null</result></ServerResponse>"));

        Assert.Null(await c.GetAccountAsync("No", "Body", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<gridinfo><gridname>DreamGrid</gridname></gridinfo>", true)]
    [InlineData(HttpStatusCode.OK, "<html>starting</html>", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<gridinfo></gridinfo>", false)]
    public async Task IsReady_requires_200_and_gridinfo(HttpStatusCode status, string body, bool expected)
    {
        var (c, h) = Make(r => (status, body));

        Assert.Equal(expected, await c.IsReadyAsync(CancellationToken.None));
        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/get_grid_info", req.Uri.AbsolutePath);
        Assert.Equal(8002, req.Uri.Port);
    }

    [Fact]
    public async Task GetMapRegionsAsync_reads_get_region_range()
    {
        var id = Guid.NewGuid();
        var (c, h) = Make(r => (HttpStatusCode.OK,
            "<?xml version=\"1.0\"?><ServerResponse><region0 type=\"List\"><uuid>" + id + "</uuid><locX>256000</locX><locY>256256</locY>" +
            "<sizeX>512</sizeX><sizeY>512</sizeY><regionName>Hub</regionName></region0></ServerResponse>"));

        var regions = await c.GetMapRegionsAsync(CancellationToken.None);

        Assert.Equal([new MapRegion(id, "Hub", 1000, 1001, 2)], regions);
        var req = Assert.Single(h.Requests);
        Assert.Equal("/grid", req.Uri.AbsolutePath);
        Assert.Equal("get_region_range", req.Form("METHOD"));
        Assert.Equal("00000000-0000-0000-0000-000000000000", req.Form("SCOPEID"));
        Assert.Equal("0", req.Form("XMIN"));
        Assert.Equal("16776960", req.Form("YMAX"));
    }

    [Theory]
    [InlineData("<ServerResponse>null</ServerResponse>")]
    [InlineData("<ServerResponse><result>null</result></ServerResponse>")]
    [InlineData("")]
    public async Task GetMapRegionsAsync_is_empty_for_a_null_reply(string body)
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, body));

        Assert.Empty(await c.GetMapRegionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetMapRegionsAsync_in_an_area_asks_for_that_area()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK, "<ServerResponse>null</ServerResponse>"));

        // Robust filters by region origin, so the query reaches 16 squares (the largest region) west and south.
        await c.GetMapRegionsAsync(new MapArea(1000, 2000, 1001, 2003), CancellationToken.None);
        await c.GetMapRegionsAsync(CancellationToken.None);

        Assert.Equal(2, h.Requests.Count);
        var area = h.Requests[0];
        Assert.Equal("/grid", area.Uri.AbsolutePath);
        Assert.Equal("get_region_range", area.Form("METHOD"));
        Assert.Equal("251904", area.Form("XMIN"));
        Assert.Equal("507904", area.Form("YMIN"));
        Assert.Equal("256511", area.Form("XMAX"));
        Assert.Equal("513023", area.Form("YMAX"));
        Assert.Equal("0", h.Requests[1].Form("XMIN"));
        Assert.Equal("16776960", h.Requests[1].Form("XMAX"));
    }

    [Fact]
    public async Task GetMapRegionsAsync_in_an_area_at_the_map_edge_starts_at_zero()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK, "<ServerResponse>null</ServerResponse>"));

        await c.GetMapRegionsAsync(new MapArea(0, 15, 3, 20), CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal("0", req.Form("XMIN"));
        Assert.Equal("0", req.Form("YMIN"));
        Assert.Equal("1023", req.Form("XMAX"));
        Assert.Equal("5375", req.Form("YMAX"));
    }

    private static string AccountReply(string principal = Pid, string? level = "<UserLevel>200</UserLevel>") =>
        "<?xml version=\"1.0\"?><ServerResponse><result type=\"List\"><FirstName>Test</FirstName><LastName>Owner</LastName>" +
        "<Email></Email><PrincipalID>" + principal + "</PrincipalID>" + level + "</result></ServerResponse>";

    [Fact]
    public async Task FindAccount_parses_an_account()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK, AccountReply()));

        var a = await c.FindAccountAsync("Test", "Owner", CancellationToken.None);

        Assert.Equal(new RobustAccount(Guid.Parse(Pid), "Test", "Owner", 200, ""), a);
        var req = Assert.Single(h.Requests);
        Assert.Equal("/accounts", req.Uri.AbsolutePath);
        Assert.Equal("getaccount", req.Form("METHOD"));
    }

    [Fact]
    public async Task FindAccount_returns_null_only_for_no_such_account()
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, "<?xml version=\"1.0\"?><ServerResponse><result>null</result></ServerResponse>"));

        Assert.Null(await c.FindAccountAsync("Nobody", "Here", CancellationToken.None));
    }

    [Theory]
    [InlineData("<ServerResponse><Result>Failure</Result><Message>Database error</Message></ServerResponse>")] // Robust's error envelope
    [InlineData("<html><body>Bad gateway</body>")] // not XML (a proxy page)
    [InlineData("")] // empty body
    [InlineData("<ServerResponse></ServerResponse>")] // no result
    [InlineData("<ServerResponse><result></result></ServerResponse>")] // an empty result
    [InlineData("<ServerResponse><result>Failure</result></ServerResponse>")] // a result that is neither null nor an account
    public async Task FindAccount_throws_on_a_reply_that_is_not_an_answer(string body)
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, body));

        await Assert.ThrowsAsync<InvalidDataException>(() => c.FindAccountAsync("Test", "Owner", CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-guid", "<UserLevel>200</UserLevel>")]
    [InlineData(Pid, null)] // no UserLevel
    [InlineData(Pid, "<UserLevel></UserLevel>")]
    [InlineData(Pid, "<UserLevel>lots</UserLevel>")]
    public async Task FindAccount_throws_on_an_unreadable_account(string principal, string? level)
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, AccountReply(principal, level)));

        await Assert.ThrowsAsync<InvalidDataException>(() => c.FindAccountAsync("Test", "Owner", CancellationToken.None));
    }

    [Fact]
    public async Task FindAccount_reads_a_negative_level()
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, AccountReply(level: "<UserLevel>-1</UserLevel>")));

        Assert.Equal(-1, (await c.FindAccountAsync("Test", "Owner", CancellationToken.None))!.UserLevel);
    }

    [Fact]
    public async Task GetAccount_still_reads_an_unusable_reply_as_no_account()
    {
        var (c, _) = Make(r => (HttpStatusCode.OK, "<ServerResponse><Result>Failure</Result></ServerResponse>"));

        Assert.Null(await c.GetAccountAsync("Test", "Owner", CancellationToken.None));
    }
}
