using System.Net;
using System.Text.Json;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Changing one's own password and email, in the real host with a fake Robust and a fake mail transport (spec 2026-10-31-resident-account-design §5, §6). All names are made up.</summary>
public sealed class AccountChangesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.61");
    private static readonly Guid AnnId = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private const string Old = "old-password-1";
    private const string New = "tall-green-kettle";

    private sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public AccountChanges Changes => F.Services.GetRequiredService<AccountChanges>();
        public LoginThrottle Throttle => F.Services.GetRequiredService<LoginThrottle>();
        public TwoFactorStore Residents => F.Services.GetRequiredService<ResidentTwoFactor>().Store;
        public RobustAccount Ann => F.Robust.All.Single(a => a.PrincipalId == AnnId);
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Factory(bool email = true, string annEmail = "ann@example.com")
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Recovery = new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLink, RecoveryRule.EmailLink),
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, Old, AnnId, email: annEmail);
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        return new Rig(f, transport);
    }

    private static string Key => AvatarName.Parse("Ann Smith").Key;

    private static Task<List<(EmailAccount Account, string Password, string To, RenderedEmail Email)>> SentAsync(Rig rig, int atLeast) =>
        SignUpsTests.SentAsync(new SignUpsTests.Rig(rig.F, rig.Transport), atLeast);

    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static List<string> Actions(LauncherFactory f) => AuditText(f).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()!).ToList();

    private static (byte[] Secret, IReadOnlyList<string> Backup) Enrol(Rig rig)
    {
        var (secret, _) = rig.Residents.BeginEnrol(AnnId, "Ann Smith", "Avalon");
        var backup = rig.Residents.ConfirmEnrol(AnnId, Totp.Code(secret, Totp.StepAt(rig.F.Time.GetUtcNow())), secret);
        Assert.NotNull(backup);
        rig.F.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return (secret, backup);
    }

    private static string CodeNow(Rig rig, byte[] secret) => Totp.Code(secret, Totp.StepAt(rig.F.Time.GetUtcNow()));

    private static Task<AccountOutcome> ChangeAsync(Rig rig, string current = Old, string? code = null, string password = New, string? again = null) =>
        rig.Changes.ChangePasswordAsync(AnnId, current, code, password, again ?? password, Caller, Ct);

    private static Task<AccountOutcome> AskAsync(Rig rig, string email = "ann.new@example.com", string current = Old, string? code = null) =>
        rig.Changes.AskEmailChangeAsync(AnnId, current, code, email, Caller, Ct);

    private static async Task<string> AskedTokenAsync(Rig rig, string email = "ann.new@example.com", string current = Old)
    {
        Assert.Equal(AccountResult.Done, (await AskAsync(rig, email, current)).Result);
        var sent = await SentAsync(rig, 1);
        return SignUpsTests.LinkToken(sent.Last(m => m.To == email).Email);
    }

    // ---- Change password (§5) ----

    [Fact]
    public async Task Wrong_current_counts_in_guest_throttle()
    {
        await using var rig = Factory();
        for (var i = 0; i < 5; i++) Assert.Equal(AccountResult.WrongPassword, (await ChangeAsync(rig, current: "nope-nope-1")).Result);
        Assert.True(rig.Throttle.IsLocked(SignInDoor.Guest, Key, Caller));
        Assert.False(rig.Throttle.IsLocked(SignInDoor.Staff, Key, IPAddress.Parse("10.0.0.9")));
    }

    [Fact]
    public async Task Locked_checks_nothing()
    {
        await using var rig = Factory();
        for (var i = 0; i < 5; i++) rig.Throttle.RecordFailure(SignInDoor.Guest, Key, Caller);
        var r = await ChangeAsync(rig);
        Assert.Equal(AccountResult.Locked, r.Result);
        Assert.True(await rig.F.Robust.AuthenticateAsync(AnnId, Old, Ct));
    }

    [Fact]
    public async Task Code_asked_only_with_resident_two_factor()
    {
        await using var rig = Factory();
        Assert.False(rig.Changes.NeedsCode(AnnId));
        Assert.False(rig.Changes.NeedsCode(TestAuth.AdminId));
        Enrol(rig);
        Assert.True(rig.Changes.NeedsCode(AnnId));
        Assert.Equal(AccountResult.WrongCode, (await ChangeAsync(rig)).Result);
    }

    [Fact]
    public async Task Wrong_code_counts_and_backup_not_spent()
    {
        await using var rig = Factory();
        var (_, backup) = Enrol(rig);
        Assert.Equal(AccountResult.WrongCode, (await ChangeAsync(rig, code: "000000")).Result);
        // A right backup code with a weak password: refused, and the backup code still works afterwards.
        Assert.Equal(AccountResult.Invalid, (await ChangeAsync(rig, code: backup[0], password: "short")).Result);
        Assert.True(rig.Residents.CheckCode(AnnId, backup[0]));
        Assert.Equal(AccountResult.Done, (await ChangeAsync(rig, code: backup[0])).Result);
        Assert.False(rig.Residents.CheckCode(AnnId, backup[0]));
    }

    [Fact]
    public async Task Same_password_refused()
    {
        await using var rig = Factory();
        var r = await ChangeAsync(rig, password: Old);
        Assert.Equal(AccountResult.Unchanged, r.Result);
        Assert.Equal(AccountChanges.SamePassword, r.Problem);
    }

    [Fact]
    public async Task Weak_password_refused_and_nothing_written()
    {
        await using var rig = Factory();
        var r = await ChangeAsync(rig, password: "short");
        Assert.Equal(AccountResult.Invalid, r.Result);
        Assert.False(string.IsNullOrEmpty(r.Problem));
        Assert.True(await rig.F.Robust.AuthenticateAsync(AnnId, Old, Ct));
    }

    [Fact]
    public async Task Write_failure_is_busy_and_sessions_kept()
    {
        await using var rig = Factory();
        var stamps = rig.F.Services.GetRequiredService<GuestStamps>();
        var before = stamps.StampOf(AnnId);
        rig.F.Robust.PasswordsDown = true;
        Assert.Equal(AccountResult.Busy, (await ChangeAsync(rig)).Result);
        Assert.Equal(before, stamps.StampOf(AnnId));
    }

    [Fact]
    public async Task Done_rotates_guest_stamp()
    {
        await using var rig = Factory();
        var stamps = rig.F.Services.GetRequiredService<GuestStamps>();
        var before = stamps.StampOf(AnnId);
        Assert.Equal(AccountResult.Done, (await ChangeAsync(rig)).Result);
        Assert.NotEqual(before, stamps.StampOf(AnnId));
        Assert.True(await rig.F.Robust.AuthenticateAsync(AnnId, New, Ct));
        Assert.Contains("password-changed", Actions(rig.F));
    }

    [Fact]
    public async Task Done_ends_staff_sessions_for_staff_only()
    {
        await using var rig = Factory();
        var sessions = rig.F.Services.GetRequiredService<StaffSessions>();
        var adminStamp = sessions.StampOf(TestAuth.AdminId);
        var annStamp = sessions.StampOf(AnnId);

        Assert.Equal(AccountResult.Done, (await ChangeAsync(rig)).Result);
        Assert.Equal(annStamp, sessions.StampOf(AnnId));

        var r = await rig.Changes.ChangePasswordAsync(TestAuth.AdminId, "pass-word1", null, New, New, Caller, Ct);
        Assert.Equal(AccountResult.Done, r.Result);
        Assert.NotEqual(adminStamp, sessions.StampOf(TestAuth.AdminId));
    }

    [Fact]
    public async Task Done_queues_password_changed()
    {
        await using var rig = Factory();
        Assert.Equal(AccountResult.Done, (await ChangeAsync(rig)).Result);
        var sent = await SentAsync(rig, 1);
        Assert.Contains(sent, m => m.To == "ann@example.com" && m.Email.Subject.Contains("was changed"));
    }

    [Fact]
    public async Task Robust_down_is_busy_not_counted()
    {
        await using var rig = Factory();
        rig.F.Robust.Down = true;
        for (var i = 0; i < 6; i++) Assert.Equal(AccountResult.Busy, (await ChangeAsync(rig)).Result);
        Assert.False(rig.Throttle.IsLocked(SignInDoor.Guest, Key, Caller));
    }

    // ---- Change email (§6) ----

    [Fact]
    public async Task No_email_set_up_is_NoEmail()
    {
        await using var rig = Factory(email: false);
        Assert.False(rig.Changes.EmailChangeAvailable());
        Assert.Equal(AccountResult.NoEmail, (await AskAsync(rig)).Result);
    }

    [Fact]
    public async Task Ask_needs_current_password()
    {
        await using var rig = Factory();
        Assert.Equal(AccountResult.WrongPassword, (await AskAsync(rig, current: "nope-nope-1")).Result);
        await Task.Delay(100, Ct);
        lock (rig.Transport.Sent) Assert.Empty(rig.Transport.Sent);
    }

    [Fact]
    public async Task Same_address_any_case_refused()
    {
        await using var rig = Factory();
        var r = await AskAsync(rig, email: " ANN@Example.com ");
        Assert.Equal(AccountResult.Unchanged, r.Result);
        Assert.Equal(AccountChanges.SameAddress, r.Problem);
    }

    [Fact]
    public async Task Ask_sends_link_to_new_and_notice_to_old()
    {
        await using var rig = Factory();
        Assert.Equal(AccountResult.Done, (await AskAsync(rig)).Result);
        var sent = await SentAsync(rig, 2);
        var link = sent.Single(m => m.To == "ann.new@example.com");
        Assert.Contains(AccountChanges.ConfirmPath + "?token=", link.Email.Text);
        Assert.Contains("24 hours", link.Email.Text);
        var notice = sent.Single(m => m.To == "ann@example.com");
        Assert.Contains(PasswordResets.AskPath, notice.Email.Text);
        Assert.Equal("ann@example.com", rig.Ann.Email);
        Assert.Contains("email-change-asked", Actions(rig.F));
    }

    [Fact]
    public async Task Notice_never_contains_new_address()
    {
        await using var rig = Factory();
        await AskAsync(rig);
        var notice = (await SentAsync(rig, 2)).Single(m => m.To == "ann@example.com");
        Assert.DoesNotContain("ann.new", notice.Email.Text);
        Assert.DoesNotContain("ann.new", notice.Email.Html);
        Assert.DoesNotContain("ann.new", notice.Email.Subject);
    }

    [Fact]
    public async Task No_old_address_no_notice()
    {
        await using var rig = Factory(annEmail: "");
        Assert.Equal(AccountResult.Done, (await AskAsync(rig)).Result);
        var sent = await SentAsync(rig, 1);
        await Task.Delay(100, Ct);
        lock (rig.Transport.Sent) Assert.Single(rig.Transport.Sent);
        Assert.Equal("ann.new@example.com", sent.Single().To);
    }

    [Fact]
    public async Task Fourth_ask_in_an_hour_sends_nothing_same_reply()
    {
        await using var rig = Factory();
        for (var i = 0; i < 4; i++) Assert.Equal(AccountResult.Done, (await AskAsync(rig)).Result);
        await SentAsync(rig, 6);
        await Task.Delay(150, Ct);
        lock (rig.Transport.Sent) Assert.Equal(3, rig.Transport.Sent.Count(m => m.To == "ann.new@example.com"));
    }

    [Fact]
    public async Task Second_ask_replaces_first()
    {
        await using var rig = Factory();
        var first = await AskedTokenAsync(rig, "first@example.com");
        var second = await AskedTokenAsync(rig, "second@example.com");
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(first, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
        var r = await rig.Changes.ConfirmEmailChangeAsync(second, Ct);
        Assert.Equal((EmailLinkResult.Done, "second@example.com"), r);
        Assert.Equal("second@example.com", rig.Ann.Email);
    }

    [Fact]
    public async Task Link_changes_address_and_is_single_use()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        Assert.Equal((EmailLinkResult.Done, "ann.new@example.com"), await rig.Changes.ConfirmEmailChangeAsync(token, Ct));
        Assert.Equal("ann.new@example.com", rig.Ann.Email);
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        Assert.Contains("email-changed", Actions(rig.F));
    }

    [Fact]
    public async Task Expired_link_after_24_hours()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        rig.F.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
    }

    [Fact]
    public async Task Taken_by_other_account_refused()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        rig.F.Robust.Add("Bob", "Jones", 0, "p", email: "ann.new@example.com");
        Assert.Equal(EmailLinkResult.Taken, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
    }

    [Fact]
    public async Task Taken_ignores_case_and_spaces()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        rig.F.Robust.Add("Bob", "Jones", 0, "p", email: " Ann.New@EXAMPLE.com");
        Assert.Equal(EmailLinkResult.Taken, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
    }

    [Fact]
    public async Task Pending_sign_up_does_not_block()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        var squatter = Guid.NewGuid();
        rig.F.Robust.Add("Sly", "Squat", -1, "p", squatter, email: "ann.new@example.com");
        rig.F.Services.GetRequiredService<PendingSignUps>().Add(squatter);
        Assert.Equal(EmailLinkResult.Done, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
    }

    [Fact]
    public async Task Suspended_account_does_block()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        rig.F.Robust.Add("Sus", "Pended", -1, "p", email: "ann.new@example.com");
        Assert.Equal(EmailLinkResult.Taken, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
    }

    [Fact]
    public async Task Write_failure_keeps_link()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        rig.F.Robust.EmailsDown = true;
        Assert.Equal(EmailLinkResult.Busy, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        rig.F.Robust.EmailsDown = false;
        Assert.Equal(EmailLinkResult.Done, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
    }

    [Fact]
    public async Task Nothing_secret_audited()
    {
        await using var rig = Factory();
        await ChangeAsync(rig, current: "wrong-guess-1");
        await ChangeAsync(rig);
        var token = await AskedTokenAsync(rig, current: New);
        await rig.Changes.ConfirmEmailChangeAsync(token, Ct);
        var text = AuditText(rig.F);
        Assert.NotEmpty(text);
        foreach (var secret in new[] { Old, New, "wrong-guess-1", token, "ann.new", "ann@example.com" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Review fixes: a waiting change can't outlive the owner taking the account back, and a link confirms only its own address ----

    [Fact]
    public async Task Password_change_cancels_waiting_email_change()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        Assert.Equal(AccountResult.Done, (await ChangeAsync(rig)).Result);
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
    }

    [Fact]
    public async Task Password_reset_cancels_waiting_email_change()
    {
        await using var rig = Factory();
        var token = await AskedTokenAsync(rig);
        var reset = rig.F.Services.GetRequiredService<Core.Guests.EmailTokens>().Issue(PasswordResets.Purpose, AnnId, PasswordResets.LinkLifetime);
        var (result, _, _) = await rig.F.Services.GetRequiredService<PasswordResets>().ResetAsync(reset, null, New, New, Caller, Ct);
        Assert.Equal(ResetResult.Done, result);
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(token, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
    }

    [Fact]
    public async Task Link_confirms_only_the_address_it_was_sent_for()
    {
        await using var rig = Factory();
        var tokenA = await AskedTokenAsync(rig, "first@example.com");
        // A second ask caught half-way: its address is waiting, but link A is still the live token.
        rig.F.Services.GetRequiredService<PendingEmailChanges>().Set(AnnId, "second@example.com", "not-link-a");
        Assert.Equal(EmailLinkResult.Expired, (await rig.Changes.ConfirmEmailChangeAsync(tokenA, Ct)).Result);
        Assert.Equal("ann@example.com", rig.Ann.Email);
    }
}
