using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Email page at /security/email: provider cards, the pop-up for each, test sends and the account list.</summary>
public sealed class EmailPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Secret = "S3cret-pw-zz";

    private static LauncherFactory Factory(OwnerPlan? plan = OwnerPlan.Local, FakeTransport? transport = null)
    {
        var t = transport ?? new FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(t),
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields, string tokenFrom = "/security/email")
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync("/security/email?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    private static Dictionary<string, string> Gmail(string password = Secret) => new()
    {
        ["Provider"] = "gmail",
        ["UserName"] = "grid@gmail.com",
        ["Password"] = password,
        ["FromName"] = "Avalon",
        ["FromAddress"] = "grid@gmail.com",
        ["ReplyTo"] = "",
        ["PerDay"] = "500",
        ["PerHour"] = "",
    };

    private static Dictionary<string, string> Custom(string host = "mail.example", string security = "StartTls") => new()
    {
        ["Provider"] = "custom",
        ["UserName"] = "me",
        ["Password"] = Secret,
        ["FromName"] = "Avalon",
        ["FromAddress"] = "me@mail.example",
        ["ReplyTo"] = "help@mail.example",
        ["PerDay"] = "",
        ["PerHour"] = "",
        ["Host"] = host,
        ["Port"] = "587",
        ["Security"] = security,
    };

    private static EmailAccounts Accounts(LauncherFactory f) => f.Services.GetRequiredService<EmailAccounts>();

    private static string Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Theory]
    [InlineData(OwnerPlan.Connected)]
    [InlineData(OwnerPlan.RegionServer)]
    public async Task Owner_only_and_404_without_guest_side(OwnerPlan plan)
    {
        await using (var f = Factory())
        {
            var admin = await SignedInAsync(f, "admin");
            using var r = await admin.GetAsync("/security/email", Ct);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        await using (var f = Factory(plan))
        {
            var owner = await SignedInAsync(f);
            using var r = await owner.GetAsync("/security/email", Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
    }

    [Fact]
    public async Task Shows_eight_cards_and_not_set_up_notice()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.Equal(EmailProviders.All.Select(p => p.Key), page.QuerySelectorAll(".email-card[data-dialog]").Select(c => c.GetAttribute("data-dialog")));
        Assert.Contains("Email isn't set up yet. Sign-up and password reset stay hidden until it is.", page.QuerySelector("#email-not-ready")?.TextContent);
        Assert.Contains("about 500 a day", page.QuerySelector(".email-card[data-dialog=gmail]")!.TextContent);
        var security = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security", Ct));
        Assert.Equal("/security/email", security.QuerySelector("#email-link")?.GetAttribute("href"));
    }

    [Fact]
    public async Task Save_adds_account_and_audits_without_password()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        using var r = await PostAsync(owner, "Save", Gmail());
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var a = Assert.Single(Accounts(f).All());
        Assert.Equal("smtp.gmail.com", a.Host);
        Assert.Equal(Secret, Accounts(f).PasswordOf(a));
        Assert.Contains("email-account-saved", Audit(f));
        Assert.DoesNotContain(Secret, Audit(f));
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.Null(page.QuerySelector("#email-not-ready"));
        var card = page.QuerySelector($".email-card[data-dialog='{a.Id:N}']");
        Assert.NotNull(card);
        Assert.Contains("1st", card.TextContent);
        Assert.Contains("0 of 500 today", card.TextContent);
        Assert.Contains("In use", card.TextContent);
    }

    [Fact]
    public async Task Password_never_rendered()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Gmail())).Dispose();
        var id = Accounts(f).All()[0].Id;
        var list = await owner.GetStringAsync("/security/email", Ct);
        var edit = await owner.GetStringAsync($"/security/email?edit={id:N}", Ct);
        Assert.DoesNotContain(Secret, list);
        Assert.DoesNotContain(Secret, edit);
        var input = new HtmlParser().ParseDocument(edit).QuerySelector($"#dlg-{id:N} input[name=Password]");
        Assert.NotNull(input);
        Assert.Equal("password", input.GetAttribute("type"));
        Assert.True(string.IsNullOrEmpty(input.GetAttribute("value")));
        Assert.Equal("Saved — type to replace", input.GetAttribute("placeholder"));
    }

    [Fact]
    public async Task Blank_password_keeps_stored_one()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Gmail())).Dispose();
        var id = Accounts(f).All()[0].Id;
        var again = Gmail(password: "");
        again["Id"] = id.ToString();
        again["FromName"] = "Avalon Grid";
        using var r = await PostAsync(owner, "Save", again);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var a = Assert.Single(Accounts(f).All());
        Assert.Equal("Avalon Grid", a.FromName);
        Assert.Equal(Secret, Accounts(f).PasswordOf(a));
    }

    [Fact]
    public async Task Saving_password_clears_needs_attention()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Gmail())).Dispose();
        var id = Accounts(f).All()[0].Id;
        Accounts(f).MarkProblem(id, "The server refused the password", DateTimeOffset.UtcNow);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.Contains("Needs attention: The server refused the password", page.QuerySelector($".email-card[data-dialog='{id:N}']")!.TextContent);
        var again = Gmail(password: "new-app-password");
        again["Id"] = id.ToString();
        (await PostAsync(owner, "Save", again)).Dispose();
        Assert.Null(Accounts(f).Find(id)!.Problem);
        Assert.Equal("new-app-password", Accounts(f).PasswordOf(Accounts(f).Find(id)!));
    }

    [Fact]
    public async Task Named_card_ignores_posted_host()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var form = Gmail();
        form["Host"] = "evil.example";
        form["Port"] = "25";
        form["Security"] = "None";
        (await PostAsync(owner, "Save", form)).Dispose();
        var a = Assert.Single(Accounts(f).All());
        Assert.Equal(("smtp.gmail.com", 587, MailSecurity.StartTls), (a.Host, a.Port, a.Security));
    }

    [Fact]
    public async Task Custom_none_security_shows_warning()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Custom(security: "None"))).Dispose();
        var a = Assert.Single(Accounts(f).All());
        Assert.Equal(MailSecurity.None, a.Security);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync($"/security/email?edit={a.Id:N}", Ct));
        var warning = page.QuerySelector($"#dlg-{a.Id:N} .none-warning");
        Assert.NotNull(warning);
        Assert.False(warning.HasAttribute("hidden"));
        Assert.Contains("Mail and password go over the network unencrypted. Use only for a mail server on your own network.", warning.TextContent);
        var fresh = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.True(fresh.QuerySelector("#dlg-custom .none-warning")!.HasAttribute("hidden"));
    }

    [Fact]
    public async Task Invalid_address_reopens_dialog_with_error()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var form = Gmail();
        form["FromAddress"] = "not an address";
        using var r = await PostAsync(owner, "Save", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.True(page.QuerySelector("#dlg-gmail")!.HasAttribute("open"));
        Assert.Contains("The \"From\" address isn't a valid email address.", page.QuerySelector("#dlg-gmail .error")?.TextContent);
        Assert.Empty(Accounts(f).All());
    }

    [Fact]
    public async Task Test_uses_unsaved_values_and_shows_outcome()
    {
        var transport = new FakeTransport();
        await using var f = Factory(transport: transport);
        var owner = await SignedInAsync(f);
        var form = Custom(host: "unsaved.example");
        form["TestTo"] = "me@example.com";
        using var r = await PostAsync(owner, "Test", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var (account, password, to, email) = Assert.Single(transport.Sent);
        Assert.Equal("unsaved.example", account.Host);
        Assert.Equal(Secret, password);
        Assert.Equal("me@example.com", to);
        Assert.Equal("Test email from Avalon", email.Subject);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Signed in and sent", page.QuerySelector("#dlg-custom .test-outcome")?.TextContent);
        Assert.DoesNotContain(Secret, await r.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Accounts(f).All());
        Assert.Contains("email-test-sent", Audit(f));
    }

    [Fact]
    public async Task Test_of_saved_account_with_blank_password_uses_stored_one()
    {
        var transport = new FakeTransport();
        await using var f = Factory(transport: transport);
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Gmail())).Dispose();
        var form = Gmail(password: "");
        form["Id"] = Accounts(f).All()[0].Id.ToString();
        form["TestTo"] = "me@example.com";
        (await PostAsync(owner, "Test", form)).Dispose();
        Assert.Equal(Secret, Assert.Single(transport.Sent).Password);
    }

    [Fact]
    public async Task Test_is_limited_to_five_a_minute()
    {
        var transport = new FakeTransport();
        await using var f = Factory(transport: transport);
        var owner = await SignedInAsync(f);
        var form = Custom();
        form["TestTo"] = "me@example.com";
        string last = "";
        for (var i = 0; i < 6; i++)
        {
            using var r = await PostAsync(owner, "Test", form);
            last = await r.Content.ReadAsStringAsync(Ct);
        }
        Assert.Equal(5, transport.Sent.Count);
        Assert.Contains("You can send 5 test emails a minute. Wait a moment and try again.", last);
    }

    [Fact]
    public async Task Move_and_pause_change_card_order_and_state()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Save", Custom(host: "one.example"))).Dispose();
        (await PostAsync(owner, "Save", Custom(host: "two.example"))).Dispose();
        var two = Accounts(f).All()[1];
        (await PostAsync(owner, "Move", new() { ["id"] = two.Id.ToString(), ["delta"] = "-1" })).Dispose();
        Assert.Equal("two.example", Accounts(f).All()[0].Host);
        (await PostAsync(owner, "Pause", new() { ["id"] = two.Id.ToString(), ["paused"] = "true" })).Dispose();
        Assert.True(Accounts(f).Find(two.Id)!.Paused);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.Contains("Paused", page.QuerySelector($".email-card[data-dialog='{two.Id:N}']")!.TextContent);
        Assert.Contains("email-account-moved", Audit(f));
        Assert.Contains("email-account-paused", Audit(f));
        (await PostAsync(owner, "Remove", new() { ["id"] = two.Id.ToString() })).Dispose();
        Assert.Equal("one.example", Assert.Single(Accounts(f).All()).Host);
        Assert.Contains("email-account-removed", Audit(f));
    }

    [Fact]
    public async Task Edit_query_renders_dialog_open_without_script()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email?edit=gmail", Ct));
        var dialog = page.QuerySelector("#dlg-gmail");
        Assert.NotNull(dialog);
        Assert.True(dialog.HasAttribute("open"));
        Assert.False(page.QuerySelector("#dlg-yahoo")!.HasAttribute("open"));
        Assert.Equal("?edit=gmail", page.QuerySelector(".email-card[data-dialog=gmail]")!.GetAttribute("href"));
        Assert.Equal("smtp.gmail.com", dialog.QuerySelector("input[name=Host]")!.GetAttribute("value"));
        Assert.True(dialog.QuerySelector("input[name=Host]")!.HasAttribute("readonly"));
        Assert.Contains("Turn on 2-Step Verification", dialog.TextContent);
    }

    internal sealed class FakeTransport : IMailTransport
    {
        public List<(EmailAccount Account, string Password, string To, RenderedEmail Email)> Sent { get; } = [];

        public Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct)
        {
            lock (Sent) Sent.Add((account, password, to, email));
            return Task.FromResult(new SendOutcome(SendResult.Sent, "Signed in and sent"));
        }
    }
}
