using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The sign-up, confirm and how-to-connect pages, and sign-in for a sign-up still waiting for its email (spec 2026-10-31-sign-up-design §3–§6).</summary>
public sealed class SignUpPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Password = "tall-green-kettle";
    public const string CheckEmail = "Check your email. We sent a link to confirm your account. It works once, for 24 hours. Check your spam folder.";
    public const string Closed = "This grid isn't open for sign-ups yet.";
    public const string Expired = "This link has expired or was already used.";

    private static HttpClient Client(SignUpsTests.Rig r) => r.F.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Antiforgery(IHtmlDocument page) =>
        page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value") ?? throw new Xunit.Sdk.XunitException("no antiforgery token");

    internal static async Task<HttpResponseMessage> PostSignUpAsync(HttpClient client, string first = "Bea", string last = "Example",
        string email = "bea@example.com", string honeypot = "", bool terms = true)
    {
        var page = await PageAsync(client, "/signup");
        var fields = new Dictionary<string, string>
        {
            ["First"] = first, ["Last"] = last, ["Email"] = email, ["Password"] = Password, ["Again"] = Password,
            ["website"] = honeypot, ["__RequestVerificationToken"] = Antiforgery(page),
        };
        if (terms) fields["AcceptTerms"] = "true";
        return await client.PostAsync("/signup", new FormUrlEncodedContent(fields), Ct);
    }

    private static async Task<IHtmlDocument> SignInAsync(HttpClient client, string name, string password)
    {
        var page = await PageAsync(client, "/guest/sign-in");
        using var r = await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name, ["Password"] = password, ["__RequestVerificationToken"] = Antiforgery(page),
        }), Ct);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Sign_up_closed_shows_message()
    {
        await using var rig = SignUpsTests.Factory(open: false);

        var page = await PageAsync(Client(rig), "/signup");

        Assert.Contains(Closed, page.QuerySelector("main")!.TextContent);
        Assert.Empty(page.QuerySelectorAll("form#sign-up"));
    }

    [Fact]
    public async Task Sign_up_form_has_bot_fields()
    {
        await using var rig = SignUpsTests.Factory();

        var page = await PageAsync(Client(rig), "/signup");

        var form = page.QuerySelector("form#sign-up")!;
        Assert.NotNull(form.QuerySelector("input[name=website]"));
        Assert.NotNull(form.QuerySelector("input[name=AcceptTerms]"));
        Assert.NotNull(form.QuerySelector("a[href='/terms']"));
        Assert.NotNull(form.QuerySelector("a[href='/privacy']"));
    }

    [Fact]
    public async Task Without_email_lands_on_connect_signed_in()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        var client = Client(rig);

        using var r = await PostSignUpAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/connect", r.Headers.Location!.OriginalString);
        var connect = await PageAsync(client, "/guest/connect");
        Assert.Contains("Log in as Bea Example", connect.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task With_email_shows_check_email()
    {
        await using var rig = SignUpsTests.Factory();

        using var r = await PostSignUpAsync(Client(rig));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(CheckEmail, page.QuerySelector("#sign-up-reply")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("form#resend"));
    }

    [Fact]
    public async Task Failed_bot_check_makes_nothing()
    {
        await using var rig = SignUpsTests.Factory();

        using var r = await PostSignUpAsync(Client(rig), honeypot: "http://spam.example");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.FirstName == "Bea");
    }

    [Fact]
    public async Task Problem_is_shown_and_typed_values_kept()
    {
        await using var rig = SignUpsTests.Factory();

        using var r = await PostSignUpAsync(Client(rig), terms: false);

        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Tick the box", page.QuerySelector("[role=alert]")!.TextContent);
        Assert.Equal("Bea", page.QuerySelector("input[name=First]")!.GetAttribute("value"));
        Assert.Equal("", page.QuerySelector("input[name=Password]")!.GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task Confirm_link_lands_on_connect()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);

        using var r = await client.GetAsync($"/guest/confirm?token={token}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/connect", r.Headers.Location!.OriginalString);
        Assert.Contains("Log in as Bea Example", (await PageAsync(client, "/guest/connect")).QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Bad_link_shows_expired()
    {
        await using var rig = SignUpsTests.Factory();

        var page = await PageAsync(Client(rig), "/guest/confirm?token=nope");

        Assert.Contains(Expired, page.QuerySelector("main")!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/signup']"));
    }

    [Fact]
    public async Task Pending_sign_in_says_confirm_first()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();

        var page = await SignInAsync(client, "Bea Example", Password);

        Assert.Contains("Confirm your email first.", page.QuerySelector("[role=alert]")!.TextContent);
        Assert.NotNull(page.QuerySelector("form#resend"));
    }

    public const string WaitingForStaff = "Thanks. The grid's staff will look at your sign-up. Once you're approved, you can sign in here with the name and password you just chose.";

    private static SignUpsTests.Rig WithApproval(bool email)
    {
        var rig = SignUpsTests.Factory(email: email);
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = s.Bots with { AdminApproval = true } } : null!);
        return rig;
    }

    [Fact]
    public async Task Waiting_for_staff_copy()
    {
        await using var rig = WithApproval(email: false);
        var client = Client(rig);

        using var r = await PostSignUpAsync(client);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains(WaitingForStaff, page.QuerySelector("main")!.TextContent);
        Assert.Empty(page.QuerySelectorAll("form#resend"));
    }

    [Fact]
    public async Task Check_email_has_staff_line()
    {
        await using var rig = SignUpsTests.Factory();

        using var r = await PostSignUpAsync(Client(rig));

        var text = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("main")!.TextContent;
        Assert.Contains(CheckEmail, text);
        Assert.Contains("Can't find the email? Staff can approve you too.", text);
    }

    [Fact]
    public async Task Decoy_reply_has_staff_line_too()
    {
        await using var rig = SignUpsTests.Factory();

        using var r = await PostSignUpAsync(Client(rig), email: "ann@example.com");

        Assert.Contains("Can't find the email? Staff can approve you too.",
            new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Sign_in_waiting_for_staff_copy()
    {
        await using var rig = WithApproval(email: false);
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();

        var page = await SignInAsync(client, "Bea Example", Password);

        Assert.Equal("Your account is waiting for approval by the grid's staff.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("form#resend"));
    }

    [Fact]
    public async Task Sign_in_waiting_for_email_copy()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();

        var page = await SignInAsync(client, "Bea Example", Password);

        Assert.Equal("Confirm your email first. Or wait for staff to approve you.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Confirm_link_after_staff_approval_says_already_approved()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;
        await rig.F.Services.GetRequiredService<DreamGrid.Launcher.Guests.SignUpApprovals>()
            .ApproveAsync(bea, "Test Owner", DreamGrid.Launcher.Core.Security.StaffRole.Owner, null, Ct);

        var page = await PageAsync(client, "/guest/confirm?token=" + token);

        Assert.Contains("You're already approved. Sign in.", page.QuerySelector("main")!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/guest/sign-in']"));
    }

    [Fact]
    public async Task First_sign_in_after_approval_greets_once()
    {
        await using var rig = WithApproval(email: false);
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;
        await rig.F.Services.GetRequiredService<DreamGrid.Launcher.Guests.SignUpApprovals>()
            .ApproveAsync(bea, "Test Owner", DreamGrid.Launcher.Core.Security.StaffRole.Owner, null, Ct);

        var signIn = await PageAsync(client, "/guest/sign-in");
        using var first = await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Bea Example", ["Password"] = Password, ["__RequestVerificationToken"] = Antiforgery(signIn),
        }), Ct);
        Assert.Equal("/guest/connect?approved=1", first.Headers.Location?.OriginalString);
        var connect = await PageAsync(client, "/guest/connect?approved=1");
        Assert.Contains("You're approved. Log in as Bea Example.", connect.QuerySelector("main")!.TextContent);

        var again = Client(rig);
        signIn = await PageAsync(again, "/guest/sign-in");
        using var second = await again.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Bea Example", ["Password"] = Password, ["__RequestVerificationToken"] = Antiforgery(signIn),
        }), Ct);
        Assert.Equal("/guest", second.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Pending_with_wrong_password_gets_the_usual_message()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await PostSignUpAsync(client)).Dispose();

        var page = await SignInAsync(client, "Bea Example", "wrong-password-1");

        Assert.Equal("Name or password is wrong.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Suspended_not_pending_keeps_old_message()
    {
        await using var rig = SignUpsTests.Factory();
        rig.F.Robust.Add("Sue", "Spended", -1, "old-password-1");

        var page = await SignInAsync(Client(rig), "Sue Spended", "old-password-1");

        Assert.Equal("Name or password is wrong.", page.QuerySelector("[role=alert]")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("form#resend"));
    }

    [Fact]
    public async Task Connect_shows_login_uri_and_steps()
    {
        await using var rig = SignUpsTests.Factory();

        var page = await PageAsync(Client(rig), "/guest/connect");

        var s = rig.F.Store.LoadOrCreate();
        var uri = GridEndpoints.LoginUri(s, Core.Config.HostAddress.Effective(s.Hostname));
        Assert.Equal(uri, page.QuerySelector("#login-uri")!.TextContent.Trim());
        Assert.Contains("Grid Manager", page.QuerySelector("main")!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='https://www.firestormviewer.org/downloads/']"));
        Assert.Contains("Avalon", page.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Connect_greets_signed_in_resident()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        var page = await PageAsync(client, "/guest/connect");

        Assert.Contains("Log in as Test Guest", page.QuerySelector("main")!.TextContent);
    }

    [Theory]
    [InlineData("/guest/confirm?token=x")]
    [InlineData("/guest/connect")]
    [InlineData("/signup")]
    public async Task Pages_absent_on_region_server(string url)
    {
        await using var rig = SignUpsTests.Factory(before: f => f.Store.Save(f.Store.LoadOrCreate() with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", Core.LauncherVersion.Current, false, 8004, 8006),
        }));

        using var r = await Client(rig).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Sign_in_links_sign_up_only_when_open()
    {
        await using (var open = SignUpsTests.Factory())
            Assert.NotNull((await PageAsync(Client(open), "/guest/sign-in")).QuerySelector("a[href='/signup']"));
        await using (var closed = SignUpsTests.Factory(open: false))
            Assert.Null((await PageAsync(Client(closed), "/guest/sign-in")).QuerySelector("a[href='/signup']"));
    }

    [Fact]
    public async Task Guest_front_page_links_how_to_connect()
    {
        await using var rig = SignUpsTests.Factory();
        var client = Client(rig);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();
        rig.F.Services.GetRequiredService<Guests.TermsAcceptances>().Accept(TestAuth.GuestId, 4, null);

        Assert.NotNull((await PageAsync(client, "/guest")).QuerySelector("a[href='/guest/connect']"));
    }
}
