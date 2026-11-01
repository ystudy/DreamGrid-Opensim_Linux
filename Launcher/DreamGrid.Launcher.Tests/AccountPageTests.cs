using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The account page and the confirm-new-email page (spec 2026-10-31-resident-account-design §3–§6). All names are made up.</summary>
public sealed class AccountPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid AnnId = Guid.Parse("a0000000-0000-0000-0000-0000000000a2");
    private const string Old = "old-password-1";
    private const string New = "tall-green-kettle";

    private static LauncherFactory Factory(bool email = true, string annEmail = "ann@example.com", LegalSettings? legal = null)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = legal ?? s.Legal,
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, Old, AnnId, email: annEmail, created: new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero));
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page, string? form = null)
    {
        var token = page.QuerySelector((form ?? "") + " input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string name = "Ann Smith", string password = Old)
    {
        var client = Client(f);
        var page = await PageAsync(client, "/guest/sign-in");
        using var r = await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name, ["Password"] = password, ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return client;
    }

    private static async Task<IHtmlDocument> PostFormAsync(HttpClient client, string formId, Dictionary<string, string> fields)
    {
        var page = await PageAsync(client, "/guest/account");
        var form = page.QuerySelector($"form#{formId}");
        Assert.NotNull(form);
        fields["__RequestVerificationToken"] = Token(page, $"form#{formId}");
        using var r = await client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(fields), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static Task<IHtmlDocument> ChangePasswordAsync(HttpClient client, string current = Old, string password = New) =>
        PostFormAsync(client, "change-password", new() { ["Current"] = current, ["Password"] = password, ["Again"] = password });

    private static string Text(IHtmlDocument page, string selector) => page.QuerySelector(selector)?.TextContent.Trim() ?? "";

    private static async Task<string> AskedTokenAsync(LauncherFactory f)
    {
        var changes = f.Services.GetRequiredService<AccountChanges>();
        Assert.Equal(AccountResult.Done, (await changes.AskEmailChangeAsync(AnnId, Old, null, "ann.new@example.com", IPAddress.Loopback, Ct)).Result);
        var tokens = f.Services.GetRequiredService<Core.Guests.EmailTokens>();
        // Replace the link just sent with one whose token the test knows, tied to the same address as the ask ties it.
        var token = tokens.Issue(AccountChanges.Purpose, AnnId, AccountChanges.LinkLifetime);
        f.Services.GetRequiredService<Core.Guests.PendingEmailChanges>().Set(AnnId, "ann.new@example.com", token);
        return token;
    }

    [Fact]
    public async Task Account_needs_sign_in()
    {
        await using var f = Factory();
        using var r = await Client(f).GetAsync("/guest/account", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/sign-in", new Uri(new Uri("http://localhost"), r.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task Shows_name_email_role_created()
    {
        await using var f = Factory();
        var page = await PageAsync(await SignedInAsync(f), "/guest/account");
        Assert.Equal("Ann Smith", Text(page, "#account-name"));
        Assert.Equal("ann@example.com", Text(page, "#account-email"));
        Assert.Equal("Resident", Text(page, "#account-role"));
        Assert.Equal("4 March 2026", Text(page, "#account-created"));
    }

    [Fact]
    public async Task Staff_shows_their_role()
    {
        await using var f = Factory();
        var page = await PageAsync(await SignedInAsync(f, "Test Admin", "pass-word1"), "/guest/account");
        Assert.Equal("Admin", Text(page, "#account-role"));
    }

    [Fact]
    public async Task No_email_shows_no_email_sentence()
    {
        await using var f = Factory(annEmail: "");
        var page = await PageAsync(await SignedInAsync(f), "/guest/account");
        Assert.Equal("No email address", Text(page, "#account-email"));
    }

    [Fact]
    public async Task Email_form_hidden_without_email()
    {
        await using var f = Factory(email: false);
        var page = await PageAsync(await SignedInAsync(f), "/guest/account");
        Assert.Null(page.QuerySelector("form#change-email"));
        Assert.Contains(AccountChanges.NoEmailText, page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("form#change-password"));
    }

    [Fact]
    public async Task Code_field_only_with_two_factor()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        var page = await PageAsync(client, "/guest/account");
        Assert.Null(page.QuerySelector("input[name=Code]"));

        var store = f.Services.GetRequiredService<ResidentTwoFactor>().Store;
        var (secret, _) = store.BeginEnrol(AnnId, "Ann Smith", "Avalon");
        Assert.NotNull(store.ConfirmEnrol(AnnId, Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())), secret));
        page = await PageAsync(client, "/guest/account");
        Assert.NotNull(page.QuerySelector("form#change-password input[name=Code]"));
        Assert.NotNull(page.QuerySelector("form#change-email input[name=Code]"));
    }

    [Fact]
    public async Task Password_change_keeps_this_session()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        var done = await ChangePasswordAsync(client);
        Assert.Contains(AccountChanges.PasswordDone, done.Body!.TextContent);
        await PageAsync(client, "/guest/account");
    }

    [Fact]
    public async Task Other_tab_signed_out_this_one_kept()
    {
        await using var f = Factory();
        var here = await SignedInAsync(f);
        var other = await SignedInAsync(f);
        await ChangePasswordAsync(here);

        using var r = await other.GetAsync("/guest/account", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/sign-in", new Uri(new Uri("http://localhost"), r.Headers.Location!).AbsolutePath);
        await PageAsync(here, "/guest/account");
    }

    [Fact]
    public async Task Wrong_password_message()
    {
        await using var f = Factory();
        var page = await ChangePasswordAsync(await SignedInAsync(f), current: "wrong-guess-1");
        Assert.Equal(AccountChanges.WrongCurrent, Text(page, "#change-password .error"));
    }

    [Fact]
    public async Task Email_ask_shows_reply()
    {
        await using var f = Factory();
        var page = await PostFormAsync(await SignedInAsync(f), "change-email", new() { ["Current"] = Old, ["NewEmail"] = "ann.new@example.com" });
        Assert.Equal(AccountChanges.AskReply, Text(page, "#change-email [role=status]"));
        Assert.Equal("ann@example.com", Text(page, "#account-email"));
    }

    [Fact]
    public async Task Confirm_link_shows_new_address()
    {
        await using var f = Factory();
        var token = await AskedTokenAsync(f);
        var page = await PageAsync(Client(f), $"/guest/confirm-email?token={token}");
        Assert.Contains("Your email address is now ann.new@example.com.", page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/guest/account']"));
    }

    [Fact]
    public async Task Confirm_link_anonymous_and_outside_terms_gate()
    {
        await using var f = Factory(legal: SignUpsTests.WrittenLegal());
        var client = await SignedInAsync(f);
        using (var account = await client.GetAsync("/guest/account", Ct))
            Assert.StartsWith("/guest/terms", account.Headers.Location?.OriginalString ?? "");

        var token = await AskedTokenAsync(f);
        var page = await PageAsync(client, $"/guest/confirm-email?token={token}");
        Assert.Contains("ann.new@example.com", page.Body!.TextContent);
    }

    [Fact]
    public async Task Bad_link_shows_expired()
    {
        await using var f = Factory();
        var page = await PageAsync(Client(f), "/guest/confirm-email?token=nope");
        Assert.Contains(AccountChanges.LinkExpired, page.Body!.TextContent);
    }

    [Fact]
    public async Task Front_page_links_account()
    {
        await using var f = Factory();
        var page = await PageAsync(await SignedInAsync(f), "/guest");
        Assert.NotNull(page.QuerySelector("a[href='/guest/account']"));
    }
}
