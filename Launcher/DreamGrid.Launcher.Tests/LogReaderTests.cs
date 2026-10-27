using System.Net;
using System.Text.Json;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LogReaderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-logs-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private LauncherSettings _settings = new()
    {
        Db = new("r", "b", "o"),
        Console = new("u", "p"),
        MachineId = "m",
    };

    public LogReaderTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        Directory.CreateDirectory(_paths.Logs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private LogReader Reader() => new(_paths, () => _settings);

    private string LogFile(string name) => Path.Combine(_paths.Logs, name);

    [Fact]
    public void Tail_returns_last_n_lines_of_large_file()
    {
        // About 5 MB: 100 000 lines of ~50 bytes.
        using (var w = new StreamWriter(LogFile("robust.log")))
            for (var i = 0; i < 100_000; i++)
                w.WriteLine($"12:00:00 - line {i:D6} " + new string('x', 30));

        var tail = Reader().Tail("robust", lines: 50);

        Assert.Equal(50, tail.Lines.Count);
        Assert.Equal(Enumerable.Range(99_950, 50).Select(i => $"12:00:00 - line {i:D6} " + new string('x', 30)), tail.Lines);
        Assert.True(tail.Truncated);
        Assert.Equal("robust", tail.Source);
    }

    [Fact]
    public void Tail_of_a_short_file_returns_everything_untruncated()
    {
        File.WriteAllText(LogFile("robust.log"), "a\r\nb\nc\n");

        var tail = Reader().Tail("robust", lines: 200);

        Assert.Equal(["a", "b", "c"], tail.Lines);
        Assert.False(tail.Truncated);
    }

    [Fact]
    public void Tail_reads_while_another_writer_holds_the_file()
    {
        using var writer = new FileStream(LogFile("robust.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("one\ntwo\n"u8);
        writer.Flush();

        var tail = Reader().Tail("robust");

        Assert.Equal(["one", "two"], tail.Lines);
    }

    [Fact]
    public void A_missing_log_file_is_an_empty_tail()
    {
        var tail = Reader().Tail("database");

        Assert.Empty(tail.Lines);
        Assert.False(tail.Truncated);
    }

    [Fact]
    public void Level_filter_keeps_warn_and_error_only()
    {
        File.WriteAllLines(LogFile("robust.log"),
        [
            "12:00:00 - [DEBUG] d",
            "12:00:01 - [INFO] i",
            "12:00:02 - [WARN] w",
            "12:00:03 - [ERROR] e",
        ]);

        var tail = Reader().Tail("robust", minLevel: "WARN");

        Assert.Equal(["12:00:02 - [WARN] w", "12:00:03 - [ERROR] e"], tail.Lines);
    }

    [Theory]
    [InlineData("DEBUG", 5)]
    [InlineData("INFO", 4)]
    [InlineData("WARN", 3)]
    [InlineData("ERROR", 2)]
    public void Level_filter_works_on_every_opensim_level(string min, int expected)
    {
        File.WriteAllLines(LogFile("robust.log"),
        [
            "12:00:00 - [DEBUG] d", "12:00:01 - [INFO] i", "12:00:02 - [WARN] w", "12:00:03 - [ERROR] e", "12:00:04 - [FATAL] f",
        ]);

        var tail = Reader().Tail("robust", minLevel: min);

        Assert.Equal(expected, tail.Lines.Count);
        Assert.Equal("12:00:04 - [FATAL] f", tail.Lines[^1]);
    }

    [Fact]
    public void An_error_token_inside_the_message_does_not_count_as_the_lines_level()
    {
        File.WriteAllLines(LogFile("robust.log"),
        [
            "12:00:00 - [INFO] chat says [ERROR] hello",
            "12:00:01 - [ERROR] real",
        ]);

        var tail = Reader().Tail("robust", minLevel: "ERROR");

        Assert.Equal(["12:00:01 - [ERROR] real"], tail.Lines);
    }

    [Fact]
    public void Level_filter_keeps_lines_without_a_level_and_knows_the_launchers_own_levels()
    {
        File.WriteAllLines(LogFile("launcher.log"),
        [
            "2026-10-25 10:00:00.000Z info Cat: fine",
            "2026-10-25 10:00:01.000Z fail Cat: broke",
            "   at Stack.Frame()",
        ]);

        var tail = Reader().Tail("launcher", minLevel: "ERROR");

        Assert.Equal(["2026-10-25 10:00:01.000Z fail Cat: broke", "   at Stack.Frame()"], tail.Lines);
    }

    [Fact]
    public void Level_filter_reads_further_back_to_find_enough_matching_lines()
    {
        using (var w = new StreamWriter(LogFile("robust.log")))
        {
            w.WriteLine("12:00:00 - [ERROR] old error");
            for (var i = 0; i < 20_000; i++) w.WriteLine($"12:00:00 - [INFO] filler {i}");
        }

        var tail = Reader().Tail("robust", lines: 5, minLevel: "ERROR");

        Assert.Equal(["12:00:00 - [ERROR] old error"], tail.Lines);
    }

    [Fact]
    public void Sources_lists_region_only_when_a_region_exists()
    {
        Assert.Equal(["launcher", "robust", "database"], Reader().Sources());

        _settings = _settings with { Programs = TestSettings.ProgramFor(new RegionSettings("r1", "Home", Guid.Empty)), Regions = [new RegionSettings("r1", "Home", Guid.NewGuid())] };

        Assert.Equal(["launcher", "robust", "region-r1", "database"], Reader().Sources());
    }

    [Fact]
    public void Tail_of_an_unknown_source_throws()
    {
        Assert.Throws<ArgumentException>(() => Reader().Tail("../settings"));
    }

    // ---- GET /api/logs ----

    private static LauncherFactory Factory() => new(s => s with { SetupToken = null, Setup = s.Setup with { OwnerCreated = true, Finished = true } })
    {
        Before = TestAuth.Apply,
    };

    private static async Task<HttpClient> SignedIn(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    [Fact]
    public async Task Api_returns_the_tail_as_json()
    {
        await using var f = Factory();
        Directory.CreateDirectory(f.Paths.Logs);
        File.WriteAllText(Path.Combine(f.Paths.Logs, "robust.log"), "a\n[WARN] b\nc\n");
        using var client = await SignedIn(f);

        using var resp = await client.GetAsync("/api/logs?source=robust&lines=2", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal("robust", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal(["[WARN] b", "c"], doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("../settings")]
    [InlineData("..%2Fsettings")]
    [InlineData("robust.log")]
    public async Task Unknown_source_is_404_and_path_traversal_is_impossible(string source)
    {
        await using var f = Factory();
        using var client = await SignedIn(f);

        using var resp = await client.GetAsync("/api/logs?source=" + source, Ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_api_call_is_401()
    {
        await using var f = Factory();
        using var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var resp = await client.GetAsync("/api/logs?source=robust", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Logs_page_needs_sign_in_and_shows_the_picker_and_lines_when_signed_in()
    {
        await using var f = Factory();
        Directory.CreateDirectory(f.Paths.Logs);
        File.WriteAllText(Path.Combine(f.Paths.Logs, "launcher.log"), "hello <b>log</b>\n");

        using var anon = await f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/logs", Ct);
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);

        using var client = await SignedIn(f);
        var html = await client.GetStringAsync("/logs?source=launcher", Ct);

        Assert.Contains("<select", html);
        Assert.Contains("hello &lt;b&gt;log&lt;/b&gt;", html);
    }
}
