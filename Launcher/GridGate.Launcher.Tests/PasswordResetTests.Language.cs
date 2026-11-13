using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Reset emails go out in the language of the page asked on, or of the resident staff send the link to.</summary>
public sealed partial class PasswordResetTests
{
    /// <summary>The owner's own German wording for both reset emails; the plain English built-in has none of these words.</summary>
    private static LauncherSettings GermanWording(LauncherSettings s) => s with
    {
        Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
        {
            ["de"] = new Dictionary<string, EmailTemplateText>
            {
                [EmailTemplates.ResetPassword] = new("Passwort zuruecksetzen fuer {GridName}", "Hallo {AvatarName}, {Link} gilt {ExpiresIn}.", DateTimeOffset.UnixEpoch),
                [EmailTemplates.StaffResetPassword] = new("Neues Passwort fuer {GridName}", "Hallo {AvatarName}, {Link} gilt {ExpiresIn}.", DateTimeOffset.UnixEpoch),
            },
        }),
    };

    [Fact]
    public async Task Forgot_password_from_a_German_page_sends_German()
    {
        await using var rig = Factory();
        var client = Client(rig);
        client.DefaultRequestHeaders.Add("Cookie", "dg-lang=de");
        await AskAsync(client, "Ann Smith", "ann@example.com");

        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Equal(EmailTemplates.Render(EmailTemplates.ResetPassword, EmailTemplates.Default(EmailTemplates.ResetPassword, "de"),
            new Dictionary<string, string> { ["GridName"] = "Avalon" }).Subject, sent.Email.Subject);
        Assert.Contains("Passwort", sent.Email.Subject);
        Assert.Contains("30 Minuten", sent.Email.Text);
        Assert.DoesNotContain("minutes", sent.Email.Text);
    }

    [Fact]
    public async Task Forgot_password_from_a_German_page_uses_the_owners_German_wording()
    {
        await using var rig = Factory(also: GermanWording);
        var client = Client(rig);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de");
        await AskAsync(client, "Ann Smith", "ann@example.com");

        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Equal("Passwort zuruecksetzen fuer Avalon", sent.Email.Subject);
        Assert.Contains("30 Minuten", sent.Email.Text);
    }

    [Fact]
    public async Task Forgot_password_from_an_English_page_ignores_the_German_wording()
    {
        await using var rig = Factory(also: GermanWording);
        await AskAsync(Client(rig), "Ann Smith", "ann@example.com");

        Assert.Equal("Reset your Avalon password", Assert.Single(await SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Staff_reset_link_goes_in_the_residents_saved_language()
    {
        await using var rig = Factory(also: GermanWording);
        rig.F.Services.GetRequiredService<ResidentLanguages>().Set(AnnId, "de");

        Assert.Equal(Guests.StaffResetResult.Sent, Resets(rig).SendByStaff(await AnnAsync(rig)));

        Assert.Equal("Neues Passwort fuer Avalon", Assert.Single(await SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Staff_reset_link_to_a_resident_with_no_saved_language_goes_in_the_grid_default()
    {
        await using var rig = Factory(also: s => GermanWording(s) with { Languages = s.Languages with { Default = "de" } });

        Assert.Equal(Guests.StaffResetResult.Sent, Resets(rig).SendByStaff(await AnnAsync(rig)));

        Assert.Equal("Neues Passwort fuer Avalon", Assert.Single(await SentAsync(rig, 1)).Email.Subject);
    }
}
