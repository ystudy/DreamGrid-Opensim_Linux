using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The grid's email wording: working defaults, required fill-in words, and an HTML version that is safe.</summary>
public sealed class EmailTemplatesTests
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_contain_their_required_words()
    {
        Assert.Equal(["test", "confirm-email", "reset-password", "account-approved", "password-changed", "already-have-account",
            "confirm-new-email", "email-change-notice", "signup-denied", "signups-waiting", "name-changed", "email-changed-by-staff",
            "email-set-by-staff", "staff-reset-password", "takedown-removed", "takedown-restored", "ticket-confirm", "ticket-access", "ticket-staff-reply", "ticket-new", "ticket-reply-guest"], EmailTemplates.All);
        foreach (var id in EmailTemplates.All)
        {
            var d = EmailTemplates.Default(id);
            Assert.Empty(EmailTemplates.MissingWords(id, d.Subject, d.Body));
            Assert.False(string.IsNullOrWhiteSpace(EmailTemplates.Title(id)));
        }
        Assert.Contains("{Link}", EmailTemplates.Required("reset-password"));
        Assert.Contains("{ExpiresIn}", EmailTemplates.Required("confirm-email"));
        Assert.DoesNotContain("{Link}", EmailTemplates.Required("account-approved"));
        Assert.Contains("{Link}", EmailTemplates.Words("account-approved"));
    }

    [Fact]
    public void Denied_renders_empty_note()
    {
        var r = EmailTemplates.Render("signup-denied", EmailTemplates.Default("signup-denied"),
            new Dictionary<string, string> { ["AvatarName"] = "Ann Avatar", ["GridName"] = "Avalon", ["Note"] = "" });

        Assert.Equal("Your sign-up on Avalon", r.Subject);
        Assert.Equal("Hello Ann Avatar,\n\nYour sign-up on Avalon wasn't approved.\n\n", r.Text);
        Assert.Equal(["{GridName}", "{AvatarName}", "{Note}"], EmailTemplates.Words("signup-denied"));
    }

    [Fact]
    public void Waiting_needs_its_link()
    {
        Assert.Equal(["{GridName}", "{Count}", "{Names}", "{Link}"], EmailTemplates.Words("signups-waiting"));
        Assert.Equal(["{Link}"], EmailTemplates.Required("signups-waiting"));
        var r = EmailTemplates.Render("signups-waiting", EmailTemplates.Default("signups-waiting"),
            new Dictionary<string, string> { ["GridName"] = "Avalon", ["Count"] = "2", ["Names"] = "Ann Avatar\nBo Avatar", ["Link"] = "http://h/" });
        Assert.Equal("2 waiting for approval on Avalon", r.Subject);
        Assert.Contains("Ann Avatar\nBo Avatar", r.Text);
    }

    [Fact]
    public void Missing_link_is_reported()
    {
        Assert.Equal(["{Link}", "{ExpiresIn}"], EmailTemplates.MissingWords("reset-password", "s", "no link"));
        Assert.Empty(EmailTemplates.MissingWords("reset-password", "Reset {ExpiresIn}", "Go to {Link}"));
    }

    [Fact]
    public void Current_falls_back_to_default()
    {
        var s = TestSettings.Create();
        Assert.Equal(EmailTemplates.Default("test"), EmailTemplates.Current(s, "test", "en"));
        var mine = new EmailTemplateText("Hello", "Mine {GridName}", Saved);
        var withMine = s with
        {
            Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
            {
                ["en"] = new Dictionary<string, EmailTemplateText> { ["test"] = mine },
            }),
        };
        Assert.Equal(mine, EmailTemplates.Current(withMine, "test", "en"));
        Assert.Equal(EmailTemplates.Default("reset-password"), EmailTemplates.Current(withMine, "reset-password", "en"));
    }

    private static LauncherSettings WithWording(string language, string id, EmailTemplateText text) => TestSettings.Create() with
    {
        Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
        {
            [language] = new Dictionary<string, EmailTemplateText> { [id] = text },
        }),
    };

    [Fact]
    public void Staff_emails_are_the_four_that_go_to_staff()
    {
        Assert.Equal(["test", "signups-waiting", "ticket-new", "ticket-reply-guest"], EmailTemplates.All.Where(EmailTemplates.IsStaffEmail));
    }

    [Fact]
    public void Staff_email_is_English_whatever_the_language()
    {
        foreach (var id in EmailTemplates.All.Where(EmailTemplates.IsStaffEmail))
            Assert.Equal(EmailTemplates.Default(id, "en"), EmailTemplates.Default(id, "de"));
        Assert.Equal(EmailTemplates.Default("ticket-new"), EmailTemplates.Default("ticket-new", "ru"));
        var mine = new EmailTemplateText("Mine", "Mine {Link}", Saved);
        Assert.Equal(mine, EmailTemplates.Current(WithWording("en", "ticket-new", mine), "ticket-new", "de"));
    }

    [Fact]
    public void Default_without_a_language_is_the_English_one()
    {
        foreach (var id in EmailTemplates.All)
            Assert.Equal(EmailTemplates.Default(id, "en"), EmailTemplates.Default(id));
    }

    [Fact]
    public void A_language_without_text_falls_back_to_English()
    {
        Assert.Equal(EmailTemplates.Default("confirm-email", "en"), EmailTemplates.Default("confirm-email", "xx"));
        Assert.Equal("30 minutes", EmailTemplates.Words("ExpiresIn.30m", "xx"));
    }

    [Fact]
    public void Owner_wording_in_another_language_is_not_used()
    {
        var s = WithWording("en", "confirm-email", new EmailTemplateText("Mine", "Mine {Link} {ExpiresIn}", Saved));
        Assert.Equal("Mine", EmailTemplates.Current(s, "confirm-email", "en").Subject);
        Assert.Equal(EmailTemplates.Default("confirm-email", "de"), EmailTemplates.Current(s, "confirm-email", "de"));
        Assert.NotEqual("Mine", EmailTemplates.Current(s, "confirm-email", "fr").Subject);
    }

    [Fact]
    public void Default_in_German_is_German()
    {
        var de = EmailTemplates.Default("confirm-email", "de");
        Assert.Contains("E-Mail", de.Subject);
        Assert.NotEqual(EmailTemplates.Default("confirm-email", "en"), de);
        Assert.Empty(EmailTemplates.MissingWords("confirm-email", de.Subject, de.Body));
        Assert.Equal("24 Stunden", EmailTemplates.Words("ExpiresIn.24h", "de"));
    }

    [Fact]
    public void Owner_wording_in_the_language_is_used()
    {
        var mine = new EmailTemplateText("Meins", "Meins {Link} {ExpiresIn}", Saved);
        var s = WithWording("de", "confirm-email", mine);
        Assert.Equal(mine, EmailTemplates.Current(s, "confirm-email", "de"));
        Assert.Equal(EmailTemplates.Default("confirm-email", "en"), EmailTemplates.Current(s, "confirm-email", "en"));
    }

    [Fact]
    public void A_missing_resident_text_throws_instead_of_giving_an_empty_email()
    {
        var e = Assert.Throws<InvalidOperationException>(() => EmailTemplates.Resource("no-such-email.Subject", Languages.Culture("en")));
        Assert.Contains("no-such-email.Subject", e.Message);
        Assert.Equal("30 minutes", EmailTemplates.Resource("ExpiresIn.30m", Languages.Culture("en")));
    }

    [Fact]
    public void Words_for_an_unknown_key_throw()
    {
        Assert.Throws<ArgumentException>(() => EmailTemplates.Words("ExpiresIn.never", "en"));
    }

    [Fact]
    public void Words_are_filled_in_both_versions()
    {
        var r = EmailTemplates.Render("reset-password", new("Reset for {AvatarName}", "Hi {AvatarName}, go to [the page]({Link}) within {ExpiresIn}.", Saved),
            new Dictionary<string, string> { ["AvatarName"] = "Ann Avatar", ["Link"] = "https://grid.example/reset?t=abc", ["ExpiresIn"] = "30 minutes", ["GridName"] = "Avalon" });
        Assert.Equal("Reset for Ann Avatar", r.Subject);
        Assert.Contains("Hi Ann Avatar, go to [the page](https://grid.example/reset?t=abc) within 30 minutes.", r.Text);
        Assert.Contains("href=\"https://grid.example/reset?t=abc\"", r.Html);
        Assert.Contains("30 minutes", r.Html);
        Assert.DoesNotContain("{", r.Html);
    }

    [Fact]
    public void Values_are_escaped_in_html()
    {
        var r = EmailTemplates.Render("test", new("Hi", "Hello {AvatarName} *there*", Saved),
            new Dictionary<string, string> { ["AvatarName"] = "<script>x</script> [y](javascript:z)" });
        Assert.Contains("&lt;script&gt;", r.Html);
        Assert.DoesNotContain("<script", r.Html);
        Assert.DoesNotContain("href=\"javascript", r.Html);
        Assert.Contains("<em>there</em>", r.Html);
    }

    [Fact]
    public void Raw_html_in_body_is_inert()
    {
        var r = EmailTemplates.Render("test", new("Hi", "<img src=x onerror=alert(1)>", Saved), new Dictionary<string, string>());
        Assert.DoesNotContain("<img", r.Html);
    }

    [Fact]
    public void Subject_is_one_line()
    {
        var r = EmailTemplates.Render("test", new("Hi {AvatarName}", "b", Saved),
            new Dictionary<string, string> { ["AvatarName"] = "A\r\nBcc: x@example.com" });
        Assert.DoesNotContain('\n', r.Subject);
        Assert.DoesNotContain('\r', r.Subject);
    }

    [Fact]
    public void Unknown_words_are_left_as_typed()
    {
        var r = EmailTemplates.Render("test", new("Hi", "See {Nothing}", Saved), new Dictionary<string, string>());
        Assert.Contains("{Nothing}", r.Text);
    }

    [Fact]
    public void Sample_fills_every_word()
    {
        foreach (var id in EmailTemplates.All)
        {
            var sample = EmailTemplates.Sample(id, "Avalon");
            foreach (var word in EmailTemplates.Words(id))
                Assert.True(sample.ContainsKey(word.Trim('{', '}')), $"{id} {word}");
            Assert.Equal("Avalon", sample["GridName"]);
        }
    }

    [Fact]
    public void Password_changed_default_has_no_link()
    {
        var d = EmailTemplates.Default(EmailTemplates.PasswordChanged);
        Assert.Equal("password-changed", EmailTemplates.PasswordChanged);
        Assert.Empty(EmailTemplates.Required(EmailTemplates.PasswordChanged));
        Assert.DoesNotContain("{Link}", d.Body);
        Assert.Contains("{GridName}", d.Subject);
        Assert.Contains("{AvatarName}", d.Body);
    }

    [Fact]
    public void Staff_notice_templates()
    {
        Assert.Equal(["{GridName}", "{OldName}", "{AvatarName}"], EmailTemplates.Words(EmailTemplates.NameChanged));
        Assert.Equal(["{GridName}", "{AvatarName}", "{Link}"], EmailTemplates.Words(EmailTemplates.EmailChangedByStaff));
        Assert.Equal(["{GridName}", "{AvatarName}"], EmailTemplates.Words(EmailTemplates.EmailSetByStaff));
        Assert.Equal(["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], EmailTemplates.Words(EmailTemplates.StaffResetPassword));
        Assert.Equal(["{Link}", "{ExpiresIn}"], EmailTemplates.Required(EmailTemplates.StaffResetPassword));
        Assert.Contains("two-factor", EmailTemplates.Default(EmailTemplates.StaffResetPassword).Body);
        foreach (var id in new[] { EmailTemplates.NameChanged, EmailTemplates.EmailChangedByStaff, EmailTemplates.EmailSetByStaff, EmailTemplates.StaffResetPassword })
            Assert.Contains(id, EmailTemplates.All);
    }

    [Fact]
    public void Notice_template_never_has_new_address_word()
    {
        Assert.Equal(["{GridName}", "{AvatarName}", "{Link}"], EmailTemplates.Words(EmailTemplates.EmailChangeNotice));
        Assert.Equal(["{Link}"], EmailTemplates.Required(EmailTemplates.EmailChangeNotice));
    }

    [Fact]
    public void Confirm_new_email_renders_link_and_expiry()
    {
        var d = EmailTemplates.Default(EmailTemplates.ConfirmNewEmail);
        Assert.Equal(["{Link}", "{ExpiresIn}"], EmailTemplates.Required(EmailTemplates.ConfirmNewEmail));
        var mail = EmailTemplates.Render(EmailTemplates.ConfirmNewEmail, d, new Dictionary<string, string>
        {
            ["GridName"] = "Dream", ["AvatarName"] = "Ann Smith", ["Link"] = "http://x/guest/confirm-email?token=abc", ["ExpiresIn"] = "24 hours",
        });
        Assert.Equal("Confirm your new email address", mail.Subject);
        Assert.Contains("http://x/guest/confirm-email?token=abc", mail.Text);
        Assert.Contains("24 hours", mail.Text);
        Assert.Contains("Ann Smith", mail.Text);
    }

    // ---- a language the owner added: its file's email texts, English per key where it has none ----

    private static OwnerLanguages Italian(Dictionary<string, string> email)
    {
        var store = new OwnerLanguages(Path.Combine(Path.GetTempPath(), "dg-email-langs-" + Guid.NewGuid().ToString("N")), TimeProvider.System);
        store.Save(new OwnerLanguage("it", new Dictionary<string, string>(), email, Saved));
        return store;
    }

    [Fact]
    public void Email_in_an_added_language_uses_its_file_then_English_per_key()
    {
        var store = Italian(new()
        {
            ["confirm-email.Subject"] = "Conferma la tua email su {GridName}",
            ["confirm-email.Body"] = "  ", // blank reads as missing
            ["ExpiresIn.24h"] = "24 ore",
        });
        var english = EmailTemplates.Default("confirm-email", "en");

        var it = EmailTemplates.Default("confirm-email", "it", store);
        Assert.Equal("Conferma la tua email su {GridName}", it.Subject);
        Assert.Equal(english.Body, it.Body);
        Assert.Equal("24 ore", EmailTemplates.Words("ExpiresIn.24h", "it", store));
        Assert.Equal("30 minutes", EmailTemplates.Words("ExpiresIn.30m", "it", store));
        Assert.Equal(EmailTemplates.Words("Reason.Copyright", "en"), EmailTemplates.Words("Reason.Copyright", "it", store));

        // Current reads the same, an owner override in Italian wins, and a staff email stays English.
        var s = TestSettings.Create();
        Assert.Equal(it, EmailTemplates.Current(s, "confirm-email", "it", store));
        var mine = new EmailTemplateText("Mia", "Mia {Link} {ExpiresIn}", Saved);
        Assert.Equal(mine, EmailTemplates.Current(WithWording("it", "confirm-email", mine), "confirm-email", "it", store));
        Assert.Equal(EmailTemplates.Default("ticket-new"), EmailTemplates.Current(s, "ticket-new", "it", store));

        // Without the store, or for a built-in language, the file is not read.
        Assert.Equal(english, EmailTemplates.Default("confirm-email", "it"));
        Assert.Equal(EmailTemplates.Default("confirm-email", "de"), EmailTemplates.Default("confirm-email", "de", store));
        Assert.Equal("24 hours", EmailTemplates.Words("ExpiresIn.24h", "it"));
    }
}
