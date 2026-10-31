using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The reset page the email links to (<c>/guest/reset-password</c>).</summary>
public sealed partial class PasswordResetTests
{
    private const string Expired = "This link has expired or was already used. Ask for a new one.";
    private const string WrongCodeText = "That code isn't right. Try again, or use a backup code.";
    private const string NewPassword = "tall-green-kettle";

    /// <summary>Asks for a reset for <paramref name="name"/> and returns the token from the email it gets.</summary>
    private static async Task<string> LinkTokenAsync(Rig rig, string name, string email)
    {
        int before;
        lock (rig.Transport.Sent) before = rig.Transport.Sent.Count;
        await AskAsync(Client(rig), name, email);
        var sent = await SentAsync(rig, before + 1);
        Assert.True(sent.Count > before, "no reset email was sent");
        var m = System.Text.RegularExpressions.Regex.Match(sent[before].Email.Text, @"/guest/reset-password\?token=([A-Za-z0-9_-]+)");
        Assert.True(m.Success);
        return m.Groups[1].Value;
    }

    private static async Task<IHtmlDocument> ResetAsync(HttpClient client, string token, string password = NewPassword,
        string? again = null, string? code = null)
    {
        var page = await PageAsync(client, "/guest/reset-password?token=" + token);
        var form = new Dictionary<string, string>
        {
            ["Token"] = token, ["Password"] = password, ["Again"] = again ?? password, ["__RequestVerificationToken"] = Token(page),
        };
        if (code is not null) form["Code"] = code;
        using var r = await client.PostAsync("/guest/reset-password", new FormUrlEncodedContent(form), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string? Done(IHtmlDocument page) => page.QuerySelector("#reset-done")?.TextContent.Trim();
    private static string? Error(IHtmlDocument page) => page.QuerySelector(".error")?.TextContent.Trim();

    private static async Task<bool> PasswordWorksAsync(Rig rig, Guid principal, string password) =>
        await rig.F.Robust.AuthenticateAsync(principal, password, Ct);

    /// <summary>The test guest and test admin (the ones the test sign-in routes use) with an email address.</summary>
    private static void GiveEmails(Rig rig)
    {
        rig.F.Robust.Add("Test", "Guest", 0, "pass-word1", TestAuth.GuestId, email: "guest@example.com");
        rig.F.Robust.Add("Test", "Admin", 200, "pass-word1", TestAuth.AdminId, email: "admin@example.com");
    }

    private static (byte[] Secret, IReadOnlyList<string> Backup) Enrol(Rig rig, TwoFactorStore store, Guid principal, string name)
    {
        var (secret, _) = store.BeginEnrol(principal, name, "Avalon");
        var backup = store.ConfirmEnrol(principal, CodeNow(rig, secret), secret);
        Assert.NotNull(backup);
        rig.F.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return (secret, backup);
    }

    private static string CodeNow(Rig rig, byte[] secret) => Totp.Code(secret, Totp.StepAt(rig.F.Time.GetUtcNow()));

    private static RecoverySettings CodeForStaff => new(RecoveryRule.EmailLink, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode);
    private static RecoverySettings CodeForAll => new(RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode);

    [Fact]
    public async Task Full_reset_then_sign_in_with_new_password()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");

        var page = await ResetAsync(Client(rig), token);

        Assert.Equal("Your password is changed. Sign in with your new password.", Done(page));
        Assert.NotNull(page.QuerySelector("#reset-done-link[href='/guest/sign-in']"));
        Assert.True(await PasswordWorksAsync(rig, AnnId, NewPassword));
        Assert.False(await PasswordWorksAsync(rig, AnnId, "old-password-1"));
        Assert.Equal(1, Actions(rig.F, "password-reset"));
    }

    [Fact]
    public async Task Old_sessions_end_after_reset()
    {
        await using var rig = Factory();
        GiveEmails(rig);
        var guest = Client(rig);
        await guest.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/guest", Ct)).StatusCode);
        var admin = Client(rig);
        await admin.GetAsync("/__test/signin?role=admin", Ct);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/dashboard", Ct)).StatusCode);

        Assert.NotNull(Done(await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Test Guest", "guest@example.com"))));
        Assert.NotNull(Done(await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Test Admin", "admin@example.com"))));

        Assert.NotEqual(HttpStatusCode.OK, (await guest.GetAsync("/guest", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync("/dashboard", Ct)).StatusCode);
    }

    [Fact]
    public async Task Notice_email_sent_after_reset()
    {
        await using var rig = Factory();
        await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Ann Smith", "ann@example.com"));

        var sent = await SentAsync(rig, 2);
        Assert.Equal(2, sent.Count);
        Assert.Equal("Your password on Avalon was changed", sent[1].Email.Subject);
        Assert.Equal("ann@example.com", sent[1].To);
    }

    [Fact]
    public async Task Second_use_of_a_link_changes_nothing()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");
        var second = Client(rig);
        var page2 = await PageAsync(second, "/guest/reset-password?token=" + token);

        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));
        using var r = await second.PostAsync("/guest/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = token, ["Password"] = "another-password-9", ["Again"] = "another-password-9", ["__RequestVerificationToken"] = Token(page2),
        }), Ct);

        Assert.Contains(Expired, await r.Content.ReadAsStringAsync(Ct));
        Assert.True(await PasswordWorksAsync(rig, AnnId, NewPassword));
        Assert.Equal(1, Actions(rig.F, "password-reset"));
    }

    [Fact]
    public async Task Failed_write_keeps_the_link()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");
        rig.F.Robust.PasswordsDown = true;

        Assert.Equal("Couldn't change the password right now. Try again in a few minutes.", Error(await ResetAsync(Client(rig), token)));

        rig.F.Robust.PasswordsDown = false;
        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));
    }

    [Fact]
    public async Task Expired_link_after_31_minutes()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");
        rig.F.Time.Advance(TimeSpan.FromMinutes(31));

        var page = await PageAsync(Client(rig), "/guest/reset-password?token=" + token);

        Assert.Equal(Expired, page.QuerySelector("#reset-expired")?.TextContent.Trim());
        Assert.Null(page.QuerySelector("form#reset"));
    }

    [Fact]
    public async Task Rule_with_code_but_no_two_factor_needs_link_only()
    {
        await using var rig = Factory(CodeForAll);
        var token = await LinkTokenAsync(rig, "Adam Admin", "adam@example.com");

        Assert.Null((await PageAsync(Client(rig), "/guest/reset-password?token=" + token)).QuerySelector("input[name=Code]"));
        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));
    }

    [Fact]
    public async Task Code_required_when_enrolled()
    {
        await using var rig = Factory(CodeForAll);
        GiveEmails(rig);
        var (staffSecret, _) = Enrol(rig, rig.F.Services.GetRequiredService<TwoFactorStore>(), TestAuth.AdminId, "Test Admin");
        var (guestSecret, _) = Enrol(rig, rig.F.Services.GetRequiredService<ResidentTwoFactor>().Store, TestAuth.GuestId, "Test Guest");

        var adminToken = await LinkTokenAsync(rig, "Test Admin", "admin@example.com");
        Assert.NotNull((await PageAsync(Client(rig), "/guest/reset-password?token=" + adminToken)).QuerySelector("input[name=Code]"));
        Assert.Equal(WrongCodeText, Error(await ResetAsync(Client(rig), adminToken, code: "")));
        Assert.NotNull(Done(await ResetAsync(Client(rig), adminToken, code: CodeNow(rig, staffSecret))));

        var guestToken = await LinkTokenAsync(rig, "Test Guest", "guest@example.com");
        Assert.NotNull(Done(await ResetAsync(Client(rig), guestToken, code: CodeNow(rig, guestSecret))));
    }

    [Fact]
    public async Task Rule_without_code_ignores_two_factor()
    {
        await using var rig = Factory();
        GiveEmails(rig);
        Enrol(rig, rig.F.Services.GetRequiredService<TwoFactorStore>(), TestAuth.AdminId, "Test Admin");
        Assert.NotNull(Done(await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Test Admin", "admin@example.com"))));
    }

    [Fact]
    public async Task Wrong_code_counts_in_throttle_and_locks_after_five()
    {
        await using var rig = Factory(CodeForStaff);
        GiveEmails(rig);
        var (secret, _) = Enrol(rig, rig.F.Services.GetRequiredService<TwoFactorStore>(), TestAuth.AdminId, "Test Admin");
        var token = await LinkTokenAsync(rig, "Test Admin", "admin@example.com");

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
            Assert.Equal(WrongCodeText, Error(await ResetAsync(Client(rig), token, code: "000000x")));
        var locked = await ResetAsync(Client(rig), token, code: CodeNow(rig, secret));

        Assert.Equal("Too many attempts. Try again in a few minutes.", Error(locked));
        Assert.True(rig.F.Services.GetRequiredService<LoginThrottle>().IsLocked(SignInDoor.Staff, AvatarName.Parse("Test Admin").Key, null));
        Assert.Equal(LoginThrottle.NameMaxFailures, Actions(rig.F, "reset-code-wrong"));
        Assert.False(await PasswordWorksAsync(rig, TestAuth.AdminId, NewPassword));
    }

    [Fact]
    public async Task Backup_code_works_once()
    {
        await using var rig = Factory(CodeForStaff);
        GiveEmails(rig);
        var (_, backup) = Enrol(rig, rig.F.Services.GetRequiredService<TwoFactorStore>(), TestAuth.AdminId, "Test Admin");

        Assert.NotNull(Done(await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Test Admin", "admin@example.com"), code: backup[0])));
        var again = await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Test Admin", "admin@example.com"), "another-password-9", code: backup[0]);
        Assert.Equal(WrongCodeText, Error(again));
    }

    [Fact]
    public async Task Link_refused_after_rule_turned_off()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() with
        {
            Recovery = new RecoverySettings(RecoveryRule.Off, RecoveryRule.EmailLink, RecoveryRule.EmailLink),
        });

        var page = await PageAsync(Client(rig), "/guest/reset-password?token=" + token);

        Assert.Equal(Expired, page.QuerySelector("#reset-expired")?.TextContent.Trim());
    }

    [Fact]
    public async Task Weak_password_shows_the_rule()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");

        Assert.Equal("Use at least 8 characters.", Error(await ResetAsync(Client(rig), token, "short")));
        Assert.Equal("Type the same password twice.", Error(await ResetAsync(Client(rig), token, NewPassword, "different-one")));
        Assert.Equal("That password is too common. Choose another.", Error(await ResetAsync(Client(rig), token, "password123")));
        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));
    }

    [Fact]
    public async Task Staff_done_page_links_to_login()
    {
        await using var rig = Factory();
        var page = await ResetAsync(Client(rig), await LinkTokenAsync(rig, "Adam Admin", "adam@example.com"));
        Assert.NotNull(page.QuerySelector("#reset-done-link[href='/login']"));
    }

    [Fact]
    public async Task Reset_page_without_a_token_says_expired()
    {
        await using var rig = Factory();
        var page = await PageAsync(Client(rig), "/guest/reset-password");
        Assert.Equal(Expired, page.QuerySelector("#reset-expired")?.TextContent.Trim());
    }

    [Fact]
    public async Task Reset_audit_and_log_have_no_secrets()
    {
        await using var rig = Factory();
        var token = await LinkTokenAsync(rig, "Ann Smith", "ann@example.com");
        await ResetAsync(Client(rig), token);

        var text = string.Join('\n', Audit(rig.F).Select(e => e.GetRawText()));
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(NewPassword, text);
        Assert.DoesNotContain("example.com", text);
        var logs = string.Join('\n', rig.F.Logs.Select(l => l.Message));
        Assert.DoesNotContain(NewPassword, logs);
        Assert.DoesNotContain(token, logs);
    }
}
