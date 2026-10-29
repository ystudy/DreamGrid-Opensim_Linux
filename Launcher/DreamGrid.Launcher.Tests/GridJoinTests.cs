using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class GridJoinTests
{
    private static string Xml(string plan = "grid-asset") =>
        "<gridinfo><gridname>Avalon</gridname><login>http://192.168.1.5:8002/</login><dreamgrid_version>7.1.0</dreamgrid_version>" +
        "<dreamgrid_private_port>8003</dreamgrid_private_port><dreamgrid_hypergrid>false</dreamgrid_hypergrid>" +
        $"<dreamgrid_region_ports>8004-8006</dreamgrid_region_ports><dreamgrid_plan>{plan}</dreamgrid_plan></gridinfo>";

    [Fact]
    public void Parse_reads_a_grid_asset_server() =>
        Assert.Equal(new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006),
            GridJoin.Parse("192.168.1.5", 8002, Xml()));

    [Fact]
    public void Parse_reads_empty_region_ports()
    {
        var xml = Xml().Replace("<dreamgrid_region_ports>8004-8006</dreamgrid_region_ports>", "<dreamgrid_region_ports></dreamgrid_region_ports>");
        var join = GridJoin.Parse("192.168.1.5", 8002, xml);
        Assert.Null(join.RegionPortsFrom);
        Assert.Null(join.RegionPortsTo);
    }

    [Fact]
    public void Parse_refuses_another_plan()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("192.168.1.5", 8002, Xml("simple-public")));
        Assert.Contains("Simple Public Server", e.Message);
        Assert.Contains("Grid Asset Server", e.Message);
    }

    [Fact]
    public void Parse_refuses_a_grid_without_dreamgrid_keys()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("grid.osgrid.org", 80, "<gridinfo><gridname>OSgrid</gridname></gridinfo>"));
        Assert.Contains("isn't a DreamGrid Grid Asset Server", e.Message);
    }

    [Fact]
    public void Parse_refuses_what_is_not_grid_info()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("192.168.1.5", 8002, "<html><body>Hello</body></html>"));
        Assert.Contains("didn't answer like a grid", e.Message);
    }

    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 8002)]
    [InlineData(" 192.168.1.5:9002 ", "192.168.1.5", 9002)]
    [InlineData("http://grid.example.org:8002/", "grid.example.org", 8002)]
    [InlineData("grid.example.org", "grid.example.org", 8002)]
    public void JoinAddress_parses(string typed, string host, int port)
    {
        Assert.Equal((host, port), JoinAddress.Parse(typed, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.5:")]
    [InlineData("192.168.1.5:70000")]
    [InlineData("a b")]
    [InlineData("ftp://x")]
    public void JoinAddress_refuses(string typed)
    {
        Assert.Null(JoinAddress.Parse(typed, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task Client_marks_nothing_answering_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => throw new HttpRequestException("Connection refused")));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new GridJoinClient(http).ReadAsync("192.168.1.5", 8002, TestContext.Current.CancellationToken));

        Assert.True(e.NoAnswer);
        Assert.Contains("Nothing answered", e.Message);
    }

    [Fact]
    public async Task Client_does_not_mark_another_plan_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Xml("local")) }));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new GridJoinClient(http).ReadAsync("192.168.1.5", 8002, TestContext.Current.CancellationToken));

        Assert.False(e.NoAnswer);
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
