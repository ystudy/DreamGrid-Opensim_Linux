using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Tickets;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The help, legal and welcome pages (plan 16a, task 7) take every word GridGate itself writes from <c>ResidentText</c>: rendered in
/// English, no page shows a raw key, and every key the group's source files use has an entry. The owner's own text is untouched.
/// </summary>
public sealed class HelpLegalWelcomeKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex RawKey = new(@"[A-Z][a-zA-Z]+\.[A-Z][a-zA-Z]+", RegexOptions.Compiled);

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = SignUpsTests.WrittenLegal(),
            SignUp = new SignUpSettings(Open: true),
            Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(new EmailPageTests.FakeTransport()),
        };
        TestAuth.AddStaff(f.Robust);
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> form, string? formUrl = null)
    {
        var page = await GetAsync(client, formUrl ?? url);
        form["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value")
            ?? throw new InvalidOperationException("no token");
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static Dictionary<string, string> Form(string message = "My region won't start since yesterday.", string email = "bea@example.com") => new()
    {
        ["Name"] = "Bea Example", ["Email"] = email, ["Category"] = TicketCategories.Technical,
        ["Subject"] = "Region won't start", ["Message"] = message,
    };

    /// <summary>No raw key in the title, the visible text of <paramref name="root"/>, or its placeholder, aria-label, title and alt words.</summary>
    private static void NoRawKeys(IHtmlDocument page, string what, string rootSelector = "main")
    {
        var root = page.QuerySelector(rootSelector) ?? throw new InvalidOperationException(what + ": no " + rootSelector);
        Assert.False(string.IsNullOrWhiteSpace(root.TextContent), what + ": the page is empty");
        var words = new List<string> { page.Title ?? "", root.TextContent };
        foreach (var e in root.QuerySelectorAll("*"))
            foreach (var a in new[] { "placeholder", "aria-label", "title", "alt" })
                if (e.GetAttribute(a) is { } v) words.Add(v);
        foreach (var w in words)
            Assert.False(RawKey.IsMatch(w), $"{what}: '{RawKey.Match(w).Value}' looks like a key with no English text (in '{w.Trim()}')");
    }

    private static Ticket Ticket(Guid id, string number, TicketState state = TicketState.Open, DateTimeOffset? closed = null) =>
        new(id, number, state, TicketCategories.Technical, "Region won't start", new TicketSender(null, "Bea Example", "bea@example.com"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, closed,
            [
                new TicketMessage(DateTimeOffset.UtcNow, null, null, "Help me please."),
                new TicketMessage(DateTimeOffset.UtcNow, TestAuth.OwnerId, "Test", "Try restarting it."),
            ], [])
        { Evidence = "https://example.com/picture.png" };

    [Fact]
    public async Task SupportPagesShowEveryKeyTheyUse()
    {
        await using var f = Factory();
        var anon = Client(f);

        var form = await GetAsync(anon, "/support");
        Assert.NotNull(form.QuerySelector("#support-form"));
        NoRawKeys(form, "support, form for someone not signed in");

        // The form's own checks, in the page's words.
        using (var bad = await PostAsync(anon, "/support", Form(message: "short"), "/support"))
        {
            var page = await ParseAsync(bad);
            Assert.Contains("Write at least 10 characters about the problem.", page.QuerySelector(".error")!.TextContent);
            NoRawKeys(page, "support, a message that is too short");
        }
        using (var bad = await PostAsync(anon, "/support", Form(email: "not-an-address"), "/support"))
        {
            var page = await ParseAsync(bad);
            Assert.Contains("Write the email address we should answer.", page.QuerySelector(".error")!.TextContent);
            NoRawKeys(page, "support, a bad address");
        }
        using (var ok = await PostAsync(anon, "/support", Form(), "/support"))
        {
            var page = await ParseAsync(ok);
            Assert.NotNull(page.QuerySelector("#support-reply"));
            NoRawKeys(page, "support, check your email");
        }

        // Signed in: sends at once, sees the list, and the fourth open ticket is refused.
        var ann = Client(f);
        (await ann.GetAsync("/__test/guest-signin", Ct)).Dispose();
        for (var i = 0; i < 3; i++)
            using (var sent = await PostAsync(ann, "/support", Form(), "/support"))
                Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        var list = await GetAsync(ann, "/support");
        Assert.NotNull(list.QuerySelector("#my-tickets"));
        NoRawKeys(list, "support, a resident's tickets");
        using (var fourth = await PostAsync(ann, "/support", Form(), "/support"))
        {
            var page = await ParseAsync(fourth);
            Assert.Contains("You already have 3 open tickets. Reply on one of them instead.", page.QuerySelector(".error")!.TextContent);
            NoRawKeys(page, "support, too many open tickets");
        }

        NoRawKeys(await GetAsync(anon, "/support/lost-link"), "lost link");
        using (var lost = await PostAsync(anon, "/support/lost-link", new() { ["Email"] = "bea@example.com" }))
        {
            var page = await ParseAsync(lost);
            Assert.NotNull(page.QuerySelector("#lost-link-reply"));
            NoRawKeys(page, "lost link, sent");
        }

        var confirm = await GetAsync(anon, "/support/confirm?token=x");
        Assert.NotNull(confirm.QuerySelector("#confirm-ticket"));
        NoRawKeys(confirm, "confirm");
        using (var failed = await PostAsync(anon, "/support/confirm", new() { ["Token"] = "x" }, "/support/confirm?token=x"))
        {
            var page = await ParseAsync(failed);
            Assert.NotNull(page.QuerySelector(".error"));
            NoRawKeys(page, "confirm, a link that does not work");
        }

        // The sign-in notice of a grid that can't send email.
        await using var noEmail = new LauncherFactory(s => s with
        {
            GridName = "Avalon", SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }) { Before = TestAuth.Apply };
        var off = await GetAsync(Client(noEmail), "/support");
        Assert.NotNull(off.QuerySelector("#support-sign-in"));
        NoRawKeys(off, "support, email off");
    }

    [Fact]
    public async Task TicketPageShowsEveryKeyItUses()
    {
        await using var f = Factory();
        var store = f.Services.GetRequiredService<TicketStore>();
        var open = store.Create(_ => true, (id, n) => Ticket(id, n))!;
        var closed = store.Create(_ => true, (id, n) => Ticket(id, n, TicketState.Closed, DateTimeOffset.UtcNow - TimeSpan.FromDays(30)))!;
        var client = Client(f);

        // A link that has run out says so in the page's words.
        NoRawKeys(await GetAsync(client, "/support/ticket?id=" + Guid.NewGuid()), "ticket, a link that has run out");

        // The ticket cookie holds one ticket, so the closed ticket is opened from another browser.
        var other = Client(f);
        foreach (var (t, browser) in new[] { (open, client), (closed, other) })
        {
            var (withLink, link) = TicketLinks.Add(t, DateTimeOffset.UtcNow);
            store.Update(t.Id, _ => withLink);
            using (await browser.GetAsync($"/support/ticket?id={t.Id}&link={link}", Ct)) { }
        }

        var page = await GetAsync(client, "/support/ticket?id=" + open.Id);
        Assert.NotNull(page.QuerySelector("#reply"));
        Assert.Contains("Staff (Test)", page.QuerySelector("#messages")!.TextContent);
        Assert.Contains("Technical problem", page.QuerySelector("main")!.TextContent);
        NoRawKeys(page, "ticket, open");

        var sent = await GetAsync(client, $"/support/ticket?id={open.Id}&sent=1");
        Assert.Contains("Your reply is with the staff.", sent.QuerySelector(".ok-box")!.TextContent);
        NoRawKeys(sent, "ticket, after a reply");

        using (var empty = await PostAsync(client, "/support/ticket?id=" + open.Id, new() { ["Id"] = open.Id.ToString(), ["Reply"] = "short" }))
        {
            var error = await ParseAsync(empty);
            Assert.Contains("Write at least 10 characters.", error.QuerySelector(".error")!.TextContent);
            NoRawKeys(error, "ticket, a reply that is too short");
        }

        var ended = await GetAsync(other, "/support/ticket?id=" + closed.Id);
        Assert.NotNull(ended.QuerySelector("#closed-for-good"));
        NoRawKeys(ended, "ticket, closed for good");
    }

    [Fact]
    public async Task LegalPagesShowEveryKeyTheyUse()
    {
        await using var f = Factory();
        var client = Client(f);
        foreach (var id in new[] { "terms", "privacy", "cookies" })
        {
            var page = await GetAsync(client, "/" + id);
            Assert.Matches(@"^Version 4, \d+ October 2026$", page.QuerySelector("main h1 + p")!.TextContent.Trim());
            NoRawKeys(page, "legal " + id);
        }
        // The footer's navigation label.
        var terms = await GetAsync(client, "/terms");
        Assert.Equal("Legal pages", terms.QuerySelector("footer nav")!.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task WelcomeShowsEveryKeyItUses()
    {
        await using var f = Factory();
        f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Secret Person", Guid.NewGuid())], true);
        f.Regions.Result = new GridRegionList([new(Guid.NewGuid(), "A", 1000, 1000), new(Guid.NewGuid(), "B", 1001, 1000)], true);
        var client = Client(f);

        var page = await GetAsync(client, "/welcome");
        var text = page.Body!.TextContent;
        Assert.Contains("1 person online", text);
        Assert.Contains("2 regions", text);
        foreach (var link in new[] { "Create an account", "Forgot password", "Terms", "Privacy" })
            Assert.Contains(page.QuerySelectorAll(".splash-links a"), a => a.TextContent == link);
        NoRawKeys(page, "welcome", "body");

        f.OnlineUsers.Result = OnlineList.Unavailable;
        f.Regions.Result = GridRegionList.Unavailable;
        f.Services.GetRequiredService<DreamGrid.Launcher.Splash.SplashSnapshot>().Invalidate();
        var down = await GetAsync(client, "/welcome");
        Assert.Contains("Status unavailable", down.Body!.TextContent);
        NoRawKeys(down, "welcome, database down", "body");
    }

    [Fact]
    public async Task EveryKeyTheGroupUsesHasAnEnglishEntry()
    {
        await using var f = Factory();
        var t = f.Services.GetRequiredService<IStringLocalizer<ResidentText>>();
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DreamGrid.Launcher", "Pages", "Legal.cshtml")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        Assert.NotNull(root);
        var app = Path.Combine(root, "DreamGrid.Launcher");
        string[] files =
        [
            "Pages/Support/Confirm.cshtml", "Pages/Support/Confirm.cshtml.cs", "Pages/Support/Index.cshtml", "Pages/Support/Index.cshtml.cs",
            "Pages/Support/LostLink.cshtml", "Pages/Support/LostLink.cshtml.cs", "Pages/Support/Ticket.cshtml", "Pages/Support/Ticket.cshtml.cs",
            "Pages/Support/TicketView.cs", "Pages/Legal.cshtml", "Pages/Legal.cshtml.cs", "Pages/Shared/_LegalLinks.cshtml",
            "Pages/Welcome.cshtml", "Pages/Shared/_SplashBlock.cshtml", "Pages/Shared/_SplashBlocks.cshtml", "Pages/Shared/_SplashLayout.cshtml",
            "Guests/SupportTickets.cs",
        ];
        var keys = files.Select(p => Path.Combine(app, p)).SelectMany(p =>
                Regex.Matches(File.ReadAllText(p), @"(?:\b(?:[tT]|loc)\[|\bnew\(|\.With\()""([A-Z][A-Za-z]+\.[A-Za-z0-9]+)""(?!\s*\+)").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(keys.Count > 60, "found only " + keys.Count + " keys; the scan is not finding them");
        Assert.All(keys, k => Assert.False(t[k].ResourceNotFound, k + " has no entry in ResidentText.resx"));
    }
}
