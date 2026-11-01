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
        Assert.Equal(["test", "confirm-email", "reset-password", "account-approved", "password-changed", "already-have-account"], EmailTemplates.All);
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
    public void Missing_link_is_reported()
    {
        Assert.Equal(["{Link}", "{ExpiresIn}"], EmailTemplates.MissingWords("reset-password", "s", "no link"));
        Assert.Empty(EmailTemplates.MissingWords("reset-password", "Reset {ExpiresIn}", "Go to {Link}"));
    }

    [Fact]
    public void Current_falls_back_to_default()
    {
        var s = TestSettings.Create();
        Assert.Equal(EmailTemplates.Default("test"), EmailTemplates.Current(s, "test"));
        var mine = new EmailTemplateText("Hello", "Mine {GridName}", Saved);
        var withMine = s with
        {
            Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
            {
                ["en"] = new Dictionary<string, EmailTemplateText> { ["test"] = mine },
            }),
        };
        Assert.Equal(mine, EmailTemplates.Current(withMine, "test"));
        Assert.Equal(EmailTemplates.Default("reset-password"), EmailTemplates.Current(withMine, "reset-password"));
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
}
