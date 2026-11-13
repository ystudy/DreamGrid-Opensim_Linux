using System.Net;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Navigation;
using GridGate.Launcher.Resident;
using GridGate.Launcher.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The owner's page for the wording of the guest pages, at /security/wording.</summary>
public sealed class WordingPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Page = "/security/wording";

    private static LauncherFactory Factory(Dictionary<string, Dictionary<string, string>>? wording = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Wording = (wording ?? []).ToDictionary(l => l.Key, l => (IReadOnlyDictionary<string, string>)l.Value),
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedIn(LauncherFactory f, string role)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync($"/__test/signin?role={role}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static Task<HttpClient> OwnerAsync(LauncherFactory f) => SignedIn(f, "owner");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields, string lang = "en")
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync($"{Page}?lang={lang}", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync($"{Page}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static string Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Stored(LauncherFactory f) =>
        f.Services.GetRequiredService<Func<LauncherSettings>>()().Wording;

    private static string English(LauncherFactory f, string key) => f.Services.GetRequiredService<LanguageCatalog>().English(key);

    [Fact]
    public async Task Owner_only()
    {
        await using var f = Factory();
        var admin = await SignedIn(f, "admin");
        using var r = await admin.GetAsync(Page, Ct);
        Assert.True(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, r.StatusCode.ToString());
        using var anon = await f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Page, Ct);
        Assert.True(anon.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, anon.StatusCode.ToString());
    }

    [Fact]
    public async Task Shows_built_in_text_for_the_chosen_language()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var en = new HtmlParser().ParseDocument(await owner.GetStringAsync($"{Page}?lang=en", Ct));
        var de = new HtmlParser().ParseDocument(await owner.GetStringAsync($"{Page}?lang=de", Ct));
        foreach (var key in ToneText.Keys)
        {
            var enText = en.QuerySelector($"[data-key='{key}'] .built-in")!.TextContent.Trim();
            var deText = de.QuerySelector($"[data-key='{key}'] .built-in")!.TextContent.Trim();
            Assert.Equal(English(f, key).Replace("{0}", "{grid}"), enText);
            Assert.NotEmpty(deText);
            Assert.Contains(ToneText.Title(key), de.QuerySelector($"[data-key='{key}']")!.TextContent);
        }
        Assert.NotEqual(en.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent, de.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent);
        Assert.Contains("de", de.QuerySelectorAll("select[name=lang] option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task Unknown_language_falls_back_to_default()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var a = new HtmlParser().ParseDocument(await owner.GetStringAsync($"{Page}?lang=xx", Ct));
        var b = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page, Ct));
        Assert.Equal("en", a.QuerySelector("select[name=lang] option[selected]")!.GetAttribute("value"));
        Assert.Equal(a.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent, b.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent);
    }

    [Fact]
    public async Task Save_stores_only_an_override()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "de", ["key"] = "Home.Intro", ["Text"] = "Hallo\r\nin {grid}" }, "de"))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("Hallo\nin {grid}", Stored(f)["de"]["Home.Intro"]);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync($"{Page}?lang=de", Ct));
        Assert.Equal("Hallo\nin {grid}", page.QuerySelector("[data-key='Home.Intro'] textarea")!.TextContent.TrimStart('\n'));

        // Saving the built-in text itself leaves no override behind.
        var builtIn = page.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent.Trim();
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "de", ["key"] = "Home.Intro", ["Text"] = builtIn }, "de"))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Stored(f).ContainsKey("de"));
    }

    [Fact]
    public async Task Reset_removes_the_override()
    {
        await using var f = Factory(new() { ["en"] = new() { ["Home.Title"] = "Mine", ["Support.Title"] = "Keep" } });
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, "Reset", new() { ["lang"] = "en", ["key"] = "Home.Title" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Stored(f)["en"].ContainsKey("Home.Title"));
        Assert.Equal("Keep", Stored(f)["en"]["Support.Title"]);
        Assert.Contains("page-wording-reset", Audit(f));
    }

    [Fact]
    public async Task Empty_save_puts_the_original_back()
    {
        await using var f = Factory(new() { ["en"] = new() { ["Home.Title"] = "Mine", ["Support.Title"] = "Keep" } });
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "en", ["key"] = "Home.Title", ["Text"] = "  \r\n " }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Stored(f)["en"].ContainsKey("Home.Title"));
        Assert.Equal("Keep", Stored(f)["en"]["Support.Title"]);
        var log = Audit(f);
        Assert.Contains("page-wording-reset", log);
        Assert.DoesNotContain("page-wording-saved", log);

        // With nothing saved, an empty box changes nothing and is still fine.
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "fr", ["key"] = "Home.Title", ["Text"] = "" }, "fr"))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Stored(f).ContainsKey("fr"));
    }

    [Fact]
    public async Task Too_long_is_refused()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Save", new() { ["lang"] = "en", ["key"] = "Home.Intro", ["Text"] = new string('a', 2001) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("2,000", await r.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Stored(f));
        using var ok = await PostAsync(owner, "Save", new() { ["lang"] = "en", ["key"] = "Home.Intro", ["Text"] = new string('a', 2000) });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public async Task Rejects_unknown_key_and_language()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "en", ["key"] = "Nope", ["Text"] = "x" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "xx", ["key"] = "Home.Title", ["Text"] = "x" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(Stored(f));
    }

    [Fact]
    public async Task Audited_without_text()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, "Save", new() { ["lang"] = "fr", ["key"] = "Home.Title", ["Text"] = "SecretWords" }, "fr"))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var log = Audit(f);
        Assert.Contains("page-wording-saved", log);
        Assert.Contains("fr/Home.Title", log);
        Assert.DoesNotContain("SecretWords", log);
    }

    [Fact]
    public async Task No_longer_used_overrides_are_listed_and_deletable()
    {
        await using var f = Factory(new() { ["en"] = new() { ["Old.Key"] = "Old words", ["Home.Title"] = "Mine" } });
        var owner = await OwnerAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page, Ct));
        var unused = page.QuerySelector("#no-longer-used")!;
        Assert.Contains("Old.Key", unused.TextContent);
        Assert.Contains("Old words", unused.TextContent);
        Assert.DoesNotContain("Home.Title", unused.TextContent);
        using (var r = await PostAsync(owner, "Delete", new() { ["lang"] = "en", ["key"] = "Old.Key" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Stored(f)["en"].ContainsKey("Old.Key"));
        Assert.Equal("Mine", Stored(f)["en"]["Home.Title"]);
        // A tone key is not deletable through this route.
        using (var r = await PostAsync(owner, "Delete", new() { ["lang"] = "en", ["key"] = "Home.Title" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Added_language_shows_its_file_text_or_English()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<OwnerLanguages>().Save(new OwnerLanguage("it",
            new Dictionary<string, string> { ["Home.Intro"] = "Ciao dal file" }, new Dictionary<string, string>(), DateTimeOffset.UtcNow));
        var owner = await OwnerAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync($"{Page}?lang=it", Ct));
        Assert.Equal("Ciao dal file", page.QuerySelector("[data-key='Home.Intro'] .built-in")!.TextContent.Trim());
        Assert.Equal(English(f, "Home.Title").Replace("{0}", "{grid}"), page.QuerySelector("[data-key='Home.Title'] .built-in")!.TextContent.Trim());
    }

    [Fact]
    public void Menu_shows_the_item_to_the_owner_only()
    {
        var s = TestSettings.Create() with { Plan = OwnerPlan.Local };
        Assert.Contains("Page wording", StaffMenu.For(StaffRole.Owner, s, true).Single(g => g.Text == "Guest pages").Items.Select(i => i.Text));
        Assert.DoesNotContain("Page wording", StaffMenu.For(StaffRole.Admin, s, true).SelectMany(g => g.Items).Select(i => i.Text));
    }
}
