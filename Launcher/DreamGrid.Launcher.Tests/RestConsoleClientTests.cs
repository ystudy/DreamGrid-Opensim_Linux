using System.Net;
using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RestConsoleClientTests
{
    private const string Sid = "5f2c1e0a-1111-2222-3333-444455556666";
    private static readonly Uri Base = new("http://127.0.0.1:8010/");

    private const string StartOk =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><ConsoleSession><SessionID>" + Sid + "</SessionID><Prompt>Robust# </Prompt></ConsoleSession>";
    private const string CmdOk = "<ConsoleSession><Result>OK</Result></ConsoleSession>";
    private const string Empty = "<ConsoleSession></ConsoleSession>";

    private static (RestConsoleClient Client, StubHandler Handler) Make(
        Func<RecordedRequest, (HttpStatusCode, string)> reply)
    {
        var h = new StubHandler(reply);
        return (new RestConsoleClient(new HttpClient(h), Base, "dg", "pw secret"), h);
    }

    [Fact]
    public async Task StartSession_posts_USER_and_PASS_form_and_reads_SessionID()
    {
        var (c, h) = Make(r => (HttpStatusCode.OK, StartOk));

        Assert.True(await c.IsUpAsync(CancellationToken.None));

        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/StartSession/", req.Uri.AbsolutePath);
        Assert.Equal("dg", req.Form("USER"));
        Assert.Equal("pw secret", req.Form("PASS"));
    }

    [Fact]
    public async Task RunAsync_returns_line_texts_and_skips_echoed_command_lines()
    {
        var reads = 0;
        var (c, h) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/") return (HttpStatusCode.OK, StartOk);
            if (r.Uri.AbsolutePath == "/SessionCommand/") return (HttpStatusCode.OK, CmdOk);
            // First read is the scrollback sent to a new session; second carries the output.
            return (HttpStatusCode.OK, Interlocked.Increment(ref reads) switch
            {
                1 => "<ConsoleSession><Line Number=\"1\" Level=\"normal\" Prompt=\"False\" Command=\"False\" Input=\"False\">old scrollback</Line></ConsoleSession>",
                2 => "<ConsoleSession>" +
                     "<Line Number=\"2\" Level=\"normal\" Prompt=\"False\" Command=\"True\" Input=\"False\">show users</Line>" +
                     "<Line Number=\"3\" Level=\"normal\" Prompt=\"False\" Command=\"False\" Input=\"False\">alpha &amp; beta</Line>" +
                     "<Line Number=\"4\" Level=\"normal\" Prompt=\"False\" Command=\"False\" Input=\"False\">gamma</Line>" +
                     "</ConsoleSession>",
                _ => Empty,
            });
        });

        var lines = await c.RunAsync("show users", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal(new[] { "alpha & beta", "gamma" }, lines);
        var cmd = Assert.Single(h.Requests, r => r.Uri.AbsolutePath == "/SessionCommand/");
        Assert.Equal(Sid, cmd.Form("ID"));
        Assert.Equal("show users", cmd.Form("COMMAND"));
        Assert.Contains(h.Requests, r => r.Uri.AbsolutePath == "/ReadResponses/" + Sid + "/");
    }

    [Fact]
    public async Task Session_is_cached_and_restarted_once_when_it_expires()
    {
        var starts = 0;
        var expire = false;
        var (c, h) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/") { starts++; return (HttpStatusCode.OK, StartOk); }
            if (r.Uri.AbsolutePath == "/SessionCommand/")
                return expire ? (HttpStatusCode.NotFound, "") : (HttpStatusCode.OK, CmdOk);
            return (HttpStatusCode.OK, Empty);
        });

        await c.SendAsync("one", CancellationToken.None);
        await c.SendAsync("two", CancellationToken.None);
        Assert.Equal(1, starts);

        expire = true;
        var ex = await Record.ExceptionAsync(() => c.SendAsync("three", CancellationToken.None));
        Assert.NotNull(ex); // still 404 after the single retry
        Assert.Equal(2, starts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.OK, "<ConsoleSession><SessionID></SessionID></ConsoleSession>")]
    public async Task Bad_credentials_throw_ConsoleAuthException(HttpStatusCode status, string body)
    {
        var (c, _) = Make(r => (status, body));

        await Assert.ThrowsAsync<ConsoleAuthException>(() => c.RunAsync("x", TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task IsUpAsync_is_false_when_nothing_answers()
    {
        var c = new RestConsoleClient(new HttpClient(new ThrowingHandler()), Base, "u", "p");

        Assert.False(await c.IsUpAsync(CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("refused");
    }
}
