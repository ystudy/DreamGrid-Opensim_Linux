using System.Net;
using DreamGrid.Launcher.Dashboard;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The dashboard's Core services checks (design 2026-11-05-dashboard).</summary>
public sealed class ServiceChecksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each request from a table by path; records every request; can make time pass.</summary>
    private sealed class Handler(FakeTimeProvider time) : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Answers { get; } = new();
        public List<HttpRequestMessage> Seen { get; } = [];
        public TimeSpan Takes { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            time.Advance(Takes);
            var key = request.RequestUri!.AbsolutePath;
            return Answers.TryGetValue(key, out var answer)
                ? Task.FromResult(answer())
                : throw new HttpRequestException("Connection refused");
        }
    }

    private sealed class Clients(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static (ServiceChecks Checks, Handler Handler) Rig(bool hypergrid = true)
    {
        var time = new FakeTimeProvider();
        var handler = new Handler(time);
        var s = TestSettings.Create() with { Plan = DreamGrid.Launcher.Core.Settings.OwnerPlan.SimplePublic, Hostname = "grid.example.org", Hypergrid = hypergrid };
        return (new ServiceChecks(new Clients(handler), () => s, time), handler);
    }

    private static HttpResponseMessage Ok(string body = "") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static ServiceCheck Row(IReadOnlyList<ServiceCheck> rows, string name) => rows.Single(r => r.Name == name);

    [Fact]
    public async Task Each_row_reads_its_answer()
    {
        var (checks, h) = Rig();
        h.Answers["/get_grid_info"] = () => Ok("<gridinfo><gridname>Avalon</gridname></gridinfo>");
        h.Answers["/helo"] = () => Ok();
        h.Answers["/CAPS/GetTexture/"] = () => new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };
        h.Answers["/assets/89556747-24cb-43ed-920b-47caed15465f/metadata"] = () => Ok("<?xml version=\"1.0\"?><AssetMetadata><FullID>x</FullID></AssetMetadata>");
        h.Answers["/accounts"] = () => Ok("<ServerResponse><result>null</result></ServerResponse>");

        var rows = await checks.CheckAsync(profilesRead: true, Ct);

        Assert.All(rows, r => Assert.Equal(ServiceState.Working, r.State));
        Assert.Equal(["Login", "Hypergrid", "Textures", "Assets", "Accounts", "Profiles"], rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_missing_texture_service_reads_check_this_and_no_answer_reads_down()
    {
        var (checks, h) = Rig();
        h.Answers["/CAPS/GetTexture/"] = () => new(HttpStatusCode.NotFound);

        var rows = await checks.CheckAsync(profilesRead: false, Ct);

        Assert.Equal(ServiceState.CheckThis, Row(rows, "Textures").State);
        Assert.Equal(ServiceState.Down, Row(rows, "Login").State);
        Assert.Null(Row(rows, "Login").Milliseconds);
        Assert.Equal(ServiceState.CheckThis, Row(rows, "Profiles").State);
    }

    [Fact]
    public async Task A_failing_account_service_or_missing_asset_service_reads_check_this()
    {
        var (checks, h) = Rig();
        h.Answers["/accounts"] = () => Ok("<ServerResponse><Result>Failure</Result></ServerResponse>");
        h.Answers["/assets/89556747-24cb-43ed-920b-47caed15465f/metadata"] = () => new(HttpStatusCode.NotFound);

        var rows = await checks.CheckAsync(true, Ct);

        Assert.Equal(ServiceState.CheckThis, Row(rows, "Accounts").State);
        Assert.Equal(ServiceState.CheckThis, Row(rows, "Assets").State);
    }

    [Fact]
    public async Task A_body_cut_off_mid_read_reads_down_not_an_error()
    {
        var (checks, h) = Rig();
        h.Answers["/get_grid_info"] = () => new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };

        var rows = await checks.CheckAsync(true, Ct);

        Assert.Equal(ServiceState.Down, Row(rows, "Login").State);
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("reset");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("reset");
    }

    [Fact]
    public async Task An_answer_over_a_second_reads_slow()
    {
        var (checks, h) = Rig(hypergrid: false);
        h.Answers["/get_grid_info"] = () => Ok("<gridinfo/>");
        h.Takes = TimeSpan.FromMilliseconds(1500);

        var rows = await checks.CheckAsync(true, Ct);

        Assert.Equal(ServiceState.Slow, Row(rows, "Login").State);
        Assert.Equal(1500, Row(rows, "Login").Milliseconds);
        Assert.DoesNotContain(rows, r => r.Name == "Hypergrid");
    }

    [Fact]
    public async Task Every_request_goes_to_this_computer_only_and_the_texture_one_asks_for_a_kilobyte()
    {
        var (checks, h) = Rig();
        await checks.CheckAsync(true, Ct);

        Assert.NotEmpty(h.Seen);
        Assert.All(h.Seen, r => Assert.Equal("127.0.0.1", r.RequestUri!.Host));
        var texture = h.Seen.Single(r => r.RequestUri!.AbsolutePath == "/CAPS/GetTexture/");
        Assert.Equal("bytes=0-1023", texture.Headers.Range!.ToString());
    }
}
