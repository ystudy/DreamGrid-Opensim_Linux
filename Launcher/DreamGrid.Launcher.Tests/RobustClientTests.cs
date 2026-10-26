using System.Net;
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
}
