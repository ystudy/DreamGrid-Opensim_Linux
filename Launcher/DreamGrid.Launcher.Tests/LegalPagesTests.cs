using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The operator's legal pages: what counts as written, the sign-up gate, the public pages and their links. All texts are made up.</summary>
public sealed class LegalPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Saved = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Legal settings with these page ids written (each "Test [id] one.", version 3, saved <see cref="Saved"/>).</summary>
    private static LegalSettings Legal(bool responsible, params string[] pages) => new(
        new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = pages.ToDictionary(p => p, p => new LegalText($"Test {p} one.", 3, Saved)),
        },
        responsible,
        responsible ? Saved : null);

    private static LauncherSettings Settings(LegalSettings legal) =>
        new() { Db = new("a", "b", "c"), Console = new("u", "p"), MachineId = "m", Legal = legal };

    [Fact]
    public void Gate_closed_until_three_texts_and_tick()
    {
        Assert.True(LegalPages.SignUpGateOpen(Settings(Legal(true, "terms", "privacy", "cookies"))));
        Assert.Empty(LegalPages.Missing(Settings(Legal(true, "terms", "privacy", "cookies"))));

        // No tick keeps it closed even with every text.
        Assert.False(LegalPages.SignUpGateOpen(Settings(Legal(false, "terms", "privacy", "cookies", "contact"))));
        // Each missing text keeps it closed, and Missing names it.
        Assert.False(LegalPages.SignUpGateOpen(Settings(Legal(true, "privacy", "cookies"))));
        Assert.Equal(["terms"], LegalPages.Missing(Settings(Legal(true, "privacy", "cookies"))));
        Assert.False(LegalPages.SignUpGateOpen(Settings(Legal(true, "terms", "cookies"))));
        Assert.Equal(["privacy"], LegalPages.Missing(Settings(Legal(true, "terms", "cookies"))));
        Assert.False(LegalPages.SignUpGateOpen(Settings(Legal(true, "terms", "privacy"))));
        Assert.Equal(["cookies"], LegalPages.Missing(Settings(Legal(true, "terms", "privacy"))));
        // Nothing written: all three, in order. The default settings are the same.
        Assert.False(LegalPages.SignUpGateOpen(Settings(Legal(true))));
        Assert.Equal(["terms", "privacy", "cookies"], LegalPages.Missing(Settings(new LegalSettings())));
        Assert.False(LegalPages.SignUpGateOpen(Settings(new LegalSettings())));
    }

    [Fact]
    public void Contact_is_optional()
    {
        var s = Settings(Legal(true, "terms", "privacy", "cookies"));

        Assert.Null(LegalPages.Get(s, LegalPages.Contact));
        Assert.True(LegalPages.SignUpGateOpen(s));
        Assert.DoesNotContain("contact", LegalPages.Missing(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \r\n\t ")]
    public void Whitespace_text_counts_as_missing(string text)
    {
        var texts = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText>
            {
                ["terms"] = new("Test terms one.", 1, Saved),
                ["privacy"] = new(text, 1, Saved),
                ["cookies"] = new("Test cookies one.", 1, Saved),
            },
        };
        var s = Settings(new LegalSettings(texts, true, Saved));

        Assert.Null(LegalPages.Get(s, "privacy"));
        Assert.Equal(["privacy"], LegalPages.Missing(s));
        Assert.False(LegalPages.SignUpGateOpen(s));
    }

    [Fact]
    public void Only_the_default_language_counts()
    {
        var texts = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["de"] = new Dictionary<string, LegalText> { ["terms"] = new("Test terms one.", 1, Saved) },
        };

        Assert.Null(LegalPages.Get(Settings(new LegalSettings(texts)), "terms"));
    }

    // ---- the pages ----

    private static LauncherFactory Factory(LegalSettings legal, Func<LauncherSettings, LauncherSettings>? more = null) =>
        new(s => (more ?? (x => x))(s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = legal,
        }))
        {
            Before = TestAuth.Apply,
        };

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    [Fact]
    public async Task Legal_page_shows_rendered_text()
    {
        var texts = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText>
            {
                ["terms"] = new("## Test heading\n\nTest terms **one**. [here](javascript:alert(1)) <script>alert(2)</script>", 4, Saved),
            },
        };
        await using var f = Factory(new LegalSettings(texts));

        using var r = await Client(f).GetAsync("/terms", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Terms", page.QuerySelector("main h1")!.TextContent);
        Assert.Equal("Version 4, 30 October 2026", page.QuerySelector("main h1 + p")!.TextContent.Trim());
        Assert.Equal("Test heading", page.QuerySelector("main article h2")!.TextContent);
        Assert.Equal("one", page.QuerySelector("main article strong")!.TextContent);
        Assert.Null(page.QuerySelector("main article a")!.GetAttribute("href"));
        Assert.Empty(page.QuerySelectorAll("main article script"));
        Assert.Contains("<script>alert(2)</script>", page.QuerySelector("main article")!.TextContent);
        Assert.Equal("Terms - Avalon", page.Title);
        Assert.Empty(page.QuerySelectorAll("header nav a")); // none of the staff side
    }

    [Theory]
    [InlineData("terms", "Terms")]
    [InlineData("privacy", "Privacy")]
    [InlineData("cookies", "Cookies")]
    [InlineData("contact", "Contact")]
    public async Task Each_legal_page_is_at_its_own_address(string id, string title)
    {
        await using var f = Factory(Legal(true, id));

        using var r = await Client(f).GetAsync("/" + id, Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(title, page.QuerySelector("main h1")!.TextContent);
        Assert.Contains($"Test {id} one.", page.QuerySelector("main article")!.TextContent);
    }

    [Theory]
    [InlineData("/Terms")]
    [InlineData("/PRIVACY")]
    public async Task Legal_page_address_ignores_case(string url)
    {
        await using var f = Factory(Legal(true, "terms", "privacy"));

        using var r = await Client(f).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("one.", page.QuerySelector("main article")!.TextContent);
    }

    [Theory]
    [InlineData("/terms")]
    [InlineData("/privacy")]
    [InlineData("/cookies")]
    [InlineData("/contact")]
    public async Task Legal_page_without_text_is_404(string url)
    {
        await using var f = Factory(new LegalSettings());

        using var r = await Client(f).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Legal_page_with_only_whitespace_is_404()
    {
        var texts = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText> { ["terms"] = new("  \n ", 1, Saved) },
        };
        await using var f = Factory(new LegalSettings(texts));

        using var r = await Client(f).GetAsync("/terms", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Signup_says_not_open()
    {
        // Open or not, this plan only says it isn't open yet; sign-up itself comes later.
        foreach (var legal in new[] { new LegalSettings(), Legal(true, "terms", "privacy", "cookies") })
        {
            await using var f = Factory(legal);

            using var r = await Client(f).GetAsync("/signup", Ct);

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
            Assert.Contains("This grid isn't open for sign-ups yet.", page.QuerySelector("main")!.TextContent);
            Assert.Empty(page.QuerySelectorAll("form"));
        }
    }

    [Fact]
    public async Task Footer_links_only_pages_with_text()
    {
        await using var f = Factory(Legal(true, "terms", "cookies"));
        var anonymous = Client(f);
        var guest = Client(f);
        (await guest.GetAsync("/__test/guest-signin", Ct)).Dispose();

        foreach (var (client, url) in new[] { (anonymous, "/"), (anonymous, "/home"), (anonymous, "/signup"), (anonymous, "/terms"), (guest, "/guest/terms") })
        {
            using var r = await client.GetAsync(url, Ct);

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
            var links = page.QuerySelectorAll("footer nav a").Select(a => (a.GetAttribute("href"), a.TextContent)).ToList();
            Assert.Equal([("/terms", "Terms"), ("/cookies", "Cookies")], links);
        }
    }

    [Fact]
    public async Task Footer_has_all_four_links_in_order_and_keeps_staff_sign_in()
    {
        await using var f = Factory(Legal(true, "contact", "cookies", "privacy", "terms"));

        using var r = await Client(f).GetAsync("/", Ct);

        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["/terms", "/privacy", "/cookies", "/contact"], page.QuerySelectorAll("footer nav a").Select(a => a.GetAttribute("href")));
        Assert.Equal("Staff sign in", page.QuerySelector("footer a[href='/login']")!.TextContent.Trim());
    }

    [Fact]
    public async Task No_text_means_no_legal_links()
    {
        await using var f = Factory(new LegalSettings());

        using var r = await Client(f).GetAsync("/", Ct);

        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Empty(page.QuerySelectorAll("footer nav"));
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Legal_pages_404_on_region_server(OwnerPlan plan)
    {
        await using var f = Factory(Legal(true, "terms", "privacy", "cookies", "contact"), s => s with
        {
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006) : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null) : null,
            Hypergrid = false,
        });

        foreach (var url in new[] { "/terms", "/privacy", "/cookies", "/contact", "/signup" })
        {
            using var r = await Client(f).GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
    }
}
