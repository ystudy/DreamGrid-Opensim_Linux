using System.Net;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

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

        Assert.Null(LegalPages.Get(s, LegalPages.Contact, "en"));
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

        Assert.Null(LegalPages.Get(s, "privacy", "en"));
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

        Assert.Null(LegalPages.Get(Settings(new LegalSettings(texts)), "terms", "en"));
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
        // Written legal pages alone don't open it: the owner's sign-up switch is still off.
        foreach (var legal in new[] { new LegalSettings(), Legal(true, "terms", "privacy", "cookies") })
        {
            await using var f = Factory(legal);

            using var r = await Client(f).GetAsync("/signup", Ct);

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
            Assert.Contains("This grid isn't open for sign-ups yet.", page.QuerySelector("main")!.TextContent);
            Assert.Empty(page.QuerySelectorAll("main form"));
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
        Assert.Equal("Staff sign in", page.QuerySelector("header a[href='/login']")!.TextContent.Trim());
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

    [Fact]
    public async Task The_front_page_shows_the_written_legal_pages_as_buttons()
    {
        await using var f = Factory(Legal(true, "contact", "privacy", "terms"));
        using var r = await Client(f).GetAsync("/", Ct);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

        var buttons = page.QuerySelectorAll("main #front-legal a.btn").ToList();
        Assert.Equal(["/terms", "/privacy", "/contact"], buttons.Select(a => a.GetAttribute("href")));
        Assert.Equal(["Terms", "Privacy", "Contact"], buttons.Select(a => a.TextContent.Trim()));
        Assert.Equal(["/img/icons.svg#document", "/img/icons.svg#lock", "/img/icons.svg#mail"],
            buttons.Select(a => a.QuerySelector("svg use")!.GetAttribute("href")));
    }

    [Fact]
    public async Task No_legal_text_means_no_row_on_the_front_page()
    {
        await using var f = Factory(new LegalSettings());
        using var r = await Client(f).GetAsync("/", Ct);
        Assert.Null(new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#front-legal"));
    }

    // ---- the language a legal page is shown in ----

    private static LegalSettings Written(params (string Language, string Text)[] pages) => new(
        pages.ToDictionary(p => p.Language, p => (IReadOnlyDictionary<string, LegalText>)new Dictionary<string, LegalText> { ["terms"] = new(p.Text, 2, Saved) }),
        true,
        Saved);

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> TermsAsync(LauncherFactory f, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/terms");
        if (cookie is not null) request.Headers.Add("Cookie", "gg-lang=" + cookie);
        using var r = await Client(f).SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Legal_page_falls_back_to_the_default_language_with_a_note()
    {
        // The grid's default is German and only German is written; an English reader gets the German text and is told.
        await using var f = Factory(Written(("de", "Test Bedingungen.")), s => s with { Languages = s.Languages with { Default = "de" } });

        var english = await TermsAsync(f, "en");

        Assert.Equal("This page is shown in Deutsch.", english.QuerySelector("main [role=note]")!.TextContent.Trim());
        Assert.Contains("Test Bedingungen.", english.QuerySelector("main article")!.TextContent);

        // A German reader reads their own language and needs no note.
        var german = await TermsAsync(f, "de");
        Assert.Null(german.QuerySelector("main [role=note]"));
        Assert.Contains("Test Bedingungen.", german.QuerySelector("main article")!.TextContent);
    }

    [Fact]
    public async Task Legal_page_in_the_readers_language_has_no_note()
    {
        await using var f = Factory(Written(("en", "Test terms."), ("de", "Test Bedingungen.")));

        Assert.Null((await TermsAsync(f, "en")).QuerySelector("main [role=note]"));
        Assert.Null((await TermsAsync(f, "de")).QuerySelector("main [role=note]"));
        Assert.Contains("Test Bedingungen.", (await TermsAsync(f, "de")).QuerySelector("main article")!.TextContent);
    }

    [Fact]
    public async Task Legal_page_falls_back_to_English_when_the_default_has_no_text_either()
    {
        await using var f = Factory(Written(("en", "Test terms.")), s => s with { Languages = s.Languages with { Default = "de" } });

        var page = await TermsAsync(f, "de");

        // The note is in the reader's language (German); the language it names is given in its own name.
        Assert.Equal("Diese Seite wird in folgender Sprache angezeigt: English.", page.QuerySelector("main [role=note]")!.TextContent.Trim());
        Assert.Contains("Test terms.", page.QuerySelector("main article")!.TextContent);
    }

    [Fact]
    public void Get_tries_the_language_then_the_default_then_English()
    {
        var s = Settings(Written(("en", "English."), ("de", "Deutsch."), ("fr", "Francais."))) with { Languages = new LanguageSettings { Default = "fr" } };

        Assert.Equal("Deutsch.", LegalPages.Get(s, "terms", "de")!.Markdown);
        Assert.Equal("Francais.", LegalPages.Get(s, "terms", "es")!.Markdown); // the default's
        var onlyEnglish = Settings(Written(("en", "English."))) with { Languages = new LanguageSettings { Default = "fr" } };
        Assert.Equal("English.", LegalPages.Get(onlyEnglish, "terms", "es")!.Markdown);
        Assert.Equal(("fr", "Francais."), (LegalPages.Find(s, "terms", "es")!.Value.Language, LegalPages.Find(s, "terms", "es")!.Value.Text.Markdown));
        Assert.Null(LegalPages.Get(onlyEnglish, "privacy", "de"));
    }

    [Fact]
    public void Sign_up_gate_uses_the_grid_default()
    {
        // The default language's texts are the source: German texts open the gate when German is the default, English ones don't.
        var german = new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["de"] = new[] { "terms", "privacy", "cookies" }.ToDictionary(p => p, p => new LegalText("Test.", 1, Saved)),
        };
        var s = Settings(new LegalSettings(german, true, Saved)) with { Languages = new LanguageSettings { Default = "de" } };

        Assert.True(LegalPages.SignUpGateOpen(s));
        Assert.Empty(LegalPages.Missing(s));

        // German as the default with no legal records at all: English stands in for reading and acceptance, but the gate stays
        // closed until the German pages are written.
        var english = Settings(Legal(true, "terms", "privacy", "cookies")) with { Languages = new LanguageSettings { Default = "de" } };
        Assert.False(LegalPages.SignUpGateOpen(english));
        Assert.Equal(["terms", "privacy", "cookies"], LegalPages.Missing(english));
        Assert.Equal(("en", 3), (LegalPages.SourceOf(english, "terms").Language, LegalPages.SourceOf(english, "terms").Text!.Version));
        Assert.Equal(3, LegalPages.Get(english, "terms", "de")!.Version);
        Assert.True(LegalPages.SignUpGateOpen(english with { Languages = new LanguageSettings { Default = "en" } }));

        // A cleared German Terms is "no text": English does not take over, and the gate asks for Terms again.
        var cleared = Versions("de", ("de", "privacy", 1), ("de", "cookies", 1), ("en", "terms", 3)) is var v
            ? v with { Legal = v.Legal with { Texts = v.Legal.Texts!.ToDictionary(l => l.Key, l => l.Key == "de"
                ? (IReadOnlyDictionary<string, LegalText>)new Dictionary<string, LegalText>(l.Value) { ["terms"] = new("", 6, Saved) }
                : l.Value) } }
            : null!;
        Assert.Equal(("de", null), (LegalPages.SourceOf(cleared, "terms").Language, LegalPages.SourceOf(cleared, "terms").Text));
        Assert.Null(LegalPages.Get(cleared, "terms", "en"));
        Assert.Equal(["terms"], LegalPages.Missing(cleared));
        Assert.Equal(7, LegalPages.NextVersion(cleared, "terms")); // past the cleared v6
    }

    [Fact]
    public void Next_version_is_past_every_language_and_never_reused()
    {
        var s = Versions("de", ("en", "terms", 5), ("de", "terms", 2), ("fr", "terms", 4));
        Assert.Equal(6, LegalPages.NextVersion(s, "terms"));
        Assert.Equal(1, LegalPages.NextVersion(s, "privacy"));
        Assert.Equal(1, LegalPages.NextVersion(Settings(new LegalSettings()), "terms"));
    }

    [Fact]
    public void Up_to_date_and_default_blockers_compare_with_the_effective_source()
    {
        // Path B: German default without records, English Terms v5 standing in, French Terms v2.
        var s = Versions("de", ("en", "terms", 5), ("fr", "terms", 2)) with { Legal = Versions("de", ("en", "terms", 5), ("fr", "terms", 2)).Legal with { OperatorResponsible = false } };
        Assert.Contains("terms", LegalPages.UpToDate(s, "fr"));
        Assert.Equal(["terms"], LegalPages.DefaultBlockers(s, "fr"));
        Assert.Empty(LegalPages.DefaultBlockers(s, "es")); // no records: English keeps standing in at v5
        Assert.Equal("Terms, Privacy and Cookies", LegalPages.Titles(["terms", "privacy", "cookies"]));
        Assert.Equal("Privacy and Cookies", LegalPages.Titles(["privacy", "cookies"]));
        Assert.Equal("Terms", LegalPages.Titles(["terms"]));
    }

    // ---- legal versions: the default language is the source, a translation is shown only at the source's version ----

    private static LauncherSettings Versions(string @default, params (string Language, string Page, int Version)[] texts) =>
        Settings(new LegalSettings(
            texts.GroupBy(t => t.Language).ToDictionary(g => g.Key,
                g => (IReadOnlyDictionary<string, LegalText>)g.ToDictionary(t => t.Page, t => new LegalText($"Test {t.Language} {t.Page} v{t.Version}.", t.Version, Saved))),
            true,
            Saved)) with { Languages = new LanguageSettings { Default = @default } };

    [Fact]
    public void Legal_translation_hidden_when_source_is_newer()
    {
        var s = Versions("en", ("en", "terms", 4), ("de", "terms", 3), ("en", "privacy", 2), ("de", "privacy", 2));

        // Terms: the German translation is behind, so a German reader gets the source.
        Assert.Equal(("en", "Test en terms v4."), (LegalPages.Find(s, "terms", "de")!.Value.Language, LegalPages.Find(s, "terms", "de")!.Value.Text.Markdown));
        // Privacy: up to date, so it is shown.
        Assert.Equal(("de", "Test de privacy v2."), (LegalPages.Find(s, "privacy", "de")!.Value.Language, LegalPages.Find(s, "privacy", "de")!.Value.Text.Markdown));
        // A translation of a page the source doesn't have is not shown either.
        Assert.Null(LegalPages.Find(Versions("en", ("de", "cookies", 1)), "cookies", "de"));
    }

    [Fact]
    public async Task Legal_translation_hidden_when_source_is_newer_on_the_page()
    {
        var s = Versions("en", ("en", "terms", 4), ("de", "terms", 3));
        await using var f = Factory(s.Legal);

        var german = await TermsAsync(f, "de");

        Assert.Equal("Diese Seite wird in folgender Sprache angezeigt: English.", german.QuerySelector("main [role=note]")!.TextContent.Trim());
        Assert.Contains("Test en terms v4.", german.QuerySelector("main article")!.TextContent);
    }

    [Fact]
    public void UpToDate_lists_the_required_pages_missing_or_behind()
    {
        var s = Versions("en", ("en", "terms", 4), ("en", "privacy", 2), ("en", "cookies", 1), ("en", "contact", 5),
            ("de", "terms", 4), ("de", "privacy", 1), ("fr", "terms", 4), ("fr", "privacy", 2), ("fr", "cookies", 1));

        Assert.Equal(["privacy", "cookies"], LegalPages.UpToDate(s, "de"));
        Assert.Empty(LegalPages.UpToDate(s, "fr")); // contact is optional
        Assert.Empty(LegalPages.UpToDate(s, "en")); // the source itself
        Assert.Equal(["terms", "privacy", "cookies"], LegalPages.UpToDate(s, "es"));
    }

    [Fact]
    public void After_a_default_change_the_old_source_is_a_translation_at_the_same_version()
    {
        // The numbers carry over: English and German Terms both at 3, so German as the default shows English readers their own text.
        var s = Versions("de", ("en", "terms", 3), ("de", "terms", 3));

        Assert.Equal(3, LegalPages.Get(s, "terms", "de")!.Version);
        Assert.Equal("en", LegalPages.Find(s, "terms", "en")!.Value.Language);
    }

    [Fact]
    public void High_water_marks_are_seeded_from_old_records_and_outlive_them()
    {
        // A settings file from before plan 16b has no marks: they come from its records, cleared ones included.
        var old = Versions("en", ("en", "terms", 3), ("it", "terms", 6)).Legal;
        Assert.Null(old.HighestVersion);
        var marked = LegalPages.WithMarks(old);
        Assert.Equal(6, marked.HighestVersion!["terms"]);

        // The records go; the mark stays, and the next number is past it.
        var gone = marked with { Texts = marked.Texts!.Where(l => l.Key != "it").ToDictionary(l => l.Key, l => l.Value) };
        var s = Settings(gone);
        Assert.Equal(7, LegalPages.NextVersion(s, "terms"));
        Assert.Equal(6, LegalPages.Highest(gone, "terms"));
        // A mark never comes down.
        Assert.Equal(6, LegalPages.WithMarks(gone).HighestVersion!["terms"]);
    }
}
