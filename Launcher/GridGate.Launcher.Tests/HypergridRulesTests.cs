using System.Net;
using System.Net.Sockets;
using System.Text;
using GridGate.Launcher.Core.Hypergrid;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The address rules, map placement and link test of hypergrid links (spec 2026-11-05-hypergrid-links-estates-design).</summary>
public sealed class HypergridRulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("grid.example.org:8002", "http://grid.example.org:8002/", null)]
    [InlineData("  grid.example.org:8002:Welcome Island ", "http://grid.example.org:8002/", "Welcome Island")]
    [InlineData("grid.example.org:8002:Odd:Name", "http://grid.example.org:8002/", "Odd:Name")]
    [InlineData("http://grid.example.org:8002/", "http://grid.example.org:8002/", null)]
    [InlineData("http://grid.example.org:8002", "http://grid.example.org:8002/", null)]
    [InlineData("https://Grid.Example.org:8002/ Welcome   Island", "https://grid.example.org:8002/", "Welcome Island")]
    [InlineData("http://hg.example.org/", "http://hg.example.org/", null)]
    [InlineData("203.0.113.9:9000", "http://203.0.113.9:9000/", null)]
    public void Accepts_the_usual_forms(string typed, string url, string? region)
    {
        var a = HyperlinkAddress.Parse(typed, out var problem);
        Assert.Null(problem);
        Assert.NotNull(a);
        Assert.Equal(url, a.Url);
        Assert.Equal(region, a.Region);
    }

    [Theory]
    [InlineData("")]
    [InlineData("grid.example.org")]
    [InlineData("ftp://grid.example.org:21/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pw@grid.example.org:8002/")]
    [InlineData("http://grid.example.org:8002/path")]
    [InlineData("http://grid.example.org:8002/?x=1")]
    [InlineData("http://grid.example.org:8002/#top")]
    [InlineData("http://grid.example.org:99999/")]
    [InlineData("grid.example.org:port")]
    [InlineData("grid.example.org:8002:Say \"hi\"")]
    [InlineData("grid.example.org:8002:Line\nbreak")]
    public void Refuses_anything_else(string typed)
    {
        Assert.Null(HyperlinkAddress.Parse(typed, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Refuses_a_region_name_that_is_too_long()
    {
        Assert.Null(HyperlinkAddress.Parse("grid.example.org:8002:" + new string('a', HyperlinkAddress.MaxRegion + 1), out var problem));
        Assert.Contains("at most", problem);
    }

    [Fact]
    public void Knows_its_own_grid()
    {
        var a = HyperlinkAddress.Parse("MyGrid.example.org:8002", out _)!;
        Assert.True(a.IsSameGrid("http://mygrid.example.org", 8002, null));
        Assert.True(a.IsSameGrid(null, 8002, "mygrid.example.org"));
        Assert.False(a.IsSameGrid("http://mygrid.example.org", 9000, "mygrid.example.org"));
        Assert.False(a.IsSameGrid("http://other.example.org", 8002, "other.example.org"));
    }

    [Fact]
    public void A_new_link_goes_one_empty_row_above_the_grid()
    {
        MapSpace[] map = [new(1000, 1000, 1, 1, false), new(1001, 1000, 2, 2, false)];
        Assert.Equal((1000, 1003), HyperlinkPlace.Free(map));
    }

    [Fact]
    public void A_new_link_skips_cells_already_taken()
    {
        MapSpace[] map = [new(1000, 1000, 1, 1, false), new(1000, 1002, 1, 1, true), new(1001, 1002, 1, 1, true)];
        Assert.Equal((1002, 1002), HyperlinkPlace.Free(map));
        Assert.False(HyperlinkPlace.IsFree(map, 1001, 1002));
        Assert.True(HyperlinkPlace.IsFree(map, 1003, 1002));
    }

    [Fact]
    public void An_empty_map_puts_it_in_the_middle()
    {
        Assert.Equal((HyperlinkPlace.EmptyX, HyperlinkPlace.EmptyY), HyperlinkPlace.Free([]));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.9", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.9.9.9", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    public void Only_public_addresses_may_be_tested(string ip, bool allowed) =>
        Assert.Equal(allowed, GridProbe.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("<gridinfo><gridname>Other Grid</gridname><login>http://x/</login></gridinfo>", "Other Grid")]
    [InlineData("<gridinfo><login>http://x/</login></gridinfo>", null)]
    [InlineData("<html><body>hello</body></html>", null)]
    [InlineData("not xml", null)]
    [InlineData("<!DOCTYPE gridinfo [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><gridinfo><gridname>&x;</gridname></gridinfo>", null)]
    public void Reads_the_grid_name(string xml, string? name) => Assert.Equal(name, GridProbe.GridName(xml));

    [Fact]
    public async Task The_link_test_never_connects_to_this_computer()
    {
        await using var server = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nConnection: close\r\n\r\n<gridinfo><gridname>Local</gridname></gridinfo>");
        using var probe = new GridProbe();
        var result = await probe.CheckAsync(new Uri($"http://127.0.0.1:{server.Port}/"), Ct);
        Assert.False(result.Ok);
        Assert.Contains("private network", result.Message);
        Assert.False(server.Connected);
    }

    [Fact]
    public async Task The_link_test_reads_a_grid()
    {
        await using var server = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nConnection: close\r\n\r\n<gridinfo><gridname>Far Grid</gridname></gridinfo>");
        using var probe = new GridProbe(_ => true);
        var result = await probe.CheckAsync(new Uri($"http://127.0.0.1:{server.Port}/"), Ct);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("Far Grid", result.GridName);
        Assert.Equal("/get_grid_info", server.Path);
    }

    [Fact]
    public async Task The_link_test_does_not_follow_a_redirect()
    {
        await using var server = new OneShotServer("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var probe = new GridProbe(_ => true);
        var result = await probe.CheckAsync(new Uri($"http://127.0.0.1:{server.Port}/"), Ct);
        Assert.False(result.Ok);
        Assert.Contains("redirect", result.Message);
    }

    [Fact]
    public async Task The_link_test_stops_reading_a_huge_answer()
    {
        var body = "<gridinfo><gridname>" + new string('x', GridProbe.MaxBytes) + "</gridname></gridinfo>";
        await using var server = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nConnection: close\r\n\r\n" + body);
        using var probe = new GridProbe(_ => true);
        var result = await probe.CheckAsync(new Uri($"http://127.0.0.1:{server.Port}/"), Ct);
        Assert.False(result.Ok);
        Assert.Contains("KB", result.Message);
    }

    /// <summary>A loopback TCP server that answers one request with a fixed reply and remembers the path asked for.</summary>
    private sealed class OneShotServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;

        public OneShotServer(string reply)
        {
            _listener.Start();
            _serve = ServeAsync(reply);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool Connected { get; private set; }
        public string? Path { get; private set; }

        private async Task ServeAsync(string reply)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                Connected = true;
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var n = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, n);
                Path = request.Split(' ').ElementAtOrDefault(1);
                var bytes = Encoding.UTF8.GetBytes(reply);
                await stream.WriteAsync(bytes);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serve;
        }
    }
}
