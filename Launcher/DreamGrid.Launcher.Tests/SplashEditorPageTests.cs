using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using Xunit;
using static DreamGrid.Launcher.Tests.SplashPictureApiTests;

namespace DreamGrid.Launcher.Tests;

/// <summary>The splash page editor at /splash (plan 12c Task 2): who reaches it, and the markup its script relies on.</summary>
public sealed class SplashEditorPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HtmlParser Parser = new();

    private static LauncherFactory RegionServer() => Factory(settings: s => s with
    {
        Plan = OwnerPlan.RegionServer,
        Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
        Hypergrid = false,
    });

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task Owner_and_admin_open_the_editor(string role)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);

        using var r = await client.GetAsync("/splash", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["Text", "Picture", "Grid name and logo", "Live status", "News", "Places to go", "Links"],
            page.QuerySelectorAll("#splash-shelf .shelf-item").Select(b => b.TextContent.Trim()));
        Assert.Equal(Enum.GetNames<SplashBlockKind>(), page.QuerySelectorAll("#splash-shelf .shelf-item").Select(b => b.GetAttribute("data-kind")));
        Assert.Equal(SplashDefaults.Design().Blocks.Count, page.QuerySelectorAll("#canvas-page [data-block-id]").Length);
        var editor = page.QuerySelector("#splash-editor")!;
        Assert.Equal("/api/splash", editor.GetAttribute("data-api"));
        Assert.Equal("/splash?handler=Blocks", editor.GetAttribute("data-blocks"));
        Assert.NotNull(page.QuerySelector("#splash-editor input[name=__RequestVerificationToken]"));
        Assert.NotNull(page.QuerySelector("link#draft-css[href^=\"/api/splash/draft.css?v=\"]"));
        foreach (var id in new[] { "undo", "redo", "forward", "back", "duplicate", "delete", "snap", "snap-size", "save", "publish", "discard", "preview-off", "preview-tab", "save-state", "splash-settings" })
            Assert.True(page.GetElementById(id) is not null, "missing #" + id);
        Assert.Equal(["1024x640", "1280x800", "1920x1080"],
            page.QuerySelectorAll("button.preview-size").Select(b => b.GetAttribute("data-w") + "x" + b.GetAttribute("data-h")));
        Assert.Equal(["8", "16", "32", "64"], page.QuerySelectorAll("#snap-size option").Select(o => o.GetAttribute("value")));
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Supervisor_and_anonymous_are_refused(string? role)
    {
        await using var f = Factory();
        var client = role is null ? Client(f) : await StaffAsync(f, role);

        using var r = await client.GetAsync("/splash", Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Editor_is_404_on_a_region_server()
    {
        await using var f = RegionServer();
        var client = await StaffAsync(f, "owner");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/splash", Ct)).StatusCode);
    }

    [Fact]
    public async Task Blocks_handler_returns_the_draft_blocks()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var design = new SplashDesign(1280, 800, new SplashBackground("#223344", null, 50, 50, 0), false, 16,
        [
            new SplashBlock(1, SplashBlockKind.Text, 40, 40, 600, 80, 1, "#000000", 50, null, false, 8,
                new SplashFont(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans),
                new SplashContent(Text: new Dictionary<string, string> { ["en"] = "Hello there" })),
        ]);
        using (var put = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=0", await TokenAsync(client), JsonContent.Create(design, options: Json)))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var r = await client.GetAsync("/splash?handler=Blocks", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        var html = await r.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Hello there", html);
        Assert.Contains("data-block-id=\"1\"", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Editor_scripts_are_files()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var page = Parser.ParseDocument(await client.GetStringAsync("/splash", Ct));

        var scripts = page.QuerySelectorAll("script").Select(s => s.GetAttribute("src")).ToList();
        Assert.All(scripts, s => Assert.False(string.IsNullOrEmpty(s)));
        Assert.Contains("/js/splash-editor-core.js", scripts);
        Assert.Contains("/js/splash-editor.js", scripts);
        foreach (var file in new[] { "/js/splash-editor-core.js", "/js/splash-editor.js", "/css/splash-editor.css" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(file, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("supervisor", false)]
    public async Task Menu_has_splash_page_for_admins(string role, bool shown)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);
        var page = Parser.ParseDocument(await client.GetStringAsync("/profile/sessions", Ct));

        var link = page.QuerySelector("header nav a[href=\"/splash\"]");
        Assert.Equal(shown, link is not null);
        if (shown) Assert.Equal("Splash page", link!.TextContent.Trim());
    }

    [Fact]
    public async Task Region_server_menu_has_no_splash_page()
    {
        await using var f = RegionServer();
        var client = await StaffAsync(f, "owner");
        var page = Parser.ParseDocument(await client.GetStringAsync("/profile/sessions", Ct));

        Assert.Null(page.QuerySelector("header nav a[href=\"/splash\"]"));
    }
}
