using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest layout's language, direction and picker, and the /language page that remembers a choice.</summary>
public sealed class LanguagePickerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private const string Page = "/support";

    private static LauncherFactory Make(Func<LanguageSettings, LanguageSettings>? languages = null, IPAddress? caller = null,
        IReadOnlyList<string>? adminNetworks = null)
    {
        var f = new LauncherFactory(s => s with
        {
            Security = adminNetworks is null ? s.Security : s.Security with { AdminNetworks = adminNetworks },
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
            Languages = languages is null ? s.Languages : languages(s.Languages),
        })
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
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) =>
        f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost") });

    private static async Task<IHtmlDocument> GetAsync(HttpClient c, string url, string? langCookie = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (langCookie is not null) req.Headers.Add("Cookie", "dg-lang=" + langCookie);
        using var r = await c.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string code, string? returnUrl, bool token = true, string from = Page)
    {
        var form = new Dictionary<string, string> { ["code"] = code };
        if (returnUrl is not null) form["returnUrl"] = returnUrl;
        if (token) form["__RequestVerificationToken"] = await PickerTokenAsync(c, from);
        return await c.PostAsync("/language", new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>The antiforgery token in the header picker of <paramref name="page"/>, as that page made it for this browser.</summary>
    private static async Task<string> PickerTokenAsync(HttpClient c, string page) =>
        (await GetAsync(c, page)).QuerySelector("form[action='/language'] input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    private static async Task<HttpClient> GuestAsync(LauncherFactory f)
    {
        var c = Client(f);
        using var s = await c.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, s.StatusCode);
        return c;
    }

    private static string[] SetCookies(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.Where(h => h.StartsWith("dg-lang=", StringComparison.Ordinal)).ToArray() : [];

    [Fact]
    public async Task Layout_sets_lang_and_dir()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), Page, "de");
        Assert.Equal("de", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("ltr", page.DocumentElement.GetAttribute("dir"));
    }

    [Fact]
    public async Task Layout_words_come_from_the_resources_not_the_key_names()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), Page);
        Assert.Equal("Ask for help", page.QuerySelector("#help-link")!.TextContent.Trim());
    }

    // lang comes from the culture the page is rendered in (CurrentUICulture), so this proves the culture reached the render, and
    // that concurrent requests in two languages do not see each other's.
    [Fact]
    public async Task Layout_takes_its_language_from_the_render_culture_and_concurrent_requests_do_not_leak()
    {
        await using var f = Make();
        var client = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false }); // the host is built here, once
        await GetAsync(client, Page);
        var codes = Enumerable.Range(0, 24).Select(i => i % 2 == 0 ? "de" : "fr").ToArray();
        var pages = await Task.WhenAll(codes.Select(async code =>
        {
            await Task.Yield();
            var doc = await GetAsync(client, Page, code);
            return (code, lang: doc.DocumentElement.GetAttribute("lang"));
        }));
        Assert.All(pages, p => Assert.Equal(p.code, p.lang));
    }

    [Fact]
    public async Task Layout_loads_rtl_bootstrap_for_a_right_to_left_language()
    {
        // The built-in list has no right-to-left language; a hand-edited default is still offered (OfferedOrDefault).
        await using var f = Make(l => l with { Default = "ar" });
        var page = await GetAsync(Client(f), Page);
        Assert.Equal("rtl", page.DocumentElement.GetAttribute("dir"));
        Assert.Equal("ar", page.DocumentElement.GetAttribute("lang"));
        var sheets = page.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")).ToList();
        Assert.Contains("/lib/bootstrap/bootstrap.rtl.min.css", sheets);
        Assert.DoesNotContain("/lib/bootstrap/bootstrap.min.css", sheets);
    }

    [Fact]
    public async Task Layout_loads_ltr_bootstrap_otherwise()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), Page, "de");
        var sheets = page.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")).ToList();
        Assert.Contains("/lib/bootstrap/bootstrap.min.css", sheets);
        Assert.DoesNotContain("/lib/bootstrap/bootstrap.rtl.min.css", sheets);
    }

    [Fact]
    public async Task Picker_lists_offered_languages_in_their_own_names()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), Page, "de");
        var options = page.QuerySelectorAll("form[action='/language'] select[name=code] option").ToList();
        Assert.Equal(Languages.BuiltIn.ToList(), options.Select(o => o.GetAttribute("value")!).ToList());
        Assert.Contains("Deutsch", options.Select(o => o.TextContent.Trim()));
        Assert.Contains("Русский", options.Select(o => o.TextContent.Trim()));
        Assert.Equal("de", options.Single(o => o.HasAttribute("selected")).GetAttribute("value"));
        Assert.Equal("/support", page.QuerySelector("form[action='/language'] input[name=returnUrl]")!.GetAttribute("value"));
        Assert.Contains("/js/language.js", page.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")));
    }

    [Fact]
    public async Task Picker_is_hidden_when_one_language_is_offered()
    {
        await using var f = Make(l => l with { Offered = ["en"], Default = "en" });
        var page = await GetAsync(Client(f), Page);
        Assert.Null(page.QuerySelector("form[action='/language']"));
    }

    [Fact]
    public async Task Posting_the_picker_sets_the_cookie_and_returns()
    {
        await using var f = Make();
        var c = Client(f);
        using var r = await PostAsync(c, "de", "/guest/sign-in");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/guest/sign-in", r.Headers.Location?.OriginalString);
        var cookie = Assert.Single(SetCookies(r)).ToLowerInvariant();
        Assert.Contains("dg-lang=de", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("max-age=31536000", cookie);
        Assert.Contains("path=/", cookie);
        // The next page is in German.
        Assert.Equal("de", (await GetAsync(c, Page)).DocumentElement.GetAttribute("lang"));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData("de<script>")]
    [InlineData("ar")]
    public async Task Picker_refuses_an_unknown_code(string bad)
    {
        await using var f = Make();
        using var r = await PostAsync(Client(f), bad, "/");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(SetCookies(r));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    public async Task Picker_never_redirects_off_site(string returnUrl)
    {
        await using var f = Make();
        using var r = await PostAsync(Client(f), "fr", returnUrl);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
    }

    // The picker's token is the one its page made, for that page's user: the guest on /guest/* (the Guest policy) and /home
    // (which takes the guest itself). /language has no policy, so it must accept that token, not only one for the staff scheme.
    [Theory]
    [InlineData("/guest/account")]
    [InlineData("/guest/connect")]
    [InlineData("/home")]
    [InlineData("/support")]
    public async Task Picker_works_for_a_signed_in_guest_from_every_resident_page(string from)
    {
        await using var f = Make();
        var c = await GuestAsync(f);
        using var r = await PostAsync(c, "de", from, from: from);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal(from, r.Headers.Location?.OriginalString);
        Assert.Contains("dg-lang=de", Assert.Single(SetCookies(r)));
        Assert.Equal("de", f.Services.GetRequiredService<ResidentLanguages>().Get(TestAuth.GuestId));
    }

    [Fact]
    public async Task Picker_works_for_staff_looking_at_the_front_page()
    {
        await using var f = Make();
        var c = Client(f);
        using (var s = await c.GetAsync("/__test/signin?role=admin", Ct)) Assert.Equal(HttpStatusCode.NoContent, s.StatusCode);
        using var r = await PostAsync(c, "fr", "/home", from: "/home");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Contains("dg-lang=fr", Assert.Single(SetCookies(r)));
    }

    [Fact]
    public async Task Picker_works_anonymously_from_the_support_page()
    {
        await using var f = Make();
        using var r = await PostAsync(Client(f), "es", "/support", from: "/support");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Contains("dg-lang=es", Assert.Single(SetCookies(r)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Picker_needs_the_token_for_a_signed_in_guest(bool guest)
    {
        await using var f = Make();
        var c = guest ? await GuestAsync(f) : Client(f);
        await GetAsync(c, "/home"); // the browser has its antiforgery cookie
        using var r = await PostAsync(c, "de", "/", token: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(SetCookies(r));
        Assert.Null(f.Services.GetRequiredService<ResidentLanguages>().Get(TestAuth.GuestId));
    }

    [Theory]
    [InlineData("/support", false)]
    [InlineData("/home", true)]
    [InlineData("/guest/account", true)]
    public async Task Picker_refuses_a_token_from_another_browser(string from, bool guest)
    {
        await using var f = Make();
        var other = guest ? await GuestAsync(f) : Client(f);
        var token = await PickerTokenAsync(other, from);
        var me = guest ? await GuestAsync(f) : Client(f);
        await GetAsync(me, from); // my own antiforgery cookie, which the other browser's token is not paired with
        var form = new Dictionary<string, string> { ["code"] = "de", ["returnUrl"] = "/", ["__RequestVerificationToken"] = token };
        using var r = await me.PostAsync("/language", new FormUrlEncodedContent(form), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(SetCookies(r));
    }

    [Fact]
    public async Task Picker_works_from_outside_the_admin_networks()
    {
        await using var f = Make(caller: IPAddress.Parse("192.168.1.50"), adminNetworks: ["10.0.0.0/8"]);
        var c = Client(f);
        using (var staff = await c.GetAsync("/login", Ct)) Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode); // the list is on
        using var r = await PostAsync(c, "de", "/home", from: "/home");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Contains("dg-lang=de", Assert.Single(SetCookies(r)));
    }

    [Fact]
    public async Task Picker_saves_the_choice_for_a_signed_in_guest()
    {
        await using var f = Make();
        var c = Client(f);
        using (var s = await c.GetAsync("/__test/guest-signin", Ct)) Assert.Equal(HttpStatusCode.NoContent, s.StatusCode);
        using var r = await PostAsync(c, "de", "/support");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("de", f.Services.GetRequiredService<ResidentLanguages>().Get(TestAuth.GuestId));
    }

    // The German and Russian satellite assemblies load in the web app: the page words are translated, not only lang="de".
    [Fact]
    public async Task Sign_in_page_in_German_has_the_German_title()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), "/guest/sign-in", "de");
        Assert.Equal("Anmelden", page.QuerySelector("h1")!.TextContent.Trim());
        Assert.StartsWith("Anmelden", page.Title);
    }

    [Fact]
    public async Task Forgot_password_page_in_Russian_is_in_Cyrillic()
    {
        await using var f = Make();
        var page = await GetAsync(Client(f), "/guest/forgot-password", "ru");
        var title = page.QuerySelector("h1")!.TextContent.Trim();
        Assert.Equal("Забыли пароль", title);
        Assert.Matches(@"\p{IsCyrillic}", title);
    }

    [Fact]
    public async Task Picker_needs_the_antiforgery_token()
    {
        await using var f = Make();
        using var r = await PostAsync(Client(f), "de", "/", token: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(SetCookies(r));
    }

    [Fact]
    public async Task Getting_the_language_page_is_not_allowed()
    {
        await using var f = Make();
        using var r = await Client(f).GetAsync("/language", Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
    }

    [Fact]
    public void Right_to_left_stylesheet_is_the_same_bootstrap_version_as_the_left_to_right_one()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        var lib = Path.Combine(dir!.FullName, "Launcher", "DreamGrid.Launcher", "wwwroot", "lib", "bootstrap");
        string Version(string file) => Regex.Match(File.ReadAllText(Path.Combine(lib, file)), @"Bootstrap\s+v(\d+\.\d+\.\d+)").Groups[1].Value;
        Assert.Equal("5.3.3", Version("bootstrap.min.css"));
        Assert.Equal(Version("bootstrap.min.css"), Version("bootstrap.rtl.min.css"));
    }
}
