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

        var req = h.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/StartSession/", req.Uri.AbsolutePath);
        Assert.Equal("dg", req.Form("USER"));
        Assert.Equal("pw secret", req.Form("PASS"));
    }

    [Fact]
    public async Task RunAsync_returns_output_and_skips_input_echo_and_command_prompt_lines()
    {
        var reads = 0;
        var (c, h) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/") return (HttpStatusCode.OK, StartOk);
            if (r.Uri.AbsolutePath == "/SessionCommand/") return (HttpStatusCode.OK, CmdOk);
            // First read is the scrollback sent to a new session; second carries the output.
            // Real server: command prompt = Command="true"; echo of typed text = Input="true".
            return (HttpStatusCode.OK, Interlocked.Increment(ref reads) switch
            {
                1 => Lines(("old scrollback", "false", "false", "false")),
                2 => Lines(
                    ("show users", "false", "false", "true"),
                    ("alpha & beta", "false", "false", "false"),
                    ("gamma", "false", "false", "false"),
                    ("Are you sure? [y/n]: ", "true", "false", "false"),
                    ("Robust# ", "true", "true", "false")),
                _ => Empty,
            });
        });

        var lines = await c.RunAsync("show users", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal(new[] { "alpha & beta", "gamma", "Are you sure? [y/n]: " }, lines);
        var cmd = Assert.Single(h.Requests, r => r.Uri.AbsolutePath == "/SessionCommand/");
        Assert.Equal(Sid, cmd.Form("ID"));
        Assert.Equal("show users", cmd.Form("COMMAND"));
        Assert.Contains(h.Requests, r => r.Uri.AbsolutePath == "/ReadResponses/" + Sid + "/");
    }

    [Fact]
    public async Task RunLinesAsync_marks_only_interactive_prompts_case_insensitively()
    {
        var reads = 0;
        var (c, _) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/") return (HttpStatusCode.OK, StartOk);
            if (r.Uri.AbsolutePath == "/SessionCommand/") return (HttpStatusCode.OK, CmdOk);
            return (HttpStatusCode.OK, Interlocked.Increment(ref reads) switch
            {
                1 => Empty,
                2 => Lines(
                    ("Grid name:", "false", "false", "false"),   // ordinary output ending in ':'
                    ("Model name []: ", "True", "False", "false"), // interactive prompt
                    ("Robust# ", "true", "true", "false")),      // command prompt: dropped
                _ => Empty,
            });
        });

        var lines = await c.RunLinesAsync("x", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal(new[] { new ConsoleOutput("Grid name:", false), new ConsoleOutput("Model name []: ", true) }, lines);
    }

    private static string Lines(params (string Text, string Prompt, string Command, string Input)[] lines) =>
        "<ConsoleSession>" + string.Concat(lines.Select((l, i) =>
            $"<Line Number=\"{i + 1}\" Level=\"normal\" Prompt=\"{l.Prompt}\" Command=\"{l.Command}\" Input=\"{l.Input}\">{System.Security.SecurityElement.Escape(l.Text)}</Line>")) +
        "</ConsoleSession>";

    [Fact]
    public async Task IsUpAsync_then_RunAsync_does_not_return_scrollback_and_IsUp_closes_its_probe_session()
    {
        var reads = 0;
        var (c, h) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/") return (HttpStatusCode.OK, StartOk);
            if (r.Uri.AbsolutePath == "/SessionCommand/" || r.Uri.AbsolutePath == "/CloseSession/") return (HttpStatusCode.OK, CmdOk);
            return (HttpStatusCode.OK, Interlocked.Increment(ref reads) switch
            {
                1 => Lines(("old scrollback", "false", "false", "false")),
                2 => Lines(("fresh", "false", "false", "false")),
                _ => Empty,
            });
        });

        Assert.True(await c.IsUpAsync(CancellationToken.None));
        Assert.Contains(h.Requests, r => r.Uri.AbsolutePath == "/CloseSession/" && r.Form("ID") == Sid);

        var lines = await c.RunAsync("x", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(new[] { "fresh" }, lines);
    }

    [Fact]
    public async Task Restart_after_expired_session_inside_RunAsync_also_drains_scrollback()
    {
        var starts = 0;
        var commandsSeen = 0;
        var readsPerSession = new Dictionary<string, int>();
        var (c, _) = Make(r =>
        {
            if (r.Uri.AbsolutePath == "/StartSession/")
            {
                var n = ++starts;
                return (HttpStatusCode.OK, StartOk.Replace(Sid, "sid-" + n));
            }
            if (r.Uri.AbsolutePath == "/SessionCommand/")
                return ++commandsSeen == 1 ? (HttpStatusCode.NotFound, "") : (HttpStatusCode.OK, CmdOk);
            var sid = r.Uri.AbsolutePath.Split('/')[2];
            readsPerSession[sid] = readsPerSession.GetValueOrDefault(sid) + 1;
            return (HttpStatusCode.OK, readsPerSession[sid] switch
            {
                1 => Lines(("old scrollback", "false", "false", "false")),
                2 => Lines(("new output", "false", "false", "false")),
                _ => Empty,
            });
        });

        var lines = await c.RunAsync("x", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(2, starts);
        Assert.Equal(new[] { "new output" }, lines);
    }

    [Fact]
    public async Task RunAsync_does_not_wait_for_the_servers_long_poll()
    {
        var h = new DelayingReadHandler();
        var c = new RestConsoleClient(new HttpClient(h), Base, "dg", "pw");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var lines = await c.RunAsync("x", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
        Assert.Empty(lines);
    }

    /// <summary>Answers StartSession/SessionCommand at once but holds ReadResponses for 30 s, like the real long-poll.</summary>
    private sealed class DelayingReadHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/ReadResponses/")) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            var body = path == "/StartSession/" ? StartOk : path == "/SessionCommand/" ? CmdOk : Empty;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task IsUpAsync_lets_ConsoleAuthException_propagate()
    {
        var (c, _) = Make(r => (HttpStatusCode.Unauthorized, ""));

        await Assert.ThrowsAsync<ConsoleAuthException>(() => c.IsUpAsync(CancellationToken.None));
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
