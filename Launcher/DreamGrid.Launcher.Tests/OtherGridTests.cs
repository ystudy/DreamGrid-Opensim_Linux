using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Plans;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class OtherGridTests
{
    private static string RepoBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("DreamGrid.sln not found"), "bin");
    }

    private static readonly Uri Address = new("http://login.osgrid.org/");

    [Fact]
    public void Osgrid_preset_matches_the_shipped_proto()
    {
        var ini = IniDocument.Load(Path.Combine(RepoBin(), "config-include", "Proto", "OsGrid", "GridCommon.ini"));
        var preset = Assert.Single(GridPresets.All);

        Assert.Same(preset, GridPresets.ById("osgrid"));
        Assert.Equal(ini.Get("UserAccountService", "UserAccountServerURI")!.Trim('"'), preset.AccountsUri);
        Assert.Equal(ini.Get("AuthenticationService", "AuthenticationServerURI")!.Trim('"'), preset.AuthenticationUri);
        Assert.Equal(ini.Get("GridService", "GridServerURI")!.Trim('"'), preset.GridUri);
    }

    [Fact]
    public void ById_knows_no_other_preset()
    {
        Assert.Null(GridPresets.ById("other"));
        Assert.Null(GridPresets.ById(null));
    }

    [Fact]
    public void Parse_reads_osgrid() =>
        Assert.Equal(new OtherGridInfo("OSGrid", "http://login.osgrid.org", "http://hg.osgrid.org/", false),
            OtherGridInfoParser.Parse(Address,
                "<gridinfo><login>http://login.osgrid.org</login><gridname>OSGrid</gridname><gatekeeper>http://hg.osgrid.org/</gatekeeper></gridinfo>"));

    [Fact]
    public void Parse_without_gatekeeper_has_no_hypergrid()
    {
        var info = OtherGridInfoParser.Parse(Address, "<gridinfo><login>http://login.osgrid.org</login><gridname>OSGrid</gridname></gridinfo>");
        Assert.Null(info.GatekeeperUri);
    }

    [Fact]
    public void Parse_falls_back_to_the_host_and_the_address()
    {
        var info = OtherGridInfoParser.Parse(Address, "<gridinfo></gridinfo>");
        Assert.Equal("login.osgrid.org", info.GridName);
        Assert.Equal("http://login.osgrid.org/", info.LoginUri);
    }

    [Fact]
    public void Parse_flags_a_grid_asset_server()
    {
        var info = OtherGridInfoParser.Parse(Address, "<gridinfo><gridname>Avalon</gridname><dreamgrid_plan>grid-asset</dreamgrid_plan></gridinfo>");
        Assert.True(info.IsGridAssetServer);
    }

    [Fact]
    public void Parse_refuses_a_web_page()
    {
        var e = Assert.Throws<GridJoinException>(() => OtherGridInfoParser.Parse(Address, "<html><body>Hello</body></html>"));
        Assert.Contains("not its website", e.Message);
    }

    [Theory]
    [InlineData("grid.example.org", 8002, "http://grid.example.org:8002/")]
    [InlineData(" http://grid.example.org:9000/ ", 8002, "http://grid.example.org:9000/")]
    [InlineData("https://grid.example.org", 8003, "https://grid.example.org:8003/")]
    public void OtherGridAddress_parses(string typed, int defaultPort, string expected)
    {
        Assert.Equal(expected, OtherGridAddress.Parse(typed, defaultPort, out var error)?.AbsoluteUri);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("grid.example.org:")]
    [InlineData("grid.example.org:70000")]
    [InlineData("a b")]
    [InlineData("ftp://x")]
    public void OtherGridAddress_refuses(string typed)
    {
        Assert.Null(OtherGridAddress.Parse(typed, OtherGridAddress.LoginPort, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain("Grid Asset Server", error);
    }

    [Fact]
    public async Task ReadAsync_asks_get_grid_info()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Answer(r =>
        {
            seen = r;
            return Reply("<gridinfo><gridname>OSGrid</gridname></gridinfo>");
        }));

        var info = await new OtherGridClient(http).ReadAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal("OSGrid", info.GridName);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("http://login.osgrid.org/get_grid_info", seen.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ReadAsync_marks_nothing_answering_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => throw new HttpRequestException("Connection refused")));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new OtherGridClient(http).ReadAsync(Address, TestContext.Current.CancellationToken));

        Assert.True(e.NoAnswer);
        Assert.Contains("Nothing answered at http://login.osgrid.org/", e.Message);
    }

    [Fact]
    public async Task CheckServicesAsync_accepts_a_grid_service_and_refuses_html()
    {
        var services = new Uri("http://grid.example.org:8003/");
        HttpRequestMessage? seen = null;
        string? body = null;
        using var grid = new HttpClient(new Answer(r =>
        {
            seen = r;
            body = r.Content!.ReadAsStringAsync().Result;
            return Reply("<ServerResponse/>");
        }));
        await new OtherGridClient(grid).CheckServicesAsync(services, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("http://grid.example.org:8003/grid", seen.RequestUri!.AbsoluteUri);
        Assert.Contains("METHOD=get_region_range", body);
        Assert.Contains("XMIN=256000", body);

        using var page = new HttpClient(new Answer(_ => Reply("<html/>")));
        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new OtherGridClient(page).CheckServicesAsync(services, TestContext.Current.CancellationToken));
        Assert.Contains("not like a grid's services", e.Message);
        Assert.False(e.NoAnswer);
    }

    [Fact]
    public async Task CheckServicesAsync_marks_nothing_answering_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => throw new HttpRequestException("Connection refused")));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new OtherGridClient(http).CheckServicesAsync(new Uri("http://grid.example.org:8003/"), TestContext.Current.CancellationToken));

        Assert.True(e.NoAnswer);
    }

    private static HttpResponseMessage Reply(string xml) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(xml) };

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
