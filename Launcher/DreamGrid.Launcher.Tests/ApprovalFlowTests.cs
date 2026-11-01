using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sign-up approval end to end through the website, with email captured (spec 2026-11-01-sign-up-approval-design §12). All names are made up.</summary>
public sealed class ApprovalFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Password = "tall-green-kettle";

    private static HttpClient Browser(SignUpsTests.Rig rig) =>
        rig.F.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });

    private static async Task<HttpClient> StaffAsync(SignUpsTests.Rig rig, string role)
    {
        var c = Browser(rig);
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static void Settings(SignUpsTests.Rig rig, Func<BotSettings, BotSettings> bots) =>
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = bots(s.Bots) } : null!);

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page) => page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    /// <summary>Clicks the row's Approve button on <paramref name="from"/> (the dashboard or the list), for the row named <paramref name="name"/>.</summary>
    private static async Task<HttpResponseMessage> ApproveAsync(HttpClient staff, string from, string name)
    {
        var page = await PageAsync(staff, from);
        var row = page.QuerySelectorAll(".signup-row").Single(r => r.QuerySelector(".signup-name")!.TextContent.Trim() == name);
        var form = row.QuerySelector("form")!;
        var fields = form.QuerySelectorAll("input").ToDictionary(i => i.GetAttribute("name")!, i => i.GetAttribute("value") ?? "");
        return await staff.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(fields), Ct);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string name)
    {
        var page = await PageAsync(client, "/guest/sign-in");
        return await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name, ["Password"] = Password, ["__RequestVerificationToken"] = Token(page),
        }), Ct);
    }

    [Fact]
    public async Task Staff_approve_before_link()
    {
        await using var rig = SignUpsTests.Factory();
        var guest = Browser(rig);
        (await SignUpPageTests.PostSignUpAsync(guest)).Dispose();
        var link = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);

        using var approve = await ApproveAsync(await StaffAsync(rig, "admin"), "/", "Bea Example");
        Assert.Equal("/", approve.Headers.Location?.OriginalString);

        var sent = await SignUpsTests.SentAsync(rig, 2);
        Assert.Equal("Welcome to Avalon", sent[1].Email.Subject);
        Assert.Contains("You're already approved. Sign in.", (await PageAsync(guest, "/guest/confirm?token=" + link)).QuerySelector("main")!.TextContent);
        using var signIn = await SignInAsync(guest, "Bea Example");
        Assert.Equal("/guest/connect?approved=1", signIn.Headers.Location?.OriginalString);
        Assert.Contains("You're approved. Log in as Bea Example.", (await PageAsync(guest, "/guest/connect?approved=1")).QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Link_then_board_shows_self_approved()
    {
        await using var rig = SignUpsTests.Factory();
        var guest = Browser(rig);
        (await SignUpPageTests.PostSignUpAsync(guest)).Dispose();
        var link = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);

        using var confirm = await guest.GetAsync("/guest/confirm?token=" + link, Ct);
        Assert.Equal("/guest/connect", confirm.Headers.Location?.OriginalString);

        var card = (await PageAsync(await StaffAsync(rig, "owner"), "/")).QuerySelector("#signups-card")!;
        Assert.Contains("Self-approved (email link)", card.TextContent);
        Assert.Null(card.QuerySelector("button.approve"));
    }

    [Fact]
    public async Task No_email_waits_then_approved()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        Settings(rig, b => b with { AdminApproval = true });
        var guest = Browser(rig);
        using var signUp = await SignUpPageTests.PostSignUpAsync(guest);
        Assert.Contains(SignUpPageTests.WaitingForStaff, await signUp.Content.ReadAsStringAsync(Ct));

        using (var waiting = await SignInAsync(guest, "Bea Example"))
            Assert.Contains("Your account is waiting for approval by the grid&#x27;s staff.", await waiting.Content.ReadAsStringAsync(Ct));

        (await ApproveAsync(await StaffAsync(rig, "owner"), "/people/sign-ups", "Bea Example")).Dispose();

        using var signIn = await SignInAsync(guest, "Bea Example");
        Assert.Equal("/guest/connect?approved=1", signIn.Headers.Location?.OriginalString);
        lock (rig.Transport.Sent) Assert.Empty(rig.Transport.Sent);
    }

    [Fact]
    public async Task Deny_frees_name()
    {
        await using var rig = SignUpsTests.Factory();
        var guest = Browser(rig);
        (await SignUpPageTests.PostSignUpAsync(guest)).Dispose();
        await SignUpsTests.SentAsync(rig, 1);
        var owner = await StaffAsync(rig, "owner");
        var card = await PageAsync(owner, "/");
        var deny = card.QuerySelector("#signups-card a.deny")!.GetAttribute("href")!;

        var ask = await PageAsync(owner, deny);
        using var r = await owner.PostAsync("/people/sign-ups/deny", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = ask.QuerySelector("input[name=id]")!.GetAttribute("value")!,
            ["returnUrl"] = "/",
            ["note"] = "Club members only.",
            ["__RequestVerificationToken"] = Token(ask),
        }), Ct);

        var sent = await SignUpsTests.SentAsync(rig, 2);
        Assert.Equal("Your sign-up on Avalon", sent[1].Email.Subject);
        Assert.Contains("Club members only.", sent[1].Email.Text);
        Assert.Contains("Denied by Test Owner", (await PageAsync(owner, "/")).QuerySelector("#signups-card")!.TextContent);
        using var again = await SignUpPageTests.PostSignUpAsync(Browser(rig));
        Assert.Contains(SignUpPageTests.CheckEmail, await again.Content.ReadAsStringAsync(Ct));
        Assert.Single(rig.F.Robust.All, a => a.FirstName == "Bea");
    }

    [Fact]
    public async Task Supervisor_flow()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        Settings(rig, b => b with { AdminApproval = true, Approvers = SignUpApprovers.OwnerAdminsAndSupervisors });
        (await SignUpPageTests.PostSignUpAsync(Browser(rig))).Dispose();
        var supervisor = await StaffAsync(rig, "supervisor");

        using var dashboard = await supervisor.GetAsync("/", Ct);
        Assert.NotEqual(HttpStatusCode.OK, dashboard.StatusCode);
        using var r = await ApproveAsync(supervisor, "/people/sign-ups", "Bea Example");

        Assert.Equal("/people/sign-ups", r.Headers.Location?.OriginalString);
        Assert.Equal(0, rig.F.Robust.All.Single(a => a.FirstName == "Bea").UserLevel);
        Assert.Contains("Approved by Test Supervisor", (await PageAsync(supervisor, "/people/sign-ups")).QuerySelector("#signups-list")!.TextContent);
    }
}
