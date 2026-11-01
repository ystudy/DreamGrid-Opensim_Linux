using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Guests;

/// <summary>Email change's own request counts (a third <see cref="ResetLimits"/> on its own file, so they never mix with recovery's or sign-up's).</summary>
public sealed record EmailChangeLimits(ResetLimits Counts);

public enum AccountResult { Done, WrongPassword, WrongCode, Locked, Invalid, Unchanged, NoEmail, Busy }

public sealed record AccountOutcome(AccountResult Result, string? Problem = null);

public enum EmailLinkResult { Done, Expired, Taken, Busy }

/// <summary>
/// A signed-in person changing their own password or email (spec 2026-10-31-resident-account-design §5, §6), for residents and for
/// staff at the guest door. Both ask for the current password, and a code when the person has resident two-factor on (staff
/// two-factor is never asked here, as the guest door never asks it); wrong ones count in <see cref="LoginThrottle"/> at the guest
/// door, as sign-in does, and while it is locked nothing is checked. A code is only checked until the change is written, then
/// spent. A changed password ends every other session (a new guest stamp; for staff, every staff session too) and the person is
/// told by email. A new email address waits in <see cref="PendingEmailChanges"/> until the link sent to it is opened; the old
/// address is told at once, without naming the new one. The audit log gets every outcome, never a password, code, token or address.
/// </summary>
public sealed class AccountChanges(Func<LauncherSettings> settings, IRobustClient robust, IStaffDirectory directory,
    IRobustPasswords passwords, IRobustEmails emails, LoginThrottle throttle, ResidentTwoFactor residents, StaffSessions sessions,
    GuestStamps stamps, EmailSender sender, EmailTokens tokens, PendingEmailChanges waiting, PendingSignUps pendingSignUps,
    EmailChangeLimits limits, AuditLog audit, ILogger<AccountChanges> log)
{
    public const string Purpose = "change-email";
    public static readonly TimeSpan LinkLifetime = PendingEmailChanges.KeepFor;
    public const string ExpiresIn = "24 hours";
    public const string ConfirmPath = "/guest/confirm-email";

    public const string WrongCurrent = "That isn't your current password.";
    public const string LockedText = "Too many attempts. Try again in a few minutes.";
    public const string SamePassword = "Choose a password you haven't been using.";
    public const string PasswordBusy = "Couldn't change the password right now. Try again in a few minutes.";
    public const string PasswordDone = "Your password is changed.";
    public const string NoEmailText = "Changing your email needs the grid's email to be set up. Ask the grid owner.";
    public const string SameAddress = "That's already your email address.";
    public const string AskReply = "We sent a link to the new address. Your email stays the same until you open it. It works once, for 24 hours.";
    public const string LinkExpired = "This link has expired or was already used.";
    public const string AddressTaken = "That address is used by another avatar on this grid.";
    public const string EmailBusy = "Couldn't change your email right now. Try again in a few minutes.";
    public const string ServiceDown = "The grid's sign-in service isn't running right now. Try again later.";

    private ResetLimits Counts => limits.Counts;
    private TwoFactorStore Store => residents.Store;

    // One email link at a time (they are rare): two tabs opening the same link can't both write before either spends it.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Whether the person's changes ask for a code: they have resident two-factor on.</summary>
    public bool NeedsCode(Guid principal) => Store.StateOf(principal) == TwoFactorState.On;

    /// <summary>Whether the email can be changed: the grid can send the link.</summary>
    public bool EmailChangeAvailable() => sender.IsReady;

    public async Task<AccountOutcome> ChangePasswordAsync(Guid principal, string? current, string? code, string? password, string? again,
        IPAddress? ip, CancellationToken ct)
    {
        var (refused, account) = await CheckAsync(principal, current, code, ip, "password-change-refused", ct);
        if (refused is not null) return refused;
        var a = account!;
        if (PasswordRules.Problem(password, again, a.FirstName, a.LastName) is { } problem)
            return Refused(a, ip, "password-change-refused", "weak password", new(AccountResult.Invalid, problem));
        if (password == current) return Refused(a, ip, "password-change-refused", "same password", new(AccountResult.Unchanged, SamePassword));

        try
        {
            await passwords.SetAsync(principal, password!, ct);
        }
        catch (RobustPasswordsUnavailableException e)
        {
            log.LogWarning("Account: the password for first name {First} couldn't be written: {Message}",
                LogText.Clean(a.FirstName, 32), e.InnerException?.Message ?? e.Message);
            return new(AccountResult.Busy, PasswordBusy);
        }

        // Written: everything below happens whatever else does.
        Passed(a, code);
        CancelEmailChange(tokens, waiting, principal);
        stamps.Rotate(principal);
        if (AdminRule.RoleOf(settings(), a) != StaffRole.None) sessions.EndAll(principal);
        if (sender.IsReady && a.Email.Trim() is { Length: > 0 } to)
            sender.Queue(EmailTemplates.PasswordChanged, to, Full(a), new Dictionary<string, string> { ["AvatarName"] = Full(a) });
        Write(a, ip, "password-changed", null, "ok");
        return new(AccountResult.Done);
    }

    public async Task<AccountOutcome> AskEmailChangeAsync(Guid principal, string? current, string? code, string? newEmail, IPAddress? ip,
        CancellationToken ct)
    {
        if (!sender.IsReady) return new(AccountResult.NoEmail, NoEmailText);
        var (refused, account) = await CheckAsync(principal, current, code, ip, "email-change-refused", ct);
        if (refused is not null) return refused;
        var a = account!;
        if (SignUpRules.EmailProblem(newEmail) is { } problem)
            return Refused(a, ip, "email-change-refused", "bad address", new(AccountResult.Invalid, problem));
        var email = SignUpRules.NormalEmail(newEmail!);
        if (email == SignUpRules.NormalEmail(a.Email))
            return Refused(a, ip, "email-change-refused", "same address", new(AccountResult.Unchanged, SameAddress));

        // The password and code were right, so they are done with (a backup code is spent) whether or not a link goes out.
        Passed(a, code);
        // From here the reply is the same whatever happens, so the limits can't be read from it.
        if (!Counts.AddressAllowed(ip) || !Counts.SendAllowed(principal, email))
        {
            Write(a, ip, "email-change-refused", "limit", "refused");
            return new(AccountResult.Done);
        }
        Counts.CountAddress(ip);
        Counts.CountSend(principal, email);

        var token = tokens.Issue(Purpose, principal, LinkLifetime);
        waiting.Set(principal, email, token);
        var s = settings();
        var full = Full(a);
        var queued = sender.Queue(EmailTemplates.ConfirmNewEmail, email, full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = $"{Site(s)}{ConfirmPath}?token={token}", ["ExpiresIn"] = ExpiresIn },
            LinkLifetime);
        if (queued != QueueResult.Queued)
            log.LogWarning("Account: the new-address email for first name {First} couldn't be queued: {Result}", LogText.Clean(a.FirstName, 32), queued);
        if (a.Email.Trim() is { Length: > 0 } old)
            sender.Queue(EmailTemplates.EmailChangeNotice, old, full,
                new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = $"{Site(s)}{PasswordResets.AskPath}" });
        Write(a, ip, "email-change-asked", null, "ok");
        return new(AccountResult.Done);
    }

    public async Task<(EmailLinkResult Result, string? Email)> ConfirmEmailChangeAsync(string? token, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            return await ConfirmOneAsync(token, ct);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<(EmailLinkResult Result, string? Email)> ConfirmOneAsync(string? token, CancellationToken ct)
    {
        if (tokens.Find(Purpose, token) is not { } principal) return (EmailLinkResult.Expired, null);
        if (waiting.Get(principal, token, LinkLifetime) is not { } email) return (EmailLinkResult.Expired, null);

        RobustAccount? account;
        IReadOnlyList<RobustAccount> holders;
        try
        {
            account = await robust.FindAccountByIdAsync(principal, ct);
            holders = await directory.ListByEmailAsync(email, ct);
        }
        catch (Exception e) when (Unreachable(e, ct) || e is StaffDirectoryUnavailableException)
        {
            log.LogWarning("Account: an email change couldn't be checked: {Message}", e.Message);
            return (EmailLinkResult.Busy, null);
        }
        if (account is null)
        {
            tokens.TryRedeem(Purpose, principal, token);
            waiting.Remove(principal);
            return (EmailLinkResult.Expired, null);
        }

        // Sign-ups nobody confirmed don't count: otherwise anyone could hold a victim's address with a sign-up they never finish.
        if (holders.Any(h => h.PrincipalId != principal && !pendingSignUps.Contains(h.PrincipalId)))
        {
            tokens.TryRedeem(Purpose, principal, token);
            waiting.Remove(principal);
            Write(account, null, "email-change-refused", "address taken", "refused");
            return (EmailLinkResult.Taken, null);
        }

        try
        {
            await emails.SetAsync(principal, email, ct);
        }
        catch (RobustEmailsUnavailableException e)
        {
            log.LogWarning("Account: the email for first name {First} couldn't be written: {Message}",
                LogText.Clean(account.FirstName, 32), e.InnerException?.Message ?? e.Message);
            return (EmailLinkResult.Busy, null);
        }
        tokens.TryRedeem(Purpose, principal, token);
        waiting.Remove(principal);
        Write(account, null, "email-changed", null, "ok");
        return (EmailLinkResult.Done, email);
    }

    /// <summary>
    /// Ends a waiting email change, so the old address's "change your password now" really stops it: whoever asked for it with a
    /// stolen session can't still open the link after the owner took the account back. Called once a password is written, here and
    /// by password recovery.
    /// </summary>
    public static void CancelEmailChange(EmailTokens tokens, PendingEmailChanges waiting, Guid principal)
    {
        tokens.Revoke(Purpose, principal);
        waiting.Remove(principal);
    }

    /// <summary>
    /// The checks both changes start with: the throttle, the current password, and the code when two-factor is on. A refusal, or
    /// null and the account when they all passed. The code is only checked here; <see cref="Passed"/> spends it.
    /// </summary>
    private async Task<(AccountOutcome? Refused, RobustAccount? Account)> CheckAsync(Guid principal, string? current, string? code,
        IPAddress? ip, string refusedAction, CancellationToken ct)
    {
        RobustAccount? a;
        bool right;
        try
        {
            a = await robust.FindAccountByIdAsync(principal, ct);
            if (a is null) return (new(AccountResult.Busy, ServiceDown), null);
            if (throttle.IsLocked(SignInDoor.Guest, Key(a), ip)) return (new(AccountResult.Locked, LockedText), null);
            right = await robust.AuthenticateAsync(principal, current ?? "", ct);
        }
        catch (Exception e) when (Unreachable(e, ct))
        {
            log.LogWarning("Account: the account service did not answer: {Message}", e.Message);
            return (new(AccountResult.Busy, ServiceDown), null);
        }
        if (!right)
        {
            throttle.RecordFailure(SignInDoor.Guest, Key(a), ip);
            return (Refused(a, ip, refusedAction, "wrong password", new(AccountResult.WrongPassword, WrongCurrent)), null);
        }
        if (NeedsCode(principal) && !Store.CheckCode(principal, code ?? ""))
        {
            throttle.RecordFailure(SignInDoor.Guest, Key(a), ip);
            return (Refused(a, ip, refusedAction, "two-factor code", new(AccountResult.WrongCode, Pages.Guest.TwoFactorModel.WrongCode)), null);
        }
        return (null, a);
    }

    /// <summary>The checks passed and the change goes ahead: the code is spent and the name's count cleared.</summary>
    private void Passed(RobustAccount a, string? code)
    {
        if (NeedsCode(a.PrincipalId))
        {
            try
            {
                Store.VerifyCode(a.PrincipalId, code ?? "", out _);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogWarning("Account: a two-factor code for first name {First} couldn't be marked used: {Message}",
                    LogText.Clean(a.FirstName, 32), e.Message);
            }
        }
        throttle.RecordSuccess(SignInDoor.Guest, Key(a));
    }

    private static bool Unreachable(Exception e, CancellationToken ct) =>
        e is HttpRequestException or IOException or InvalidOperationException or InvalidDataException
        || e is TaskCanceledException && !ct.IsCancellationRequested;

    private AccountOutcome Refused(RobustAccount a, IPAddress? ip, string action, string why, AccountOutcome outcome)
    {
        Write(a, ip, action, why, "refused");
        return outcome;
    }

    private void Write(RobustAccount a, IPAddress? ip, string action, string? target, string result) =>
        audit.Write(new AuditEntry(Full(a), a.PrincipalId, AdminRule.RoleOf(settings(), a).AuditName(), ip?.ToString(), action, target, result));

    private static string Key(RobustAccount a) => AvatarName.Parse(Full(a)).Key;

    private static string Full(RobustAccount a) => $"{a.FirstName} {a.LastName}";

    private static string Site(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";
}
