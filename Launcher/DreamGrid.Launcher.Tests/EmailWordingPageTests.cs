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
    public async Task A_post_for_a_language_that_is_gone_saves_nothing()
    {
        // The owner had ?lang=it open and deleted Italian in another tab: the save must not land on the default's wording.
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        using var r = await PostAsync(owner, "Save&lang=it", EmailTemplates.ConfirmEmail,
            new() { ["Subject"] = "Conferma su {GridName}", ["Body"] = "Ciao {AvatarName}: {Link} ({ExpiresIn})" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.True(f.Store.LoadOrCreate().Email.Templates is null or { Count: 0 });
        Assert.DoesNotContain("email-template-saved", Audit(f));
    }

    [Fact]
    public async Task Page_says_which_language_the_wording_is_for()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page, Ct));
        Assert.Equal("This wording is used for emails in English.", page.QuerySelector("#wording-language-note")!.TextContent.Trim());

        using (var r = await PostAsync(owner, "Save&lang=de", EmailTemplates.ResetPassword,
            new() { ["Subject"] = "Neues Passwort", ["Body"] = "{Link} {ExpiresIn}" }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var german = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page + "?lang=de", Ct));
        Assert.Equal("This wording is used for emails in Deutsch.", german.QuerySelector("#wording-language-note")!.TextContent.Trim());
        // The 16a warning is gone: each language has its own wording now.
        Assert.Empty(german.QuerySelectorAll(".other-languages-warning"));
        Assert.Contains("(changed)", german.QuerySelector("#tpl-reset-password h2")!.TextContent);
    }

    [Fact]
    public async Task Email_wording_per_language_saved_and_used()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);

        using var r = await PostAsync(owner, "Save&lang=de", EmailTemplates.ConfirmEmail,
            new() { ["Subject"] = "Bitte bestaetigen auf {GridName}", ["Body"] = "Hallo {AvatarName}: {Link} ({ExpiresIn})" });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(Page + "?lang=de", r.Headers.Location!.OriginalString);
        var s = f.Store.LoadOrCreate();
        Assert.Equal("Bitte bestaetigen auf {GridName}", s.Email.Templates!["de"][EmailTemplates.ConfirmEmail].Subject);
        Assert.False(s.Email.Templates.ContainsKey("en"));
        Assert.Contains("email-template-saved", Audit(f));
        Assert.Contains("de confirm-email", Audit(f));
        // A German resident gets it; a French one gets the built-in French; English is untouched.
        Assert.Equal("Bitte bestaetigen auf {GridName}", EmailTemplates.Current(s, EmailTemplates.ConfirmEmail, "de").Subject);
        Assert.Equal(EmailTemplates.Default(EmailTemplates.ConfirmEmail, "fr"), EmailTemplates.Current(s, EmailTemplates.ConfirmEmail, "fr"));
        Assert.Equal(EmailTemplates.Default(EmailTemplates.ConfirmEmail, "en"), EmailTemplates.Current(s, EmailTemplates.ConfirmEmail, "en"));
        // The German page shows it; reset there brings back the built-in German.
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page + "?lang=de", Ct));
        Assert.Equal("Bitte bestaetigen auf {GridName}", page.QuerySelector("#tpl-confirm-email input[name=Subject]")!.GetAttribute("value"));
        Assert.Contains("lang=de", page.QuerySelector("#tpl-confirm-email form")!.GetAttribute("action"));
        using var reset = await PostAsync(owner, "Reset&lang=de", EmailTemplates.ConfirmEmail, []);
        Assert.Equal(Page + "?lang=de", reset.Headers.Location!.OriginalString);
        Assert.Equal(EmailTemplates.Default(EmailTemplates.ConfirmEmail, "de"), EmailTemplates.Current(f.Store.LoadOrCreate(), EmailTemplates.ConfirmEmail, "de"));
        Assert.Contains("de confirm-email", Audit(f).Split('\n').Last(l => l.Contains("email-template-reset")));
    }

    [Fact]
    public async Task Saving_the_built_in_wording_of_the_language_drops_the_copy()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var de = EmailTemplates.Default(EmailTemplates.ConfirmEmail, "de");

        using var r = await PostAsync(owner, "Save&lang=de", EmailTemplates.ConfirmEmail, new() { ["Subject"] = de.Subject, ["Body"] = de.Body });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Null(f.Store.LoadOrCreate().Email.Templates);
    }

    [Fact]
    public async Task Staff_emails_are_edited_in_English_only()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page + "?lang=de", Ct));

        Assert.Null(page.QuerySelector("#tpl-ticket-new"));
        Assert.Null(page.QuerySelector("#tpl-test"));
        Assert.NotNull(page.QuerySelector("#tpl-confirm-email"));
        Assert.Contains("Emails to staff are always in English", page.QuerySelector("#staff-emails-note")!.TextContent);
        using var r = await PostAsync(owner, "Save&lang=de", EmailTemplates.TicketNew, new() { ["Subject"] = "x", ["Body"] = "{Link}" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(f.Store.LoadOrCreate().Email.Templates);
    }

    [Fact]
    public async Task Email_wording_page_lists_added_languages()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<DreamGrid.Launcher.Core.OwnerLanguages>().Save(new DreamGrid.Launcher.Core.OwnerLanguage("it",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["confirm-email.Subject"] = "Conferma su {GridName}" }, DateTimeOffset.UtcNow));
        var owner = await OwnerAsync(f);

        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page, Ct));
        var links = page.QuerySelectorAll("#wording-languages a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal([.. DreamGrid.Launcher.Core.Languages.BuiltIn.Select(c => Page + "?lang=" + c), Page + "?lang=it"], links);
        Assert.Equal("page", page.QuerySelector($"#wording-languages a[href='{Page}?lang=en']")!.GetAttribute("aria-current"));

        // Italian starts from its file's wording, and from English where the file has none.
        var it = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page + "?lang=it", Ct));
        Assert.Equal("This wording is used for emails in Italiano.", it.QuerySelector("#wording-language-note")!.TextContent.Trim());
        Assert.Equal("Conferma su {GridName}", it.QuerySelector("#tpl-confirm-email input[name=Subject]")!.GetAttribute("value"));
        Assert.Equal(EmailTemplates.Default(EmailTemplates.ConfirmEmail).Body,
            it.QuerySelector("#tpl-confirm-email textarea[name=Body]")!.TextContent.TrimStart('\n'));
        Assert.DoesNotContain("(changed)", it.QuerySelector("#tpl-confirm-email h2")!.TextContent);
        // A language GridGate doesn't have shows the grid's default.
        var odd = new HtmlParser().ParseDocument(await owner.GetStringAsync(Page + "?lang=../xx", Ct));
        Assert.Equal("This wording is used for emails in English.", odd.QuerySelector("#wording-language-note")!.TextContent.Trim());
    }

    [Fact]
    public async Task Preview_in_a_language_uses_its_sample_words()
    {
        await using var f = Factory();
        var owner = await OwnerAsync(f);

        using var r = await PostAsync(owner, "Preview&lang=de", EmailTemplates.ResetPassword, new() { ["Subject"] = "Neu", ["Body"] = "{Link} {ExpiresIn}" });

        var preview = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#tpl-reset-password .preview")!;
        Assert.Contains(EmailTemplates.Words("ExpiresIn.30m", "de"), preview.TextContent);
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
        Assert.Equal(EmailTemplates.Default("test"), EmailTemplates.Current(f.Store.LoadOrCreate(), EmailTemplates.Test, "en"));
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
