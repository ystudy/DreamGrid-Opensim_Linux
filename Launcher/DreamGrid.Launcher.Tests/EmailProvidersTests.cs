using DreamGrid.Launcher.Core.Email;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The provider cards on the Email page: the free services researched with the owner, plus Other server.</summary>
public sealed class EmailProvidersTests
{
    [Fact]
    public void Catalog_has_eight_cards_in_order()
    {
        Assert.Equal(["gmail", "yahoo", "yandex", "icloud", "sendpulse", "brevo", "mailjet", "custom"], EmailProviders.All.Select(p => p.Key));
        var gmail = EmailProviders.Find("gmail");
        Assert.NotNull(gmail);
        Assert.Equal("smtp.gmail.com", gmail.Host);
        Assert.Equal(587, gmail.Port);
        Assert.Equal(MailSecurity.StartTls, gmail.Security);
        Assert.Equal(500, gmail.PerDay);
        Assert.Equal(50, EmailProviders.Find("sendpulse")!.PerHour);
        Assert.Null(EmailProviders.Find("hotmail"));
        Assert.Equal("Custom", EmailProviders.Find(EmailProviders.Custom)!.Kind);
    }

    [Fact]
    public void Named_cards_never_use_plain_text()
    {
        foreach (var p in EmailProviders.All.Where(p => p.Key != EmailProviders.Custom))
        {
            Assert.NotEqual(MailSecurity.None, p.Security);
            Assert.False(string.IsNullOrEmpty(p.Host), p.Key);
            Assert.NotEmpty(p.Steps);
        }
    }

    [Fact]
    public void Mask_keeps_first_letter_and_domain()
    {
        Assert.Equal("a…@example.com", EmailMask.Of("ann@example.com"));
        Assert.Equal("…", EmailMask.Of("nobody"));
        Assert.Equal("…", EmailMask.Of(""));
    }
}
