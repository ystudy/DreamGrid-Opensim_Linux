using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The account page end to end, through the real website with the email captured (spec 2026-10-31-resident-account-design §8). All names are made up.</summary>
public sealed class AccountFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Old = "old-password-1";
    private const string New = "tall-green-kettle";

    private sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Factory(bool email = true)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Recovery = new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLink, RecoveryRule.EmailLink),
            Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, Old, email: "ann@example.com");
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        return new Rig(f, transport);
    }

    private static HttpClient Client(Rig rig) => rig.F.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page, string form = "")
    {
        var token = page.QuerySelector(form + " input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string password)
    {
        var page = await PageAsync(client, "/guest/sign-in");
        return await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Ann Smith", ["Password"] = password, ["__RequestVerificationToken"] = Token(page),
        }), Ct);
    }

    private static async Task<IHtmlDocument> PostAccountAsync(HttpClient client, string formId, Dictionary<string, string> fields)
    {
        var page = await PageAsync(client, "/guest/account");
        var form = page.QuerySelector($"form#{formId}");
        Assert.NotNull(form);
        fields["__RequestVerificationToken"] = Token(page, $"form#{formId}");
        using var r = await client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(fields), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<List<(string To, RenderedEmail Email)>> SentAsync(Rig rig, Func<(string To, RenderedEmail Email), bool> until)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (rig.Transport.Sent)
            {
                var all = rig.Transport.Sent.Select(m => (m.To, m.Email)).ToList();
                if (all.Any(until)) return all;
            }
            await Task.Delay(25, Ct);
        }
        lock (rig.Transport.Sent) return [.. rig.Transport.Sent.Select(m => (m.To, m.Email))];
    }

    private static int Audited(Rig rig, string action)
    {
        var path = Path.Combine(rig.F.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return 0;
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString() == action);
    }

    private static async Task AskResetAsync(HttpClient client, string email)
    {
        var page = await PageAsync(client, "/guest/forgot-password");
        using var r = await client.PostAsync("/guest/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Ann Smith", ["Email"] = email, ["website"] = "", ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Full_password_change()
    {
        await using var rig = Factory();
        var client = Client(rig);
        using (var r = await SignInAsync(client, Old)) Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var done = await PostAccountAsync(client, "change-password", new() { ["Current"] = Old, ["Password"] = New, ["Again"] = New });
        Assert.Contains("Your password is changed.", done.Body!.TextContent);

        var fresh = Client(rig);
        using (var r = await SignInAsync(fresh, Old)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await SignInAsync(fresh, New)) Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await PageAsync(fresh, "/guest/account");
    }

    [Fact]
    public async Task Full_email_change()
    {
        await using var rig = Factory();
        var client = Client(rig);
        using (var r = await SignInAsync(client, Old)) Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await PostAccountAsync(client, "change-email", new() { ["Current"] = Old, ["NewEmail"] = "ann.new@example.com" });

        var sent = await SentAsync(rig, m => m.To == "ann@example.com");
        var link = sent.Single(m => m.To == "ann.new@example.com");
        var notice = sent.Single(m => m.To == "ann@example.com");
        Assert.DoesNotContain("ann.new", notice.Email.Text);

        var token = SignUpsTests.LinkToken(link.Email);
        var confirmed = await PageAsync(Client(rig), $"/guest/confirm-email?token={token}");
        Assert.Contains("Your email address is now ann.new@example.com.", confirmed.Body!.TextContent);

        // Recovery now finds the account by the new address, and not by the old one.
        var anon = Client(rig);
        await AskResetAsync(anon, "ann@example.com");
        await AskResetAsync(anon, "ann.new@example.com");
        var after = await SentAsync(rig, m => m.To == "ann.new@example.com" && m.Email.Text.Contains("/guest/reset-password"));
        Assert.Contains(after, m => m.To == "ann.new@example.com" && m.Email.Text.Contains("/guest/reset-password"));
        for (var i = 0; i < 200 && Audited(rig, "reset-requested") + Audited(rig, "reset-refused") < 2; i++) await Task.Delay(25, Ct);
        lock (rig.Transport.Sent)
            Assert.DoesNotContain(rig.Transport.Sent, m => m.To == "ann@example.com" && m.Email.Text.Contains("/guest/reset-password"));
    }

    [Fact]
    public async Task Email_form_absent_without_email_setup()
    {
        await using var rig = Factory(email: false);
        var client = Client(rig);
        using (var r = await SignInAsync(client, Old)) Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var page = await PageAsync(client, "/guest/account");
        Assert.Null(page.QuerySelector("form#change-email"));
        Assert.NotNull(page.QuerySelector("form#change-password"));
    }
}
