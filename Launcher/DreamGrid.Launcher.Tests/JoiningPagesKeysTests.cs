using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The pages for joining (plan 16a, task 6: front page, sign-up, the guest home, terms, regions and account) take every word from
/// <c>ResidentText</c>: rendered in English, no page shows a raw key, and every key the group's source files use has an entry.
/// Also: staff pages stay English whatever the operating system's language.
/// </summary>
public sealed class JoiningPagesKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex RawKey = new(@"[A-Z][a-zA-Z]+\.[A-Z][a-zA-Z]+", RegexOptions.Compiled);

    private static readonly Guid AnnId = Guid.Parse("a0000000-0000-0000-0000-0000000000a2");
    private static readonly Guid SunsetId = Guid.Parse("50000000-0000-0000-0000-000000000003");
    private const string Old = "old-password-1";

    private static LauncherFactory Factory(bool bots)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = SignUpsTests.WrittenLegal(),
            SignUp = new SignUpSettings(Open: true),
            Bots = bots ? new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: true, ImageCaptcha: true)
                        : new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
            Programs = [new ProgramSettings("Sunset", 8005, 8012, "ubODE")],
            Regions = [new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8005)],
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, Old, AnnId, email: "ann@example.com", created: new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero));
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");

        static ComponentStatus Process(ProcessState state) => new("x", state, null, null, null, null);
        f.Grid.Status = new GridStatus(Process(ProcessState.Running), Process(ProcessState.Running),
            [new ProgramStatus("Sunset", Process(ProcessState.Stopped), [SunsetId])],
            [new RegionStatus(SunsetId, "Sunset Shore", "Sunset", RegionState.Failed, true, 1002, 1000, 256, [])],
            true, "http://127.0.0.1:8002");
        f.Services.GetRequiredService<RegionRights>().Grant(SunsetId, AnnId, "Ann Smith");
        return f;
    }

    private static HttpClient Client(LauncherFactory f) =>
        f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost") });

    private static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

    private static async Task<IHtmlDocument> GetAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ParseAsync(r);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string formUrl, Dictionary<string, string> form)
    {
        var page = await GetAsync(client, formUrl);
        form["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value")
            ?? throw new InvalidOperationException("no token");
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>No raw key in the title, the visible text of main, or its placeholder, aria-label, title and alt words.</summary>
    private static void NoRawKeys(IHtmlDocument page, string what)
    {
        var main = page.QuerySelector("main") ?? throw new InvalidOperationException(what + ": no <main>");
        Assert.False(string.IsNullOrWhiteSpace(main.TextContent), what + ": main is empty");
        var words = new List<string> { page.Title ?? "", main.TextContent };
        foreach (var e in main.QuerySelectorAll("*"))
            foreach (var a in new[] { "placeholder", "aria-label", "title", "alt" })
                if (e.GetAttribute(a) is { } v) words.Add(v);
        foreach (var w in words)
            Assert.False(RawKey.IsMatch(w), $"{what}: '{RawKey.Match(w).Value}' looks like a key with no English text (in '{w.Trim()}')");
    }

    [Fact]
    public async Task ShowsEveryKeyItUses()
    {
        await using var f = Factory(bots: true);
        var anon = Client(f);

        NoRawKeys(await GetAsync(anon, "/home"), "home");
        var signUp = await GetAsync(anon, "/signup");
        Assert.NotNull(signUp.QuerySelector("#dg-captcha-answer"));
        NoRawKeys(signUp, "sign-up with every bot layer");
        // The hint that digits work comes with the question.
        Assert.Contains("You can type the answer in digits, for example 11.", signUp.QuerySelector(".dg-captcha")!.TextContent);
        // The CAPTCHA's sum, written in the page's words.
        Assert.Matches(@"^What is (one|two|three|four|five|six|seven|eight|nine) plus (one|two|three|four|five|six|seven|eight|nine)\?$",
            signUp.QuerySelector("label[for=dg-captcha-answer]")!.TextContent.Trim());

        // A form that fails the bot check.
        using (var bot = await PostAsync(anon, "/signup", "/signup", new() { ["First"] = "Bea", ["Last"] = "Example" }))
        {
            var page = await ParseAsync(bot);
            Assert.NotNull(page.QuerySelector(".error"));
            NoRawKeys(page, "sign-up, failed bot check");
        }

        // Guest pages, signed in as a resident with a region.
        var ann = Client(f);
        using (var ok = await PostAsync(ann, "/guest/sign-in", "/guest/sign-in", new() { ["Name"] = "Ann Smith", ["Password"] = Old }))
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var terms = await GetAsync(ann, "/guest/terms");
        Assert.NotNull(terms.QuerySelector("form button"));
        NoRawKeys(terms, "terms");
        using (var stale = await PostAsync(ann, "/guest/terms", "/guest/terms", new() { ["Version"] = "3", ["ReturnUrl"] = "/guest" }))
        {
            var page = await ParseAsync(stale);
            Assert.Contains("The terms changed while you were reading.", page.QuerySelector(".alert-warning")!.TextContent);
            NoRawKeys(page, "terms, a version that changed");
        }
        using (var accept = await PostAsync(ann, "/guest/terms", "/guest/terms", new() { ["Version"] = "4", ["ReturnUrl"] = "/guest" }))
            Assert.Equal(HttpStatusCode.Redirect, accept.StatusCode);
        NoRawKeys(await GetAsync(ann, "/guest/terms"), "terms, accepted");

        NoRawKeys(await GetAsync(ann, "/guest"), "guest home");
        var regions = await GetAsync(ann, "/guest/regions");
        Assert.NotNull(regions.QuerySelector("section.card button"));
        NoRawKeys(regions, "regions");
        using (var restart = await PostAsync(ann, "/guest/regions?handler=restart", "/guest", new() { ["id"] = SunsetId.ToString() }))
            Assert.Equal(HttpStatusCode.Redirect, restart.StatusCode);
        var again = await GetAsync(ann, "/guest/regions");
        Assert.NotNull(again.QuerySelector(".notice"));
        NoRawKeys(again, "regions, after a restart");

        NoRawKeys(await GetAsync(ann, "/guest/account"), "account");
        using (var wrong = await PostAsync(ann, "/guest/account?handler=Password", "/guest/account",
            new() { ["Current"] = "wrong-wrong", ["Password"] = "x", ["Again"] = "y" }))
        {
            var page = await ParseAsync(wrong);
            Assert.NotNull(page.QuerySelector(".error"));
            NoRawKeys(page, "account, a wrong current password");
        }
        using (var weak = await PostAsync(ann, "/guest/account?handler=Password", "/guest/account",
            new() { ["Current"] = Old, ["Password"] = "short", ["Again"] = "short" }))
        {
            var page = await ParseAsync(weak);
            Assert.Contains("Use at least 8 characters.", page.QuerySelector(".error")!.TextContent);
            NoRawKeys(page, "account, a weak new password");
        }
        using (var email = await PostAsync(ann, "/guest/account?handler=Email", "/guest/account",
            new() { ["Current"] = Old, ["NewEmail"] = "not-an-address" }))
        {
            var page = await ParseAsync(email);
            Assert.Contains("That doesn't look like an email address.", page.QuerySelector(".error")!.TextContent);
            NoRawKeys(page, "account, a bad email address");
        }
    }

    [Fact]
    public async Task SignUpMessagesAreInResidentText()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig.F);

        async Task<IHtmlDocument> Post(string first, string last, string email, string password)
        {
            using var r = await PostAsync(client, "/signup", "/signup", new()
            {
                ["First"] = first, ["Last"] = last, ["Email"] = email, ["Password"] = password, ["Again"] = password,
                ["website"] = "", ["AcceptTerms"] = "true",
            });
            return await ParseAsync(r);
        }

        NoRawKeys(await Post("", "Example", "bea@example.com", "tall-green-kettle"), "sign-up, no first name");
        NoRawKeys(await Post("Bea!", "Example", "bea@example.com", "tall-green-kettle"), "sign-up, bad characters");
        NoRawKeys(await Post("Bea", "Example", "bea@example.com", "short"), "sign-up, short password");
        NoRawKeys(await Post("Bea", "Example", "nope", "tall-green-kettle"), "sign-up, bad email");
        NoRawKeys(await Post("Ann", "Smith", "bea@example.com", "tall-green-kettle"), "sign-up, name taken");
        var done = await Post("Bea", "Example", "bea@example.com", "tall-green-kettle");
        Assert.NotNull(done.QuerySelector("#sign-up-reply"));
        NoRawKeys(done, "sign-up, check your email");
    }

    [Fact]
    public async Task SignUpClosedShowsItsWords()
    {
        await using var rig = SignUpsTests.Factory(open: false);
        NoRawKeys(await GetAsync(Client(rig.F), "/signup"), "sign-up, closed");
    }

    [Fact]
    public async Task EveryKeyTheGroupUsesHasAnEnglishEntry()
    {
        await using var f = Factory(bots: false);
        var t = f.Services.GetRequiredService<IStringLocalizer<ResidentText>>();
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DreamGrid.Launcher", "Pages", "Guest", "Terms.cshtml")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        Assert.NotNull(root);
        var app = Path.Combine(root, "DreamGrid.Launcher");
        string[] files =
        [
            "Pages/Home.cshtml", "Pages/Home.cshtml.cs", "Pages/SignUp.cshtml", "Pages/SignUp.cshtml.cs",
            "Pages/Guest/Index.cshtml", "Pages/Guest/Index.cshtml.cs", "Pages/Guest/Terms.cshtml", "Pages/Guest/Terms.cshtml.cs",
            "Pages/Guest/Regions.cshtml", "Pages/Guest/Regions.cshtml.cs", "Pages/Guest/Account.cshtml", "Pages/Guest/Account.cshtml.cs",
            "Pages/Shared/_BotFields.cshtml", "Guests/SignUps.cs", "Guests/AccountChanges.cs", "Languages/PasswordFaultText.cs",
            "Languages/ResidentHtml.cs",
        ];
        var keys = files.Select(p => Path.Combine(app, p)).SelectMany(p =>
                Regex.Matches(File.ReadAllText(p), @"(?:\b[tT]\[|\bnew\(|\.With\()""([A-Z][A-Za-z]+\.[A-Za-z0-9]+)""(?!\s*\+)").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(keys.Count > 60, "found only " + keys.Count + " keys; the scan is not finding them");
        Assert.All(keys, k => Assert.False(t[k].ResourceNotFound, k + " has no entry in ResidentText.resx"));
        // The number words the CAPTCHA question is built from.
        for (var n = 1; n <= 9; n++) Assert.False(t["Bots.Number" + n].ResourceNotFound, "Bots.Number" + n);
    }
}

[CollectionDefinition("Process culture", DisableParallelization = true)]
public sealed class ProcessCultureCollection;

/// <summary>Changes the process-wide UI culture, so it runs alone: no other test is running while it is set.</summary>
[Collection("Process culture")]
public sealed class StaffPagesEnglishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Staff_pages_stay_English_whatever_the_system_language()
    {
        var before = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            // What a German operating system would give the process; starting the host must put it back to English.
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("de");
            await using var f = new LauncherFactory(s => s with
            {
                GridName = "Avalon",
                SetupToken = null,
                Plan = OwnerPlan.Local,
                Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true,
                    Owner: new OwnerInfo("Test", "Owner", Guid.NewGuid()), NetworkSet: true),
                Recovery = Recovery.Level(GridKind.HomeLab).Rules,
            })
            {
                Before = TestAuth.Apply,
            };
            var client = f.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal("en", CultureInfo.DefaultThreadCurrentUICulture?.Name);

            // /setup/connect is a staff page that shows the login address through _LoginAddress, which reads ResidentText.
            (await client.GetAsync("/__test/signin", Ct)).Dispose();
            var page = await PageAsync(client, "/setup/connect");
            Assert.Equal("Copy", page.QuerySelector("#copy-uri")!.TextContent.Trim());
            Assert.Equal("Copied", page.QuerySelector("#copy-uri")!.GetAttribute("data-copied"));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = before;
        }
    }
}
