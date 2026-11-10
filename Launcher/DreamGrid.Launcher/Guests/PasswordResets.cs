using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Guests;

/// <summary>What the Forgot password page shows after a request.</summary>
public enum ResetAsk { Sent, GridOff }

/// <summary>
/// A live reset link: whose it is, whether the owner's rule wants their two-factor code (only when they have it on), and whether
/// staff sent it from the People page (<see cref="PasswordResets.SendByStaff"/>).
/// </summary>
public sealed record ResetLink(Guid Principal, RobustAccount Account, StaffRole Role, bool NeedsCode, bool ByStaff);

/// <summary>What sending a staff reset link came to.</summary>
public enum StaffResetResult { Sent, NoEmail, Limited, EmailOff }

/// <summary>How a reset went.</summary>
public enum ResetResult { Done, Expired, WrongCode, Locked, BadPassword, WriteFailed, CodeUnknown }

/// <summary>
/// Password recovery for everyone (residents, staff and the owner), behind the two guest-side pages. The owner's rule that
/// applies is the one for the account found (<see cref="Recovery.RuleFor"/>), never the page asked on. A request always gets
/// the same reply, whether or not the name and email match, the account's rule is off or a limit was reached
/// (<see cref="ResetLimits"/>); the account is looked up on every request that gets that far and everything after the lookup runs
/// after the reply (<see cref="IResetWork"/>), so the time taken gives nothing away either. A match gets a single-use link for <see cref="LinkLifetime"/> (<see cref="EmailTokens"/>, purpose
/// <see cref="Purpose"/>), built from the grid's own address in the settings, never the request's Host header. The audit log
/// gets the outcome, never the typed email or the token.
/// <para>The link: <see cref="OpenAsync"/> reads whose it is without spending it (<see cref="EmailTokens.Find"/>), and refuses it
/// when the account is gone, suspended or its rule is now off. <see cref="ResetAsync"/> (one at a time) checks, in order, the
/// password rules and the two-factor code (when the rule asks and the person has it on: staff and owner from the staff file,
/// residents from theirs; a wrong one counts in the sign-in throttle at that person's door), then writes the password
/// (<see cref="IRobustPasswords"/>). Only a written password spends the link and the code, so a failed write or a weak password
/// costs nothing, and of two tabs only one changes anything. Once it is written, every session the person had ends (staff and
/// guest), the change is audited and they get a short "password changed" email, even if the link ran out during the write.</para>
/// </summary>
public sealed class PasswordResets(Func<LauncherSettings> settings, IRobustClient robust, ResetLimits limits, EmailTokens tokens,
    EmailSender sender, AuditLog audit, IRobustPasswords passwords, TwoFactorStore staffTwoFactor, ResidentTwoFactor residents,
    LoginThrottle throttle, StaffSessions sessions, GuestStamps guestStamps, IResetWork work, PendingEmailChanges emailChanges,
    EmailLanguage emailLanguage, ILogger<PasswordResets> log)
{
    public const string Purpose = "reset-password";

    /// <summary>
    /// A link staff sent (owner, 2026-11-03): it works whatever the owner's recovery rule, asks no code, and turns two-factor off
    /// once the new password is written, so a hijacked account can be given back.
    /// </summary>
    public const string StaffPurpose = "staff-reset-password";
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(30);
    /// <summary>The <c>EmailText</c> key of "30 minutes", read in the email's language by <see cref="EmailTemplates.Words(string, string)"/>.</summary>
    public const string ExpiresIn = "ExpiresIn.30m";
    public const string ResetPath = "/guest/reset-password";
    public const string AskPath = "/guest/forgot-password";

    /// <summary>Whether anyone can reset a password: some rule is on and the grid can send email.</summary>
    public bool Available() => Recovery.AnyOn(settings()) && sender.IsReady;

    /// <summary>Asks for a reset link; the email goes out in <paramref name="language"/>, the language of the page asked on.</summary>
    public async Task<ResetAsk> AskAsync(string? name, string? email, IPAddress? ip, CancellationToken ct, string language = Languages.English)
    {
        if (!Available()) return ResetAsk.GridOff;
        var typed = AvatarName.Parse(name);
        if (!limits.AddressAllowed(ip))
        {
            Refused(typed, ip, "address limit");
            return ResetAsk.Sent;
        }
        limits.CountAddress(ip);
        if (!typed.IsComplete)
        {
            Refused(typed, ip, null);
            return ResetAsk.Sent;
        }

        RobustAccount? account;
        try
        {
            account = await robust.FindAccountAsync(typed.First, typed.Last, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Password reset: the account service did not answer: {Message}", e.Message);
            Refused(typed, ip, "account service down");
            return ResetAsk.Sent;
        }

        // Whether it matches, and the link and email if it does, are worked out after the reply, so a match takes no longer to
        // answer than a miss (spec §5.2). The work runs one item at a time, so the limits' check and count can't interleave.
        var typedEmail = (email ?? "").Trim();
        work.Post(() => DecideAsync(typed, typedEmail, account, ip, language));
        return ResetAsk.Sent;
    }

    private Task DecideAsync(AvatarName typed, string typedEmail, RobustAccount? account, IPAddress? ip, string language)
    {
        var s = settings();
        if (account is null || account.UserLevel < 0 || typedEmail.Length == 0
            || !string.Equals(account.Email.Trim(), typedEmail, StringComparison.OrdinalIgnoreCase)
            || Recovery.RuleFor(s, AdminRule.RoleOf(s, account)) == RecoveryRule.Off)
        {
            Refused(typed, ip, null);
            return Task.CompletedTask;
        }
        if (!limits.SendAllowed(account.PrincipalId, typedEmail))
        {
            Refused(typed, ip, "account limit");
            return Task.CompletedTask;
        }

        limits.CountSend(account.PrincipalId, typedEmail);
        var token = tokens.Issue(Purpose, account.PrincipalId, LinkLifetime);
        var link = $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}{ResetPath}?token={token}";
        var full = $"{account.FirstName} {account.LastName}";
        var queued = sender.Queue(EmailTemplates.ResetPassword, language, account.Email.Trim(), full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = link, ["ExpiresIn"] = EmailTemplates.Words(ExpiresIn, language) }, LinkLifetime);
        if (queued != QueueResult.Queued)
            log.LogWarning("Password reset: the email for first name {First} couldn't be queued: {Result}", typed.ForLog, queued);
        audit.Write(new AuditEntry(full, account.PrincipalId, AdminRule.RoleOf(s, account).AuditName(), ip?.ToString(),
            "reset-requested", null, queued == QueueResult.Queued ? "ok" : "failed"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends <paramref name="account"/> a <see cref="StaffPurpose"/> link to its own address, replacing any earlier one. It keeps
    /// the per-account limit (so busy staff can't flood an inbox) but not the per-address one: the request comes from staff, not
    /// the person. The caller checks who may send it and writes the audit.
    /// </summary>
    public StaffResetResult SendByStaff(RobustAccount account)
    {
        if (!sender.IsReady) return StaffResetResult.EmailOff;
        var to = account.Email.Trim();
        if (to.Length == 0) return StaffResetResult.NoEmail;
        if (!limits.SendAllowed(account.PrincipalId, to)) return StaffResetResult.Limited;
        limits.CountSend(account.PrincipalId, to);
        var token = tokens.Issue(StaffPurpose, account.PrincipalId, LinkLifetime);
        var s = settings();
        var link = $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}{ResetPath}?token={token}";
        var full = $"{account.FirstName} {account.LastName}";
        var language = emailLanguage.ForResident(account.PrincipalId);
        var queued = sender.Queue(EmailTemplates.StaffResetPassword, language, to, full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = link, ["ExpiresIn"] = EmailTemplates.Words(ExpiresIn, language) }, LinkLifetime);
        if (queued == QueueResult.Queued) return StaffResetResult.Sent;
        tokens.Revoke(StaffPurpose, account.PrincipalId);
        return StaffResetResult.EmailOff;
    }

    /// <summary>The live link behind <paramref name="token"/>, or null (used, expired, unknown, the account gone or its rule off).</summary>
    public async Task<ResetLink?> OpenAsync(string? token, CancellationToken ct)
    {
        var byStaff = false;
        if (tokens.Find(Purpose, token) is not { } principal)
        {
            if (tokens.Find(StaffPurpose, token) is not { } staffSent) return null;
            (principal, byStaff) = (staffSent, true);
        }
        RobustAccount? account;
        try
        {
            account = await robust.FindAccountByIdAsync(principal, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Password reset: the account service did not answer: {Message}", e.Message);
            return null;
        }
        if (account is null || account.UserLevel < 0) return null;
        var s = settings();
        var role = AdminRule.RoleOf(s, account);
        // Staff send these to residents only; one made staff since the link was sent uses Forgot password, with their own rule.
        if (byStaff) return role == StaffRole.None ? new ResetLink(principal, account, role, NeedsCode: false, ByStaff: true) : null;
        var rule = Recovery.RuleFor(s, role);
        if (rule == RecoveryRule.Off) return null;
        var needsCode = rule == RecoveryRule.EmailLinkAndCode && StoreFor(role).StateOf(principal) != TwoFactorState.Off;
        return new ResetLink(principal, account, role, needsCode, ByStaff: false);
    }

    public async Task<(ResetResult Result, ResetLink? Link, PasswordRules.Fault? Problem)> ResetAsync(string? token, string? code, string? password,
        string? again, IPAddress? ip, CancellationToken ct, string language = Languages.English)
    {
        // One reset at a time (they are rare): two tabs posting the same link at once can't both write a password before
        // either spends it; the second finds it spent.
        await _one.WaitAsync(ct);
        try
        {
            return await ResetOneAsync(token, code, password, again, ip, ct, language);
        }
        finally
        {
            _one.Release();
        }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    private async Task<(ResetResult Result, ResetLink? Link, PasswordRules.Fault? Problem)> ResetOneAsync(string? token, string? code, string? password,
        string? again, IPAddress? ip, CancellationToken ct, string language)
    {
        if (await OpenAsync(token, ct) is not { } link) return (ResetResult.Expired, null, null);
        var a = link.Account;
        var full = $"{a.FirstName} {a.LastName}";
        var door = link.Role == StaffRole.None ? SignInDoor.Guest : SignInDoor.Staff;
        var key = AvatarName.Parse(full).Key;
        var store = StoreFor(link.Role);

        // The password first, and the code only checked here: a right code is used up only once the password is written, so a
        // weak password or a failed write never costs the person their code (or a backup code).
        if (PasswordRules.Check(password, again, a.FirstName, a.LastName) is { } problem) return (ResetResult.BadPassword, link, problem);
        if (link.NeedsCode)
        {
            if (throttle.IsLocked(door, key, ip)) return (ResetResult.Locked, link, null);
            if (store.StateOf(link.Principal) == TwoFactorState.Unknown) return (ResetResult.CodeUnknown, link, null);
            if (!store.CheckCode(link.Principal, code ?? ""))
            {
                throttle.RecordFailure(door, key, ip);
                audit.Write(new AuditEntry(full, link.Principal, link.Role.AuditName(), ip?.ToString(), "reset-code-wrong", null, "refused"));
                return (ResetResult.WrongCode, link, null);
            }
        }

        try
        {
            await passwords.SetAsync(link.Principal, password!, ct);
        }
        catch (RobustPasswordsUnavailableException e)
        {
            log.LogWarning("Password reset: the password for first name {First} couldn't be written: {Message}",
                LogText.Clean(a.FirstName, 32), e.InnerException?.Message ?? e.Message);
            return (ResetResult.WriteFailed, link, null);
        }

        // The password is changed now, so everything below happens whatever else does: the link is spent (it may have run out,
        // or been replaced by a newer one, while the write took its time), the code is used up, sessions end, the change is
        // audited and the person told.
        tokens.TryRedeem(link.ByStaff ? StaffPurpose : Purpose, link.Principal, token);
        string? cleared = null;
        if (link.ByStaff && store.StateOf(link.Principal) != TwoFactorState.Off)
        {
            try
            {
                store.Disable(link.Principal);
                cleared = "two-factor cleared";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogWarning("Password reset: two-factor for first name {First} couldn't be turned off: {Message}",
                    LogText.Clean(a.FirstName, 32), e.Message);
                cleared = "two-factor not cleared";
            }
        }
        if (link.NeedsCode)
        {
            try
            {
                store.VerifyCode(link.Principal, code ?? "", out _);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogWarning("Password reset: a two-factor code for first name {First} couldn't be marked used: {Message}",
                    LogText.Clean(a.FirstName, 32), e.Message);
            }
            throttle.RecordSuccess(door, key);
        }
        AccountChanges.CancelEmailChange(tokens, emailChanges, link.Principal);
        sessions.EndAll(link.Principal);
        guestStamps.Rotate(link.Principal);
        if (a.Email.Trim() is { Length: > 0 } to)
            sender.Queue(EmailTemplates.PasswordChanged, language, to, full, new Dictionary<string, string> { ["AvatarName"] = full });
        audit.Write(new AuditEntry(full, link.Principal, link.Role.AuditName(), ip?.ToString(), "password-reset", cleared, "ok"));
        return (ResetResult.Done, link, null);
    }

    private TwoFactorStore StoreFor(StaffRole role) => role == StaffRole.None ? residents.Store : staffTwoFactor;

    private void Refused(AvatarName typed, IPAddress? ip, string? why) =>
        // Only the cleaned first name: what was typed could be anything, and the email is never written.
        audit.Write(new AuditEntry(typed.ForLog, null, StaffRole.None.AuditName(), ip?.ToString(), "reset-refused", why, "refused"));
}
