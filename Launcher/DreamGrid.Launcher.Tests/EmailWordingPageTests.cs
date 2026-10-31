using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's page for the wording of the grid's emails, at /security/email/wording.</summary>
public sealed class EmailWordingPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Page = "/security/email/wording";

    private static LauncherFactory Factory(EmailPageTests.FakeTransport? transport = null)
    {
        var t = transport ?? new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(t),
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> OwnerAsync(LauncherFactory f)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=owner", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, string id, Dictionary<string, string> fields)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(Page, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token, ["id"] = id };
        return await client.PostAsync($"{Page}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static string Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Shows_default_wording_for_each_template()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page, Ct));
        foreach (var id in EmailTemplates.All)
        {
            var section = page.QuerySelector($"#tpl-{id}");
            Assert.NotNull(section);
            Assert.Equal(EmailTemplates.Default(id).Subject, section.QuerySelector("input[name=Subject]")!.GetAttribute("value"));
            Assert.Equal(EmailTemplates.Default(id).Body, section.QuerySelector("textarea[name=Body]")!.TextContent.TrimStart('\n'));
            Assert.Contains("{GridName}", section.TextContent);
        }
        Assert.Contains("{Link} (needed)", page.QuerySelector("#tpl-reset-password")!.TextContent);
        var email = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/email", Ct));
        Assert.Equal(Page, email.QuerySelector("#wording-link")?.GetAttribute("href"));
    }

    [Fact]
    public async Task Save_refuses_missing_required_word()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Save", EmailTemplates.ResetPassword, new() { ["Subject"] = "Reset", ["Body"] = "No link here, valid for {ExpiresIn}." });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("This email needs {Link}.", page.QuerySelector("#tpl-reset-password .error")?.TextContent);
        Assert.Null(f.Store.LoadOrCreate().Email.Templates);
    }

    [Fact]
    public async Task Save_stores_under_default_language_and_audits()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Save", EmailTemplates.Test, new() { ["Subject"] = "Hello from {GridName}", ["Body"] = "A test.\r\nSecond line." });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var saved = f.Store.LoadOrCreate().Email.Templates!["en"][EmailTemplates.Test];
        Assert.Equal(("Hello from {GridName}", "A test.\nSecond line."), (saved.Subject, saved.Body));
        Assert.Contains("email-template-saved", Audit(f));
        // Saving the default wording again removes the stored copy.
        (await PostAsync(owner, "Save", EmailTemplates.Test, new() { ["Subject"] = EmailTemplates.Default("test").Subject, ["Body"] = EmailTemplates.Default("test").Body })).Dispose();
        Assert.False(f.Store.LoadOrCreate().Email.Templates?.GetValueOrDefault("en")?.ContainsKey(EmailTemplates.Test) ?? false);
    }

    [Fact]
    public async Task Reset_restores_default()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        (await PostAsync(owner, "Save", EmailTemplates.Test, new() { ["Subject"] = "Mine", ["Body"] = "Mine." })).Dispose();
        using var r = await PostAsync(owner, "Reset", EmailTemplates.Test, []);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(EmailTemplates.Default("test"), EmailTemplates.Current(f.Store.LoadOrCreate(), EmailTemplates.Test));
        Assert.Contains("email-template-reset", Audit(f));
    }

    [Fact]
    public async Task Preview_shows_sample_values_escaped()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Preview", EmailTemplates.ResetPassword,
            new() { ["Subject"] = "Reset for {AvatarName}", ["Body"] = "Go to {Link} within {ExpiresIn}. <script>alert(1)</script>" });
        var html = await r.Content.ReadAsStringAsync(Ct);
        var page = new HtmlParser().ParseDocument(html);
        var preview = page.QuerySelector("#tpl-reset-password .preview");
        Assert.NotNull(preview);
        Assert.Contains("Reset for Sample Avatar", preview.TextContent);
        Assert.Contains("https://example.com/sample-link", preview.TextContent);
        Assert.Empty(preview.QuerySelectorAll("script"));
        // What was typed stays in the form; nothing is saved.
        Assert.Equal("Reset for {AvatarName}", page.QuerySelector("#tpl-reset-password input[name=Subject]")!.GetAttribute("value"));
        Assert.Null(f.Store.LoadOrCreate().Email.Templates);
    }

    [Fact]
    public async Task Test_without_ready_account_says_set_up_first()
    {
        var transport = new EmailPageTests.FakeTransport();
        await using var f = Factory(transport);
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Test", EmailTemplates.Test, new() { ["Subject"] = "s", ["Body"] = "b", ["TestTo"] = "me@example.com" });
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Set up an email account first.", page.QuerySelector("#tpl-test .test-outcome")?.TextContent);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Test_sends_typed_wording_with_samples()
    {
        var transport = new EmailPageTests.FakeTransport();
        await using var f = Factory(transport);
        f.Services.GetRequiredService<EmailAccounts>().Save(new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls,
            "grid@gmail.com", null, "Avalon", "grid@gmail.com", null, 500, null, false, 0, null, null), "pw");
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Test", EmailTemplates.ConfirmEmail,
            new() { ["Subject"] = "Confirm {AvatarName}", ["Body"] = "{Link} for {ExpiresIn}", ["TestTo"] = "me@example.com" });
        var sent = Assert.Single(transport.Sent);
        Assert.Equal("Confirm Sample Avatar", sent.Email.Subject);
        Assert.Equal("me@example.com", sent.To);
        Assert.Contains("Signed in and sent", new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#tpl-confirm-email .test-outcome")?.TextContent);
    }
}
