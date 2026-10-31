using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's editor for the legal pages at /security/legal: saving, versions, preview, the tick, the gate and the plain-HTTP warning.</summary>
public sealed class LegalEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PlainHttp = "This website uses plain HTTP. On a public grid, passwords typed here cross the internet unencrypted and could be read on the way.";

    private static LauncherFactory Factory(OwnerPlan? plan = null, string? hostname = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Hostname = hostname ?? s.Hostname,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner", Uri? baseAddress = null)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = baseAddress ?? new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    /// <summary>Posts to the editor with the antiforgery token of <paramref name="tokenFrom"/> (the token belongs to the client, not the page).</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields, string tokenFrom = "/security/legal")
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync("/security/legal?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string page, string text) =>
        PostAsync(client, "Save", new() { ["Texts[" + page + "]"] = text });

    private static LegalText? Stored(LauncherFactory f, string page) =>
        LegalPages.Get(f.Store.LoadOrCreate(), page);

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static List<JsonElement> LegalEntries(LauncherFactory f) =>
        Audit(f).Where(e => e.GetProperty("action").GetString()!.StartsWith("legal-", StringComparison.Ordinal)).ToList();

    private static string TextOf(string html) => new HtmlParser().ParseDocument(html).Body!.TextContent;

    [Fact]
    public async Task Owner_saves_terms_version_1_then_2()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var first = f.Time.GetUtcNow();

        using var one = await SaveAsync(owner, "terms", "Test terms one.");
        Assert.Equal(HttpStatusCode.Redirect, one.StatusCode);
        Assert.Equal("/security/legal", one.Headers.Location?.OriginalString);
        var v1 = Stored(f, "terms");
        Assert.Equal(("Test terms one.", 1, first), (v1!.Markdown, v1.Version, v1.Saved));
        Assert.Equal([false], f.Grid.UpdateWriteConfig);

        f.Time.Advance(TimeSpan.FromHours(1));
        using var two = await SaveAsync(owner, "terms", "Test terms two.");
        var v2 = Stored(f, "terms");
        Assert.Equal(("Test terms two.", 2, first.AddHours(1)), (v2!.Markdown, v2.Version, v2.Saved));

        // The page shows what was saved, and the saved notice once.
        var html = await owner.GetStringAsync("/security/legal", Ct);
        Assert.Contains("Test terms two.", html);
        Assert.Contains("Saved. The pages are live now.", TextOf(html));
        Assert.DoesNotContain("Saved. The pages are live now.", TextOf(await owner.GetStringAsync("/security/legal", Ct)));
        // The public page reads it at once.
        Assert.Contains("Test terms two.", await owner.GetStringAsync("/terms", Ct));
    }

    [Fact]
    public async Task Each_page_is_versioned_on_its_own_and_a_page_left_off_the_form_is_left_alone()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);

        using var a = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms one.", ["Texts[privacy]"] = "Test privacy one." });
        using var b = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms two.", ["Texts[privacy]"] = "Test privacy one." });
        using var c = await SaveAsync(owner, "contact", "Test contact one.");

        Assert.Equal(2, Stored(f, "terms")!.Version);
        Assert.Equal(1, Stored(f, "privacy")!.Version);
        Assert.Equal(1, Stored(f, "contact")!.Version);
    }

    [Fact]
    public async Task Unchanged_save_keeps_version()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        using var one = await SaveAsync(owner, "terms", "Test terms one.\r\nSecond line.");
        var before = Stored(f, "terms");
        f.Time.Advance(TimeSpan.FromHours(1));

        // A browser posts line ends as CRLF; the same text again is not a change.
        using var again = await SaveAsync(owner, "terms", "Test terms one.\r\nSecond line.");
        using var lf = await SaveAsync(owner, "terms", "Test terms one.\nSecond line.");

        Assert.Equal(before, Stored(f, "terms"));
        var html = await owner.GetStringAsync("/security/legal", Ct);
        Assert.Contains("Nothing changed.", TextOf(html));
        Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "legal-text-saved");
    }

    [Fact]
    public async Task A_page_with_only_whitespace_is_cleared()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        using var one = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms one.", ["Texts[privacy]"] = "Test privacy one." });

        using var clear = await SaveAsync(owner, "terms", " \r\n  ");

        Assert.Null(Stored(f, "terms"));
        Assert.NotNull(Stored(f, "privacy"));
        Assert.Contains(Audit(f), e => e.GetProperty("action").GetString() == "legal-text-saved" && e.GetProperty("target").GetString() == "terms cleared");
        using var gone = await owner.GetAsync("/terms", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Clearing what was never written changes nothing and is not audited.
        var count = LegalEntries(f).Count;
        using var again = await SaveAsync(owner, "terms", "");
        Assert.Equal(count, LegalEntries(f).Count);
    }

    [Fact]
    public async Task Clearing_a_page_never_lets_its_version_numbers_be_used_again()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        using var one = await SaveAsync(owner, "terms", "Test terms one.");
        using var two = await SaveAsync(owner, "terms", "Test terms two.");

        using var clear = await SaveAsync(owner, "terms", "");
        using var clearAgain = await SaveAsync(owner, "terms", "  ");
        Assert.Null(Stored(f, "terms"));
        Assert.Equal(2, f.Store.LoadOrCreate().Legal.Texts![LegalPages.DefaultLanguage]["terms"].Version); // kept as an empty record
        Assert.Single(LegalEntries(f), e => e.GetProperty("target").GetString() == "terms cleared"); // the second clear changed nothing

        using var again = await SaveAsync(owner, "terms", "Test terms three.");

        Assert.Equal(3, Stored(f, "terms")!.Version);
        Assert.Contains(LegalEntries(f), e => e.GetProperty("target").GetString() == "terms v3");
    }

    [Fact]
    public async Task A_text_that_starts_with_a_newline_keeps_its_version_when_the_editor_posts_it_back()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        const string Text = "\nTest terms one.";
        using var one = await SaveAsync(owner, "terms", Text);
        Assert.Equal(Text, Stored(f, "terms")!.Markdown);

        // A browser drops the one newline straight after the opening tag, so the page must put one there.
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/legal", Ct));
        var shown = page.QuerySelector("#text-terms")!.TextContent;
        Assert.Equal(Text, shown);
        using var back = await SaveAsync(owner, "terms", shown);

        Assert.Equal(1, Stored(f, "terms")!.Version);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Admin_gets_403(string role)
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, role);

        using var get = await client.GetAsync("/security/legal", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await client.PostAsync("/security/legal?handler=Save",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Texts[terms]"] = "Test terms one." }), Ct);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Null(Stored(f, "terms"));
    }

    [Fact]
    public async Task Anonymous_is_sent_to_sign_in()
    {
        await using var f = Factory();
        using var r = await f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/security/legal", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    [Fact]
    public async Task Saves_and_tick_are_audited()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);

        using var save = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms one.", ["Texts[cookies]"] = "Test cookies one." });
        using var on = await PostAsync(owner, "Responsibility", new() { ["Responsible"] = "true" });
        using var same = await PostAsync(owner, "Responsibility", new() { ["Responsible"] = "true" });
        using var off = await PostAsync(owner, "Responsibility", new());

        var entries = Audit(f);
        Assert.Equal(["terms v1", "cookies v1"],
            entries.Where(e => e.GetProperty("action").GetString() == "legal-text-saved").Select(e => e.GetProperty("target").GetString()));
        Assert.Equal(["on", "off"],
            entries.Where(e => e.GetProperty("action").GetString() == "legal-responsibility").Select(e => e.GetProperty("target").GetString()));
        var entry = entries.First(e => e.GetProperty("action").GetString() == "legal-text-saved");
        Assert.Equal("owner", entry.GetProperty("role").GetString());
        Assert.Equal("Test Owner", entry.GetProperty("actor").GetString());
        // Nothing the owner wrote is in the log.
        Assert.DoesNotContain("Test terms one.", string.Join(' ', Audit(f)));
    }

    [Fact]
    public async Task The_tick_saves_who_is_responsible_and_since_when()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var now = f.Time.GetUtcNow();

        using var on = await PostAsync(owner, "Responsibility", new() { ["Responsible"] = "true" });
        var legal = f.Store.LoadOrCreate().Legal;
        Assert.True(legal.OperatorResponsible);
        Assert.Equal(now, legal.ResponsibleSince);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/legal", Ct));
        Assert.True(page.QuerySelector("#Responsible")!.HasAttribute("checked"));
        Assert.Contains("I wrote these notices and I am responsible for them.", page.QuerySelector("label[for=Responsible]")!.TextContent);

        f.Time.Advance(TimeSpan.FromMinutes(5));
        using var again = await PostAsync(owner, "Responsibility", new() { ["Responsible"] = "true" });
        Assert.Equal(now, f.Store.LoadOrCreate().Legal.ResponsibleSince); // still ticked: the date stays

        using var off = await PostAsync(owner, "Responsibility", new());
        legal = f.Store.LoadOrCreate().Legal;
        Assert.False(legal.OperatorResponsible);
        Assert.Null(legal.ResponsibleSince);
    }

    [Fact]
    public async Task Preview_renders_without_saving()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);

        using var r = await PostAsync(owner, "Preview", new()
        {
            ["Texts[terms]"] = "Test **bold** terms. <script>alert(1)</script>",
            ["Texts[privacy]"] = "   ",
        });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var preview = page.QuerySelector("#previews")!;
        Assert.Equal("Test bold terms. <script>alert(1)</script>", preview.QuerySelector("article p")!.TextContent);
        Assert.Equal("bold", preview.QuerySelector("strong")!.TextContent);
        Assert.Null(preview.QuerySelector("script"));
        Assert.Single(preview.QuerySelectorAll("article")); // only the page with text
        Assert.Contains("Test **bold** terms.", page.QuerySelector("#text-terms")!.TextContent); // what was typed stays
        Assert.Equal(new LegalSettings(), f.Store.LoadOrCreate().Legal);
        Assert.Empty(LegalEntries(f));
    }

    [Fact]
    public async Task Gate_state_lists_missing()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        string Gate(string html) => new HtmlParser().ParseDocument(html).QuerySelector("#gate-state")!.TextContent.Trim();

        Assert.Equal("Before sign-ups can be opened: Terms, Privacy, Cookies, the box saying you are responsible",
            Gate(await owner.GetStringAsync("/security/legal", Ct)));

        using var some = await PostAsync(owner, "Save", new() { ["Texts[privacy]"] = "Test privacy one.", ["Texts[contact]"] = "Test contact one." });
        Assert.Equal("Before sign-ups can be opened: Terms, Cookies, the box saying you are responsible",
            Gate(await owner.GetStringAsync("/security/legal", Ct)));

        using var all = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms one.", ["Texts[cookies]"] = "Test cookies one." });
        Assert.Equal("Before sign-ups can be opened: the box saying you are responsible", Gate(await owner.GetStringAsync("/security/legal", Ct)));

        using var tick = await PostAsync(owner, "Responsibility", new() { ["Responsible"] = "true" });
        Assert.Equal("Sign-ups can be opened", Gate(await owner.GetStringAsync("/security/legal", Ct)));
    }

    [Fact]
    public async Task The_facts_are_listed_beside_the_editor()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);

        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/legal", Ct));
        var facts = page.QuerySelector("#site-facts")!;

        Assert.Equal("Facts about this software, not legal text", facts.QuerySelector("h2")!.TextContent);
        foreach (var (name, _) in SiteFacts.Cookies)
            Assert.Contains(name, facts.TextContent);
        Assert.Contains("guests/terms.json", facts.TextContent);
        Assert.Equal(4, page.QuerySelectorAll("#legal-texts textarea").Length);
    }

    [Fact]
    public async Task Plain_http_warning_on_public_plan_only()
    {
        await using var pub = Factory(OwnerPlan.SimplePublic, "grid.example.org");
        var owner = await SignedInAsync(pub);
        foreach (var url in new[] { "/security/legal", "/dashboard" })
            Assert.Contains(PlainHttp, TextOf(await owner.GetStringAsync(url, Ct)));
        // Not on other staff pages.
        Assert.DoesNotContain(PlainHttp, TextOf(await owner.GetStringAsync("/security", Ct)));
        // Over HTTPS there is nothing to warn about.
        var secure = await SignedInAsync(pub, baseAddress: new Uri("https://localhost"));
        Assert.DoesNotContain(PlainHttp, TextOf(await secure.GetStringAsync("/security/legal", Ct)));

        await using var local = Factory(OwnerPlan.Local);
        var localOwner = await SignedInAsync(local);
        foreach (var url in new[] { "/security/legal", "/dashboard" })
            Assert.DoesNotContain(PlainHttp, TextOf(await localOwner.GetStringAsync(url, Ct)));

        // A public plan reached by a private address is not a public grid either.
        await using var lan = Factory(OwnerPlan.SimplePublic, "192.168.1.5");
        var lanOwner = await SignedInAsync(lan);
        Assert.DoesNotContain(PlainHttp, TextOf(await lanOwner.GetStringAsync("/dashboard", Ct)));
    }

    [Theory]
    [InlineData(OwnerPlan.Connected)]
    [InlineData(OwnerPlan.RegionServer)]
    public async Task Editor_404_on_connected(OwnerPlan plan)
    {
        await using var f = Factory(plan);
        var owner = await SignedInAsync(f);

        using var get = await owner.GetAsync("/security/legal", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        // There is no editor page to take a token from, so the Security page gives it.
        using var post = await PostAsync(owner, "Save", new() { ["Texts[terms]"] = "Test terms one." }, tokenFrom: "/security");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Null(Stored(f, "terms"));

        // The Security page stays, without the link to the editor.
        var security = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security", Ct));
        Assert.Null(security.QuerySelector("#legal-editor-link"));
        // Nor does the allow-list help speak of guests and public pages there are none of.
        Assert.DoesNotContain("Guests and the public pages", security.Body!.TextContent);
    }

    [Fact]
    public async Task The_security_page_links_to_the_editor()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        var owner = await SignedInAsync(f);

        var security = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security", Ct));

        Assert.Equal("/security/legal", security.QuerySelector("#legal-editor-link")!.GetAttribute("href"));
        Assert.Contains("Guests and the public pages are reachable from any address; this list covers the staff pages.", security.Body!.TextContent);
    }
}
