using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LayoutTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly LauncherFactory _factory = new(s => s with { GridName = "Avalon" });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Layout_has_viewport_meta_and_no_external_urls()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/welcome", Ct);
        var doc = new HtmlParser().ParseDocument(html);

        Assert.Equal("width=device-width, initial-scale=1",
            doc.QuerySelector("meta[name=viewport]")?.GetAttribute("content"));
        Assert.NotNull(doc.QuerySelector("main"));
        Assert.NotNull(doc.QuerySelector("footer"));
        foreach (var el in doc.QuerySelectorAll("[src],[href]"))
        {
            var url = el.GetAttribute("src") ?? el.GetAttribute("href") ?? "";
            Assert.False(url.StartsWith("http", StringComparison.OrdinalIgnoreCase), $"external url: {url}");
        }
    }

    [Fact]
    public async Task Navigation_links_are_hidden_when_not_signed_in()
    {
        using var client = _factory.CreateClient();
        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/welcome", Ct));

        Assert.Empty(doc.QuerySelectorAll("header nav a"));
        Assert.Contains("Avalon", doc.QuerySelector("header")!.TextContent);
    }

    [Fact]
    public async Task Welcome_page_tells_the_person_how_to_log_in()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/welcome", Ct);

        Assert.Contains("Log in with your avatar name and password.", html);
    }

    [Fact]
    public async Task Static_css_is_served()
    {
        using var resp = await _factory.CreateClient().GetAsync("/css/site.css", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/css", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Static_live_script_is_served()
    {
        using var resp = await _factory.CreateClient().GetAsync("/js/live.js", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("function poll(", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Map_script_loads_nothing_external()
    {
        using var resp = await _factory.CreateClient().GetAsync("/js/map.js", Ct);
        var script = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("function startMap(", script);
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Launcher_log_file_is_written()
    {
        using var client = _factory.CreateClient();
        await client.GetAsync("/welcome", Ct);
        var file = Path.Combine(_factory.Paths.Logs, "launcher.log");

        for (var i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(50, Ct);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Rolling_log_keeps_the_newest_files_and_survives_concurrent_writers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new RollingFileLoggerProvider(Path.Combine(dir, "launcher.log"), maxBytes: 2000, files: 3))
            {
                var log = provider.CreateLogger("Test");
                Parallel.For(0, 200, i => log.LogInformation("line {Number} {Pad}", i, new string('x', 50)));
            }

            var names = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(["launcher.1.log", "launcher.2.log", "launcher.log"], names);
            var lines = names.SelectMany(n => File.ReadAllLines(Path.Combine(dir, n!))).ToArray();
            Assert.All(lines, l => Assert.Matches(@"Z info Test: line \d+ x{50}$", l));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Requests_do_not_write_aspnetcore_info_lines_to_the_launcher_log()
    {
        using var client = _factory.CreateClient();
        foreach (var url in new[] { "/welcome", "/waiting", "/api/status", "/" })
            (await client.GetAsync(url, Ct)).Dispose();
        var file = Path.Combine(_factory.Paths.Logs, "launcher.log");
        for (var i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(50, Ct);

        string text;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            text = await reader.ReadToEndAsync(Ct);

        Assert.DoesNotContain(" info Microsoft.AspNetCore", text);
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task Data_protection_keys_are_kept_in_the_data_directory()
    {
        await using var f = new LauncherFactory(s => s with { SetupToken = null, Setup = s.Setup with { OwnerCreated = true, Finished = true } });
        using var client = f.CreateClient();

        // The sign-in form carries an antiforgery token, which needs a key.
        (await client.GetAsync("/login", Ct)).EnsureSuccessStatusCode();

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(f.Paths.Data, "keys"), "key-*.xml"));
    }
}
