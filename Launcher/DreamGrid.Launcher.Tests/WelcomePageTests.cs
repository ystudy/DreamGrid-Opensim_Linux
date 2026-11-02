using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The public viewer splash page at /welcome (spec 2026-11-01-viewer-splash-design §4.5–§4.7). All content is made up.</summary>
public sealed class WelcomePageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IPAddress Outsider = IPAddress.Parse("192.168.1.50");

    private static LauncherFactory Factory(Func<LauncherSettings, LauncherSettings>? more = null, IPAddress? caller = null) =>
        new(s => (more ?? (x => x))(s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }))
        {
            Before = app =>
            {
                if (caller is not null)
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = caller;
                        return next(ctx);
                    });
                TestAuth.Apply(app);
            },
        };

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static SplashFont Font => new(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    private static Dictionary<string, string> En(string text) => new() { ["en"] = text };

    private static SplashBlock Block(int id, SplashBlockKind kind, SplashContent content) =>
        new(id, kind, 40, 40 + id * 60, 600, 50, id, "#000000", 50, null, false, 8, Font, content);

    private static SplashDesign Design(params SplashBlock[] blocks) =>
        new(1280, 800, new SplashBackground("#223344", null, 50, 50, 0), false, 16, blocks);

    private static SplashDesign TextDesign(string text) => Design(Block(1, SplashBlockKind.Text, new(Text: En(text))));

    private static void Publish(LauncherFactory f, SplashDesign design)
    {
        var store = f.Services.GetRequiredService<SplashStore>();
        Assert.IsType<SplashSaveResult.Saved>(store.SaveDraft(design, store.Draft()?.Version ?? 0, "Test Owner"));
        Assert.NotNull(store.Publish("Test Owner"));
    }

    private static async Task<(HttpResponseMessage Response, AngleSharp.Html.Dom.IHtmlDocument Page, string Html)> GetAsync(LauncherFactory f, string url = "/welcome")
    {
        var r = await Client(f).GetAsync(url, Ct);
        var html = await r.Content.ReadAsStringAsync(Ct);
        return (r, new HtmlParser().ParseDocument(html), html);
    }

    [Fact]
    public async Task Welcome_shows_default_before_any_publish()
    {
        await using var f = Factory();

        var (r, page, html) = await GetAsync(f);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Avalon", page.Body!.TextContent);
        Assert.Contains("Online", page.Body.TextContent);
        Assert.NotNull(page.QuerySelector("link[href='/welcome/page-0.css']"));
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Welcome_shows_published_design()
    {
        await using var f = Factory();
        Publish(f, TextDesign("Party at Sandbox"));

        var (_, page, _) = await GetAsync(f);

        Assert.Contains("Party at Sandbox", page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("link[href='/welcome/page-1.css']"));
        Assert.NotNull(page.QuerySelector(".splash-page .splash-block.b-1"));
    }

    [Fact]
    public async Task Welcome_has_no_admin_data()
    {
        await using var f = Factory();
        f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Secret Person", Guid.NewGuid())], true);

        var (_, _, html) = await GetAsync(f);

        Assert.Contains("1 person online", html);
        Assert.DoesNotContain("Secret Person", html);
    }

    [Fact]
    public async Task Welcome_shows_unavailable_when_database_down()
    {
        await using var f = Factory();
        f.OnlineUsers.Result = OnlineList.Unavailable;
        f.Regions.Result = GridRegionList.Unavailable;

        var (r, page, _) = await GetAsync(f);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Status unavailable", page.Body!.TextContent);
    }

    [Fact]
    public async Task Regions_count_the_whole_grid()
    {
        await using var f = Factory();
        f.Regions.Result = new GridRegionList([new(Guid.NewGuid(), "A"), new(Guid.NewGuid(), "B"), new(Guid.NewGuid(), "C")], true);

        var (_, page, _) = await GetAsync(f);

        Assert.Contains("3 regions", page.Body!.TextContent);
    }

    [Fact]
    public async Task Invalid_published_file_shows_default()
    {
        await using var f = Factory();
        Publish(f, TextDesign("Should not show"));
        var file = Path.Combine(f.Paths.Splash, "published.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"x\": 40", "\"x\": -5"));

        var (_, page, _) = await GetAsync(f);

        Assert.DoesNotContain("Should not show", page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("link[href='/welcome/page-0.css']"));
        Assert.Single(f.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("published splash page"));
    }

    [Fact]
    public async Task Block_text_is_not_html()
    {
        await using var f = Factory();
        var news = new SplashNewsItem(new DateOnly(2026, 11, 1), En("<script>alert(1)</script><img src=x onerror=alert(1)>"), false);
        Publish(f, Design(Block(1, SplashBlockKind.News, new(Heading: En("<b>News</b>"), News: [news]))));

        var (_, page, _) = await GetAsync(f);

        Assert.Empty(page.QuerySelectorAll("script"));
        Assert.Empty(page.QuerySelectorAll("img"));
        Assert.Empty(page.QuerySelectorAll("b"));
        Assert.Contains("<script>alert(1)</script>", page.Body!.TextContent);
        Assert.Null(page.QuerySelector("[onerror]"));
    }

    [Fact]
    public async Task News_is_newest_first_and_hides_hidden_items()
    {
        await using var f = Factory();
        Publish(f, Design(Block(1, SplashBlockKind.News, new(News:
        [
            new SplashNewsItem(new DateOnly(2026, 10, 1), En("Older item"), false),
            new SplashNewsItem(new DateOnly(2026, 11, 1), En("Newer item"), false),
            new SplashNewsItem(new DateOnly(2026, 11, 2), En("Hidden item"), true),
        ]))));

        var (_, page, _) = await GetAsync(f);

        var text = page.Body!.TextContent;
        Assert.DoesNotContain("Hidden item", text);
        Assert.True(text.IndexOf("Newer item", StringComparison.Ordinal) < text.IndexOf("Older item", StringComparison.Ordinal));
        Assert.Contains("1 November 2026", text);
    }

    [Fact]
    public async Task Welcome_is_cached_for_a_minute()
    {
        await using var f = Factory();
        Publish(f, TextDesign("Design A"));
        var first = await GetAsync(f);
        Assert.Contains("Design A", first.Html);

        // Written straight to the store, so nothing drops the cached page.
        Publish(f, TextDesign("Design B"));
        var second = await GetAsync(f);
        Assert.Contains("Design A", second.Html);

        f.Time.Advance(TimeSpan.FromSeconds(61));
        var third = await GetAsync(f);
        Assert.Contains("Design B", third.Html);

        foreach (var r in new[] { first.Response, second.Response, third.Response })
        {
            Assert.True(r.Headers.CacheControl?.Public);
            Assert.Equal(TimeSpan.FromSeconds(60), r.Headers.CacheControl?.MaxAge);
        }
    }

    [Fact]
    public async Task Stylesheet_route_serves_current_version_only()
    {
        await using var f = Factory();
        Publish(f, TextDesign("Styled"));

        using var current = await Client(f).GetAsync("/welcome/page-1.css", Ct);
        using var other = await Client(f).GetAsync("/welcome/page-2.css", Ct);

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("text/css", current.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".b-1{", (await current.Content.ReadAsStringAsync(Ct)).Replace(" ", ""));
        Assert.Equal(TimeSpan.FromSeconds(60), current.Headers.CacheControl?.MaxAge);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Welcome_reachable_with_admin_network_list()
    {
        await using var f = Factory(s => s with { Security = s.Security with { AdminNetworks = ["10.0.0.0/8"] } }, Outsider);

        var page = await GetAsync(f);
        using var css = await Client(f).GetAsync("/welcome/page-0.css", Ct);
        using var staff = await Client(f).GetAsync("/login", Ct);

        Assert.Equal(HttpStatusCode.OK, page.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode); // the list still keeps staff pages to its networks
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Region_server_shows_default(OwnerPlan plan)
    {
        await using var f = Factory(s => s with
        {
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006) : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null) : null,
            Hypergrid = false,
        });
        Publish(f, TextDesign("Guest side only"));

        var (r, page, _) = await GetAsync(f);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("Guest side only", page.Body!.TextContent);
        Assert.Empty(page.QuerySelectorAll("a[href='/signup'], a[href='/guest/forgot-password']"));
    }

    [Fact]
    public async Task Links_follow_what_is_open()
    {
        var texts = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText> { ["terms"] = new("Test terms.", 1, DateTimeOffset.UtcNow) },
        };
        await using var f = Factory(s => s with { Legal = new LegalSettings(texts, false, null) });

        var (_, page, _) = await GetAsync(f);

        Assert.Empty(page.QuerySelectorAll("a[href='/signup']"));
        Assert.NotNull(page.QuerySelector("a[href='/terms']"));
        Assert.Empty(page.QuerySelectorAll("a[href='/privacy']"));
        Assert.NotNull(page.QuerySelector("a[href='/guest/forgot-password']"));
    }
}
