using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Navigation;
using DreamGrid.Launcher.Resident;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Languages page (/security/languages) and the page that translates one added language (plan 16b, Review Focus 2).</summary>
public sealed class LanguagesPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private const string Page = "/security/languages";

    private static LauncherFactory Factory(IReadOnlyList<string>? offered = null, LegalSettings? legal = null)
    {
        var f = new LauncherFactory(s => s with
        {
            Legal = legal ?? s.Legal,
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Languages = offered is null ? s.Languages : s.Languages with { Offered = offered },
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

    private static async Task<IHtmlDocument> DocAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var token = (await DocAsync(client, url)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string handler, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = await TokenAsync(client, tokenFrom ?? url) };
        return await client.PostAsync($"{url}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string code, byte[] file)
    {
        var url = $"{Page}/{code}";
        var content = new MultipartFormDataContent
        {
            { new StringContent(await TokenAsync(client, url)), "__RequestVerificationToken" },
        };
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(part, "file", $"{code}.json");
        return await client.PostAsync($"{url}?handler=Upload", content, Ct);
    }

    private static byte[] FileOf(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static OwnerLanguages Store(LauncherFactory f) => f.Services.GetRequiredService<OwnerLanguages>();

    private static LanguageCatalog Catalog(LauncherFactory f) => f.Services.GetRequiredService<LanguageCatalog>();

    private static void Add(LauncherFactory f, string code, Dictionary<string, string>? page = null, Dictionary<string, string>? email = null) =>
        Store(f).Save(new OwnerLanguage(code, page ?? [], email ?? [], DateTimeOffset.UtcNow));

    private static string[] LanguageFiles(LauncherFactory f) =>
        Directory.Exists(f.Paths.Languages) ? Directory.GetFiles(f.Paths.Languages) : [];

    [Fact]
    public async Task Owner_only()
    {
        await using var f = Factory();
        Add(f, "it");
        var admin = await SignedIn(f, "admin");
        var anon = f.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var url in new[] { Page, $"{Page}/it", $"{Page}/it?handler=Download" })
        {
            using var a = await admin.GetAsync(url, Ct);
            Assert.True(a.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"{url}: {a.StatusCode}");
            using var n = await anon.GetAsync(url, Ct);
            Assert.True(n.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"{url}: {n.StatusCode}");
        }
    }

    [Fact]
    public async Task Lists_built_in_and_added_with_missing_counts()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Home.Title"] = "Benvenuto" });
        var residents = f.Services.GetRequiredService<ResidentLanguages>();
        residents.Set(Guid.NewGuid(), "it");
        residents.Set(Guid.NewGuid(), "de");
        residents.Set(Guid.NewGuid(), "de");
        var page = await DocAsync(await OwnerAsync(f), Page);

        var rows = page.QuerySelectorAll("#languages tbody tr").ToList();
        Assert.Equal([.. Languages.BuiltIn, "it"], rows.Select(r => r.GetAttribute("data-code")));

        var de = rows.Single(r => r.GetAttribute("data-code") == "de");
        Assert.Contains("Deutsch", de.TextContent);
        Assert.Contains("Built in", de.QuerySelector(".kind")!.TextContent);
        Assert.Equal("Yes", de.QuerySelector(".offered")!.TextContent.Trim());
        Assert.Equal("No", de.QuerySelector(".default")!.TextContent.Trim());
        Assert.Equal("2", de.QuerySelector(".residents")!.TextContent.Trim());
        Assert.Null(de.QuerySelector(".missing a"));
        Assert.Null(de.QuerySelector("form"));

        var en = rows.Single(r => r.GetAttribute("data-code") == "en");
        Assert.Equal("Yes", en.QuerySelector(".default")!.TextContent.Trim());

        var it = rows.Single(r => r.GetAttribute("data-code") == "it");
        Assert.Contains("Italiano", it.TextContent);
        Assert.Contains("Added", it.QuerySelector(".kind")!.TextContent);
        Assert.Equal("No", it.QuerySelector(".offered")!.TextContent.Trim());
        var missing = it.QuerySelector(".missing a")!;
        Assert.Equal("/security/languages/it", missing.GetAttribute("href"));
        Assert.Equal($"Missing: {Catalog(f).Missing("it")}", missing.TextContent.Trim());
        Assert.Equal(Catalog(f).PageKeys().Count + Catalog(f).EmailKeys().Count - 1, Catalog(f).Missing("it"));
        Assert.Equal("1", it.QuerySelector(".residents")!.TextContent.Trim());
        Assert.NotNull(it.QuerySelector("form[action*='handler=Delete'] button"));
    }

    [Fact]
    public async Task Add_creates_an_empty_file_and_redirects()
    {
        await using var f = Factory();
        Add(f, "ar");
        var owner = await OwnerAsync(f);
        var page = await DocAsync(owner, Page);
        var options = page.QuerySelectorAll("#add-code option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains("it", options);
        Assert.Contains("pt-PT", options);
        Assert.DoesNotContain("de", options);
        Assert.DoesNotContain("en", options);
        Assert.DoesNotContain("ar", options); // already added
        Assert.Contains("Italiano (Italian)", page.QuerySelector("#add-code option[value='it']")!.TextContent);

        using var r = await PostAsync(owner, Page, "Add", new() { ["code"] = "it" });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/security/languages/it", r.Headers.Location!.OriginalString);
        Assert.True(File.Exists(Path.Combine(f.Paths.Languages, "it.json")));
        var stored = Store(f).Get("it")!;
        Assert.Empty(stored.Page);
        Assert.Empty(stored.Email);
        Assert.Contains("language-added", Audit(f));

        // Adding it again keeps what it has.
        Add(f, "it", new() { ["Home.Title"] = "Benvenuto" });
        using var again = await PostAsync(owner, Page, "Add", new() { ["code"] = "it" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Benvenuto", Store(f).Get("it")!.Page["Home.Title"]);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("en")]
    [InlineData("EN-us")]
    [InlineData("xx")]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("..\\it")]
    [InlineData("it/../../x")]
    public async Task Add_refuses_built_in_unknown_and_path_codes(string code)
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, Page, "Add", new() { ["code"] = code });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.NotNull(page.QuerySelector(".error"));
        Assert.Empty(LanguageFiles(f));
        Assert.Empty(Store(f).Codes());
        Assert.DoesNotContain("language-added", Audit(f));
    }

    [Fact]
    public async Task Translate_page_saves_partial_work()
    {
        await using var f = Factory();
        Add(f, "it");
        var owner = await OwnerAsync(f);
        var url = $"{Page}/it";
        var page = await DocAsync(owner, url);
        // One row per key, English beside a box; email bodies get bigger boxes.
        Assert.Equal(Catalog(f).PageKeys(), page.QuerySelectorAll("#page-texts [data-key]").Select(e => e.GetAttribute("data-key")));
        Assert.Equal(Catalog(f).EmailKeys(), page.QuerySelectorAll("#email-texts [data-key]").Select(e => e.GetAttribute("data-key")));
        Assert.Equal(Catalog(f).English("Home.Intro"), page.QuerySelector("#page-texts [data-key='Home.Intro'] .english")!.TextContent.Trim());
        var body = int.Parse(page.QuerySelector("#email-texts [data-key='confirm-email.Body'] textarea")!.GetAttribute("rows")!);
        var subject = int.Parse(page.QuerySelector("#email-texts [data-key='confirm-email.Subject'] textarea")!.GetAttribute("rows")!);
        Assert.True(body > subject);

        using (var r = await PostAsync(owner, url, "Save", new() { ["page.Home.Title"] = "Benvenuto", ["page.Home.Intro"] = "", ["email.Reason.Other"] = "Altro\r\n" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var stored = Store(f).Get("it")!;
        Assert.Equal("Benvenuto", stored.Page["Home.Title"]);
        Assert.False(stored.Page.ContainsKey("Home.Intro"));
        Assert.Equal("Altro", stored.Email["Reason.Other"]);

        // A later save of other rows keeps the earlier ones; an emptied box takes its text away.
        using (var r = await PostAsync(owner, url, "Save", new() { ["page.Home.Intro"] = "Ciao", ["email.Reason.Other"] = " " }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        stored = Store(f).Get("it")!;
        Assert.Equal("Benvenuto", stored.Page["Home.Title"]);
        Assert.Equal("Ciao", stored.Page["Home.Intro"]);
        Assert.False(stored.Email.ContainsKey("Reason.Other"));

        page = await DocAsync(owner, url);
        Assert.Equal("Benvenuto", page.QuerySelector("#page-texts [data-key='Home.Title'] textarea")!.TextContent.Trim());
    }

    [Fact]
    public async Task Bad_row_is_shown_others_save()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Bots.Question"] = "Quanto fa {0} più {1}?" });
        var owner = await OwnerAsync(f);
        var url = $"{Page}/it";
        using var r = await PostAsync(owner, url, "Save", new()
        {
            ["page.Home.Title"] = "Benvenuto",
            ["page.Bots.Question"] = "Quanto fa {0}?",
            ["email.confirm-email.Subject"] = "Conferma",
            ["page.No.Such.Key"] = "ignored",
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var bad = page.QuerySelector("#page-texts [data-key='Bots.Question']")!;
        Assert.NotNull(bad.QuerySelector(".error"));
        Assert.Contains("{1}", bad.QuerySelector(".error")!.TextContent);
        Assert.Equal("Quanto fa {0}?", bad.QuerySelector("textarea")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("#email-texts [data-key='confirm-email.Subject'] .error"));
        Assert.Null(page.QuerySelector("#page-texts [data-key='Home.Title'] .error"));

        var stored = Store(f).Get("it")!;
        Assert.Equal("Benvenuto", stored.Page["Home.Title"]);
        Assert.Equal("Quanto fa {0} più {1}?", stored.Page["Bots.Question"]);
        Assert.False(stored.Email.ContainsKey("confirm-email.Subject"));
        Assert.False(stored.Page.ContainsKey("No.Such.Key"));
    }

    [Fact]
    public async Task Download_has_every_key_with_English()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Home.Title"] = "Benvenuto" }, new() { ["Reason.Other"] = "Altro" });
        var owner = await OwnerAsync(f);
        var page = await DocAsync(owner, $"{Page}/it");
        Assert.Equal("/security/languages/it?handler=Download", page.QuerySelector("a#download")!.GetAttribute("href"));

        using var r = await owner.GetAsync($"{Page}/it?handler=Download", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment; filename=\"it.json\"", string.Join("", r.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsByteArrayAsync(Ct));
        var root = doc.RootElement;
        Assert.Equal("it", root.GetProperty("code").GetString());
        Assert.Equal(Catalog(f).PageKeys(), root.GetProperty("page").EnumerateObject().Select(p => p.Name));
        Assert.Equal(Catalog(f).EmailKeys(), root.GetProperty("email").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Benvenuto", root.GetProperty("page").GetProperty("Home.Title").GetString());
        Assert.Equal("Altro", root.GetProperty("email").GetProperty("Reason.Other").GetString());
        foreach (var key in Catalog(f).PageKeys())
            Assert.Equal(Catalog(f).English(key), root.GetProperty("english").GetProperty("page").GetProperty(key).GetString());
        foreach (var key in Catalog(f).EmailKeys())
            Assert.Equal(Catalog(f).EnglishEmail(key), root.GetProperty("english").GetProperty("email").GetProperty(key).GetString());
    }

    [Fact]
    public async Task Upload_with_problems_saves_nothing()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Home.Title"] = "Ciao" });
        var owner = await OwnerAsync(f);
        var file = FileOf(new
        {
            code = "it",
            page = new Dictionary<string, object> { ["Home.Title"] = "Benvenuto", ["Home.Intro"] = "Nuovo", ["Bots.Question"] = "Quanto fa {0}?", ["Nope.Key"] = "x" },
        });
        using var r = await UploadAsync(owner, "it", file);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var list = page.QuerySelector("#upload-problems")!;
        Assert.Contains("Bots.Question", list.TextContent);
        Assert.Contains("Nope.Key", list.TextContent);
        var stored = Store(f).Get("it")!;
        Assert.Equal("Ciao", stored.Page["Home.Title"]);
        Assert.False(stored.Page.ContainsKey("Home.Intro"));
        Assert.DoesNotContain("language-uploaded", Audit(f));

        // Not JSON, too big, or for another language: also nothing.
        using (var bad = await UploadAsync(owner, "it", Encoding.UTF8.GetBytes("not json")))
            Assert.NotNull(Parser.ParseDocument(await bad.Content.ReadAsStringAsync(Ct)).QuerySelector("#upload-problems"));
        using (var big = await UploadAsync(owner, "it", new byte[LanguageFile.MaxBytes + 1]))
        {
            Assert.Equal(HttpStatusCode.OK, big.StatusCode);
            var problems = Parser.ParseDocument(await big.Content.ReadAsStringAsync(Ct)).QuerySelector("#upload-problems");
            Assert.NotNull(problems);
            Assert.Contains("1 MB", problems.TextContent);
        }
        using (var other = await UploadAsync(owner, "it", FileOf(new { code = "es", page = new { Home_Title = "Hola" } })))
            Assert.NotNull(Parser.ParseDocument(await other.Content.ReadAsStringAsync(Ct)).QuerySelector("#upload-problems"));
        Assert.Equal("Ciao", Store(f).Get("it")!.Page["Home.Title"]);
    }

    [Fact]
    public async Task Upload_without_a_file_says_so()
    {
        await using var f = Factory();
        Add(f, "it");
        var owner = await OwnerAsync(f);
        var url = $"{Page}/it";
        var content = new MultipartFormDataContent { { new StringContent(await TokenAsync(owner, url)), "__RequestVerificationToken" } };
        using var r = await owner.PostAsync($"{url}?handler=Upload", content, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#upload-problems"));
    }

    [Fact]
    public async Task Upload_merges()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Home.Title"] = "Ciao", ["Home.Intro"] = "Vecchio" }, new() { ["Reason.Rules"] = "Regole" });
        var owner = await OwnerAsync(f);
        var file = FileOf(new
        {
            code = "it",
            page = new Dictionary<string, string> { ["Home.Intro"] = "Nuovo", ["Home.Title"] = "" },
            email = new Dictionary<string, string> { ["Reason.Other"] = "Altro" },
            english = new { page = new { x = 1 } },
        });
        using var r = await UploadAsync(owner, "it", file);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var stored = Store(f).Get("it")!;
        Assert.Equal("Ciao", stored.Page["Home.Title"]);   // empty in the file: kept
        Assert.Equal("Nuovo", stored.Page["Home.Intro"]);  // given: replaced
        Assert.Equal("Regole", stored.Email["Reason.Rules"]);
        Assert.Equal("Altro", stored.Email["Reason.Other"]);
        Assert.Contains("language-uploaded", Audit(f));

        // The downloaded file uploads back as it is.
        var downloaded = await owner.GetByteArrayAsync($"{Page}/it?handler=Download", Ct);
        using (var back = await UploadAsync(owner, "it", downloaded))
            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal(stored.Page.OrderBy(p => p.Key), Store(f).Get("it")!.Page.OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Upload_checks_an_email_against_the_half_already_saved()
    {
        // Saved (by a hand edit of the file; the site's checks don't make this): {Link} in the subject, not in the body. The file
        // gives only a subject without {Link}: checked against the English body it passes, but merged with the saved body the
        // email would have no link.
        await using var f = Factory();
        var bodyWithoutLink = Catalog(f).EnglishEmail("confirm-email.Body").Replace("{Link}", "", StringComparison.Ordinal);
        Add(f, "it", email: new() { ["confirm-email.Subject"] = "Conferma su {GridName} {Link}", ["confirm-email.Body"] = bodyWithoutLink });
        var owner = await OwnerAsync(f);
        var file = FileOf(new { code = "it", email = new Dictionary<string, string> { ["confirm-email.Subject"] = "Conferma su {GridName}" } });
        using var r = await UploadAsync(owner, "it", file);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var problems = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#upload-problems")!.TextContent;
        Assert.Contains("confirm-email.Subject", problems);
        Assert.Contains("can't be sent without", problems);
        Assert.Equal("Conferma su {GridName} {Link}", Store(f).Get("it")!.Email["confirm-email.Subject"]);
        Assert.DoesNotContain("language-uploaded", Audit(f));
    }

    [Fact]
    public async Task Delete_refuses_an_offered_language()
    {
        await using var f = Factory(offered: [.. Languages.BuiltIn, "it"]);
        Add(f, "it");
        var owner = await OwnerAsync(f);
        var page = await DocAsync(owner, Page);
        Assert.Equal("Yes", page.QuerySelector("#languages tr[data-code='it'] .offered")!.TextContent.Trim());
        using var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it", ["confirm"] = "true" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Turn it off", after.QuerySelector(".error")!.TextContent);
        Assert.NotNull(Store(f).Get("it"));
        Assert.DoesNotContain("language-deleted", Audit(f));
    }

    [Fact]
    public async Task Delete_needs_confirmation_and_shows_residents_count()
    {
        await using var f = Factory();
        Add(f, "it");
        var residents = f.Services.GetRequiredService<ResidentLanguages>();
        residents.Set(Guid.NewGuid(), "it");
        residents.Set(Guid.NewGuid(), "it");
        var owner = await OwnerAsync(f);

        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it" }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var page = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
            var confirm = page.QuerySelector("#confirm-delete")!;
            Assert.Contains("2 residents", confirm.TextContent);
            Assert.Contains("Italiano", confirm.TextContent);
            Assert.Equal("it", confirm.QuerySelector("input[name=code]")!.GetAttribute("value"));
            Assert.Equal("true", confirm.QuerySelector("input[name=confirm]")!.GetAttribute("value"));
        }
        Assert.NotNull(Store(f).Get("it"));

        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Null(Store(f).Get("it"));
        Assert.Empty(LanguageFiles(f));
        // Their saved choice stays; the pages fall back to the default for them.
        Assert.Equal(2, residents.CountUsing("it"));

        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "de", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "../it", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Delete_takes_the_page_wording_too()
    {
        await using var f = Factory();
        Add(f, "it");
        f.Services.GetRequiredService<DreamGrid.Launcher.Core.Grid.IGridController>().UpdateSettings(s => s with
        {
            Wording = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["IT"] = new Dictionary<string, string> { ["Home.Title"] = "Vecchio" },
                ["de"] = new Dictionary<string, string> { ["Home.Title"] = "Bleibt" },
            },
            Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, Core.Email.EmailTemplateText>>
            {
                ["It"] = new Dictionary<string, Core.Email.EmailTemplateText> { ["confirm-email"] = new("Vecchio", "{Link} {ExpiresIn}", LegalSaved) },
                ["de"] = new Dictionary<string, Core.Email.EmailTemplateText> { ["confirm-email"] = new("Bleibt", "{Link} {ExpiresIn}", LegalSaved) },
            }),
            Legal = Legal(("it", "terms", 2), ("en", "terms", 2)),
        }, writeConfig: false);
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var wording = f.Services.GetRequiredService<Func<LauncherSettings>>()().Wording;
        Assert.DoesNotContain(wording.Keys, k => string.Equals(k, "it", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bleibt", wording["de"]["Home.Title"]);
        // Its email wording and legal texts go too, whatever the case of the key.
        var after = f.Services.GetRequiredService<Func<LauncherSettings>>()();
        Assert.Equal(["de"], after.Email.Templates!.Keys);
        Assert.Equal(["en"], after.Legal.Texts!.Keys);

        // Added again, it starts empty: the old wording doesn't come back.
        using (var r = await PostAsync(owner, Page, "Add", new() { ["code"] = "it" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var page = await DocAsync(owner, "/security/wording?lang=it");
        Assert.Equal("", page.QuerySelector("[data-key='Home.Title'] textarea")!.TextContent.Trim());
        Assert.DoesNotContain(f.Services.GetRequiredService<Func<LauncherSettings>>()().Wording.Keys, k => string.Equals(k, "it", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Every_action_audited_without_text()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using (var r = await PostAsync(owner, Page, "Add", new() { ["code"] = "it" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using (var r = await PostAsync(owner, $"{Page}/it", "Save", new() { ["page.Home.Title"] = "SecretWords" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using (var r = await UploadAsync(owner, "it", FileOf(new { code = "it", page = new Dictionary<string, string> { ["Home.Intro"] = "SecretUpload" } })))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using (var r = await owner.GetAsync($"{Page}/it?handler=Download", Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var lines = Audit(f).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var action in new[] { "language-added", "language-translated", "language-uploaded", "language-downloaded", "language-deleted" })
        {
            var line = Assert.Single(lines, l => l.Contains(action, StringComparison.Ordinal));
            Assert.Contains("\"it\"", line);
        }
        var log = Audit(f);
        Assert.DoesNotContain("SecretWords", log);
        Assert.DoesNotContain("SecretUpload", log);
    }

    [Fact]
    public async Task Translate_page_404s_for_a_built_in_or_unknown_code()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        foreach (var code in new[] { "de", "en", "xx", "it", "EN-us", "%2E%2E%2Fit" })
        {
            using var r = await owner.GetAsync($"{Page}/{code}", Ct);
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{code}: {r.StatusCode}");
            using var d = await owner.GetAsync($"{Page}/{code}?handler=Download", Ct);
            Assert.True(d.StatusCode == HttpStatusCode.NotFound, $"{code} download: {d.StatusCode}");
        }
        using (var r = await PostAsync(owner, $"{Page}/de", "Save", new() { ["page.Home.Title"] = "x" }, tokenFrom: Page))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(LanguageFiles(f));
    }

    [Fact]
    public async Task Markup_is_shown_as_text()
    {
        await using var f = Factory(offered: [.. Languages.BuiltIn, "it"]);
        Add(f, "it", new() { ["Home.Title"] = "<script>alert(1)</script>", ["Home.Intro"] = "<b>bold</b>" });
        var owner = await OwnerAsync(f);
        using (var r = await owner.GetAsync($"{Page}/it", Ct))
        {
            var html = await r.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        }

        // On a resident page in that language too.
        var visitor = f.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/home");
        request.Headers.Add("Accept-Language", "it");
        using var home = await visitor.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var text = await home.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<b>bold</b>", text);
        Assert.DoesNotContain("<script>alert(1)</script>", text);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", text);
    }

    [Fact]
    public void Menu_shows_the_item_to_the_owner_only()
    {
        var s = TestSettings.Create() with { Plan = OwnerPlan.Local };
        var items = StaffMenu.For(StaffRole.Owner, s, true).Single(g => g.Text == "Guest pages").Items.ToList();
        var languages = items.Single(i => i.Text == "Languages");
        Assert.Equal("/security/languages", languages.Href);
        Assert.Equal(items.FindIndex(i => i.Text == "Page wording") + 1, items.IndexOf(languages));
        Assert.DoesNotContain("Languages", StaffMenu.For(StaffRole.Admin, s, true).SelectMany(g => g.Items).Select(i => i.Text));
    }

    // ---- choosing the offered languages and the default (Review Focus 4) ----

    private static async Task<HttpResponseMessage> ChooseAsync(HttpClient client, IEnumerable<string> offered, string @default, bool confirm = false)
    {
        List<KeyValuePair<string, string>> form = [new("__RequestVerificationToken", await TokenAsync(client, Page)), new("default", @default)];
        form.AddRange(offered.Select(c => new KeyValuePair<string, string>("offered", c)));
        if (confirm) form.Add(new("confirm", "true"));
        return await client.PostAsync($"{Page}?handler=Choose", new FormUrlEncodedContent(form), Ct);
    }

    private static LanguageSettings Saved(LauncherFactory f) => f.Store.LoadOrCreate().Languages;

    /// <summary>The saved languages as one comparable string: the offered codes, then the default.</summary>
    private static string Snapshot(LauncherFactory f) => string.Join(",", Saved(f).Offered) + " | " + Saved(f).Default;

    /// <summary>The saved legal texts as one comparable string.</summary>
    private static string LegalSnapshot(LauncherFactory f) => System.Text.Json.JsonSerializer.Serialize(f.Store.LoadOrCreate().Legal);

    private static async Task<IHtmlDocument> HtmlAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static readonly DateTimeOffset LegalSaved = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Legal texts by language and page, each at the given version; the owner's tick is on.</summary>
    private static LegalSettings Legal(params (string Language, string Page, int Version)[] texts) => new(
        texts.GroupBy(t => t.Language).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, LegalText>)g.ToDictionary(t => t.Page, t => new LegalText($"Test {t.Language} {t.Page}.", t.Version, LegalSaved))),
        true,
        LegalSaved);

    [Fact]
    public async Task The_table_is_one_form_with_offered_and_default()
    {
        await using var f = Factory(["en", "de", "fr"]);
        var page = await DocAsync(await OwnerAsync(f), Page);

        var form = page.QuerySelector("form#choose")!;
        Assert.Contains("handler=Choose", form.GetAttribute("action"));
        var boxes = page.QuerySelectorAll("#languages input[name=offered][form=choose]").ToList();
        Assert.Equal(Languages.BuiltIn, boxes.Select(b => b.GetAttribute("value")));
        Assert.Equal(["en", "de", "fr"], boxes.Where(b => b.HasAttribute("checked")).Select(b => b.GetAttribute("value")));
        var radios = page.QuerySelectorAll("#languages input[name=default][form=choose]").ToList();
        Assert.Equal(["en"], radios.Where(r => r.HasAttribute("checked")).Select(r => r.GetAttribute("value")));
        Assert.NotNull(page.QuerySelector("button[form=choose][type=submit]"));
    }

    [Fact]
    public async Task Choosing_saves_and_audits()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);

        using var r = await ChooseAsync(owner, ["en", "de", "fr"], "de");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(Page, r.Headers.Location!.OriginalString);
        Assert.Equal(["en", "de", "fr"], Saved(f).Offered);
        Assert.Equal("de", Saved(f).Default);
        var audit = Audit(f);
        Assert.Contains("languages-offered-changed", audit);
        Assert.Contains("en,de,fr", audit);
        Assert.Contains("language-default-changed", audit);
        var target = audit.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("action").GetString() == "language-default-changed").GetProperty("target").GetString();
        Assert.Equal("en → de", target);
        Assert.Contains("Saved.", (await DocAsync(owner, Page)).QuerySelector("[role=status]")!.TextContent);
    }

    [Theory]
    [InlineData("xx", "en")]
    [InlineData("../x", "en")]
    [InlineData("it", "en")] // not added
    [InlineData("en", "")]
    [InlineData("en", "zz")]
    public async Task A_code_not_on_the_list_is_400(string offered, string @default)
    {
        await using var f = Factory();
        var before = Snapshot(f);

        using var r = await ChooseAsync(await OwnerAsync(f), [offered], @default);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task Default_must_be_offered()
    {
        await using var f = Factory();
        var before = Snapshot(f);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), ["en", "fr"], "de"));

        Assert.Equal("Turn on Deutsch before making it the default.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Equal(before, Snapshot(f));
        Assert.DoesNotContain("languages-offered-changed", Audit(f));
    }

    [Fact]
    public async Task Turning_off_the_default_is_refused()
    {
        await using var f = Factory();
        var before = Snapshot(f);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), ["de", "fr"], "en"));

        Assert.Equal("Turn on English before making it the default.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task Incomplete_added_language_cannot_be_turned_on()
    {
        await using var f = Factory();
        Add(f, "it", new() { ["Home.Title"] = "Benvenuto" });
        var before = Snapshot(f);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), ["en", "it"], "en"));

        Assert.Equal($"Italiano still has {Catalog(f).Missing("it")} texts to translate.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task A_complete_added_language_can_be_turned_on()
    {
        await using var f = Factory();
        var c = Catalog(f);
        Add(f, "it", c.PageKeys().ToDictionary(k => k, c.English), c.EmailKeys().ToDictionary(k => k, c.EnglishEmail));

        using var r = await ChooseAsync(await OwnerAsync(f), ["en", "it"], "en");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(["en", "it"], Saved(f).Offered);
    }

    [Fact]
    public async Task Added_language_already_on_stays_on_with_new_keys()
    {
        // After an update adds texts the file lacks, Italian is still on: saving the list again keeps it on.
        await using var f = Factory(["en", "de", "it"]);
        Add(f, "it", new() { ["Home.Title"] = "Benvenuto" });
        Assert.True(Catalog(f).Missing("it") > 0);

        using var r = await ChooseAsync(await OwnerAsync(f), ["en", "it"], "en");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(["en", "it"], Saved(f).Offered);
    }

    [Fact]
    public async Task New_default_needs_up_to_date_legal_pages()
    {
        await using var f = Factory(legal: Legal(("en", "terms", 3), ("en", "privacy", 2), ("en", "cookies", 1), ("de", "terms", 3), ("de", "privacy", 1)));
        var before = Snapshot(f);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "de"));

        var alert = page.QuerySelector("[role=alert]")!;
        Assert.StartsWith("Write the Privacy and Cookies pages in Deutsch first", alert.TextContent.Trim());
        Assert.Equal("/security/legal?lang=de", alert.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task Default_change_keeps_terms_acceptances()
    {
        await using var f = Factory(legal: Legal(("en", "terms", 3), ("en", "privacy", 1), ("en", "cookies", 1),
            ("de", "terms", 3), ("de", "privacy", 1), ("de", "cookies", 1)));
        f.Services.GetRequiredService<DreamGrid.Launcher.Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 3, "203.0.113.9");
        var before = LegalSnapshot(f);

        using var r = await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "de");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("de", Saved(f).Default);
        Assert.Equal(before, LegalSnapshot(f)); // nothing renumbered
        Assert.Equal(3, Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "de")!.Version);
        // The guest who accepted version 3 in English is not asked again.
        var guest = f.CreateClient(new() { AllowAutoRedirect = false });
        (await guest.GetAsync("/__test/guest-signin", Ct)).Dispose();
        using var home = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Turning_off_a_used_language_asks_first_and_then_residents_get_the_default()
    {
        await using var f = Factory();
        var resident = Guid.NewGuid();
        f.Services.GetRequiredService<ResidentLanguages>().Set(resident, "de");
        var owner = await OwnerAsync(f);
        Assert.Equal("de", await PageLanguageAsync(f, "de"));
        var before = Snapshot(f);

        var page = await HtmlAsync(await ChooseAsync(owner, ["en", "es", "fr", "pt", "ru"], "en"));

        var confirm = page.QuerySelector("form#confirm-choose")!;
        Assert.Contains("1 resident uses Deutsch. They will see English instead.", confirm.TextContent);
        Assert.Equal(["en", "es", "fr", "pt", "ru"], confirm.QuerySelectorAll("input[name=offered]").Select(i => i.GetAttribute("value")));
        Assert.Equal("en", confirm.QuerySelector("input[name=default]")!.GetAttribute("value"));
        Assert.Equal("true", confirm.QuerySelector("input[name=confirm]")!.GetAttribute("value"));
        Assert.Equal(before, Snapshot(f)); // nothing saved yet

        using var r = await ChooseAsync(owner, ["en", "es", "fr", "pt", "ru"], "en", confirm: true);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("de", Saved(f).Offered);
        // Their saved choice is kept, but the pages and the emails are in the default now.
        Assert.Equal("de", f.Services.GetRequiredService<ResidentLanguages>().Get(resident));
        Assert.Equal("en", await PageLanguageAsync(f, "de"));
        var language = f.Services.GetRequiredService<DreamGrid.Launcher.Email.EmailLanguage>().ForResident(resident);
        Assert.Equal("en", language);
        var email = Core.Email.EmailTemplates.Current(f.Store.LoadOrCreate(), Core.Email.EmailTemplates.PasswordChanged, language, Store(f));
        Assert.Equal(Core.Email.EmailTemplates.Default(Core.Email.EmailTemplates.PasswordChanged, "en").Subject, email.Subject);
    }

    [Fact]
    public async Task Turning_off_an_unused_language_needs_no_confirmation()
    {
        await using var f = Factory();

        using var r = await ChooseAsync(await OwnerAsync(f), ["en", "de"], "en");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(["en", "de"], Saved(f).Offered);
    }

    private static async Task<HttpStatusCode> GuestHomeAsync(LauncherFactory f)
    {
        var guest = f.CreateClient(new() { AllowAutoRedirect = false });
        (await guest.GetAsync("/__test/guest-signin", Ct)).Dispose();
        using var home = await guest.GetAsync("/guest", Ct);
        return home.StatusCode;
    }

    [Fact]
    public async Task Path_B_a_default_with_an_older_terms_than_the_English_stand_in_is_refused()
    {
        // The default is German with no legal records, so English Terms v5 stands in. French has Terms v2: as the default it would
        // take Terms back to v2, and a guest who accepted v2 would be waved through.
        await using var f = Factory(legal: Legal(("en", "terms", 5), ("fr", "terms", 2)) with { OperatorResponsible = false });
        f.Store.Save(f.Store.LoadOrCreate() is var s ? s with { Languages = s.Languages with { Default = "de" } } : null!);
        f.Services.GetRequiredService<DreamGrid.Launcher.Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 2, "203.0.113.9");
        Assert.Equal(5, Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "de")!.Version);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "fr"));

        Assert.StartsWith("Write the Terms page in Fran", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Equal("de", Saved(f).Default);
        Assert.Equal(5, Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "fr")!.Version);
        Assert.Equal(HttpStatusCode.Redirect, await GuestHomeAsync(f));
    }

    [Fact]
    public async Task A_default_without_legal_records_is_allowed_while_English_stands_in()
    {
        // Sign-up can't open; German has no pages, so English keeps standing in at the same numbers.
        await using var f = Factory(legal: Legal(("en", "terms", 5)) with { OperatorResponsible = false });
        f.Services.GetRequiredService<DreamGrid.Launcher.Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 5, "203.0.113.9");

        using var r = await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "de");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(5, Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "de")!.Version);
        Assert.Equal(HttpStatusCode.OK, await GuestHomeAsync(f));
    }

    [Fact]
    public async Task A_hand_edited_default_still_goes_through_rule_4_and_the_audit()
    {
        // The saved default is Italian, added once and since deleted: the pages read English, but the legal source is still "it".
        await using var f = Factory(legal: Legal(("it", "terms", 4), ("en", "terms", 4), ("de", "terms", 2)) with { OperatorResponsible = false });
        f.Store.Save(f.Store.LoadOrCreate() is var s ? s with { Languages = s.Languages with { Default = "it" } } : null!);

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "de"));
        Assert.StartsWith("Write the Terms page in Deutsch first", page.QuerySelector("[role=alert]")!.TextContent.Trim());

        using var r = await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "en");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("it \u2192 en", Audit(f).Replace("\\u2192", "\u2192"));
    }

    /// <summary>The <c>lang</c> of the front page for a visitor whose language cookie is <paramref name="cookie"/>.</summary>
    private static async Task<string?> PageLanguageAsync(LauncherFactory f, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/home");
        request.Headers.Add("Cookie", "dg-lang=" + cookie);
        using var r = await f.CreateClient(new() { AllowAutoRedirect = false }).SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct)).DocumentElement.GetAttribute("lang");
    }

    [Fact]
    public async Task A_cleared_source_does_not_let_a_stale_translation_become_the_source()
    {
        // English Terms was v5 and is cleared; the German translation is still at v2. With the gate closed, German as the default
        // would take the Terms from 5 to nothing to 2.
        var legal = new LegalSettings(new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText> { ["terms"] = new("", 5, LegalSaved) },
            ["de"] = new Dictionary<string, LegalText> { ["terms"] = new("Test de terms.", 2, LegalSaved) },
        });
        await using var f = Factory(legal: legal);
        f.Services.GetRequiredService<DreamGrid.Launcher.Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 2, "203.0.113.9");

        var page = await HtmlAsync(await ChooseAsync(await OwnerAsync(f), Languages.BuiltIn, "de"));

        Assert.StartsWith("Write the Terms page in Deutsch first", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Equal("en", Saved(f).Default);
        Assert.Null(Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "de"));
    }

    [Fact]
    public async Task Deleting_a_language_never_lets_its_legal_numbers_be_used_again()
    {
        // Italian Terms was v6 and is cleared; English Terms is v2. Deleting Italian takes its records, not the number.
        await using var f = Factory(legal: new LegalSettings(new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["it"] = new Dictionary<string, LegalText> { ["terms"] = new("", 6, LegalSaved) },
            ["en"] = new Dictionary<string, LegalText> { ["terms"] = new("Test en terms.", 2, LegalSaved) },
        }));
        Add(f, "it");
        f.Services.GetRequiredService<DreamGrid.Launcher.Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 6, "203.0.113.9");
        var owner = await OwnerAsync(f);

        using (var r = await PostAsync(owner, Page, "Delete", new() { ["code"] = "it", ["confirm"] = "true" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(["en"], f.Store.LoadOrCreate().Legal.Texts!.Keys);
        Assert.Equal(6, f.Store.LoadOrCreate().Legal.HighestVersion!["terms"]);

        using (var r = await PostAsync(owner, "/security/legal", "Save", new() { ["Texts[terms]"] = "Test en terms, new." }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(7, Core.Guests.LegalPages.Get(f.Store.LoadOrCreate(), "terms", "en")!.Version);
        Assert.Equal(HttpStatusCode.Redirect, await GuestHomeAsync(f)); // the old v6 acceptor is asked again
    }
}
