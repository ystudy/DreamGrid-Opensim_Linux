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

namespace DreamGrid.Launcher.Guests;

/// <summary>What the Forgot password page shows after a request.</summary>
public enum ResetAsk { Sent, GridOff }

/// <summary>
/// Password recovery for everyone (residents, staff and the owner), behind the two guest-side pages. The owner's rule that
/// applies is the one for the account found (<see cref="Recovery.RuleFor"/>), never the page asked on. A request always gets
/// the same reply, whether or not the name and email match, the account's rule is off or a limit was reached
/// (<see cref="ResetLimits"/>); the account is looked up on every request that gets that far, so the time taken gives nothing
/// away either. A match gets a single-use link for <see cref="LinkLifetime"/> (<see cref="EmailTokens"/>, purpose
/// <see cref="Purpose"/>), built from the grid's own address in the settings, never the request's Host header. The audit log
/// gets the outcome, never the typed email or the token.
/// </summary>
public sealed class PasswordResets(Func<LauncherSettings> settings, IRobustClient robust, ResetLimits limits, EmailTokens tokens,
    EmailSender sender, AuditLog audit, ILogger<PasswordResets> log)
{
    public const string Purpose = "reset-password";
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(30);
    public const string ExpiresIn = "30 minutes";
    public const string ResetPath = "/guest/reset-password";

    /// <summary>Whether anyone can reset a password: some rule is on and the grid can send email.</summary>
    public bool Available() => Recovery.AnyOn(settings()) && sender.IsReady;

    public async Task<ResetAsk> AskAsync(string? name, string? email, IPAddress? ip, CancellationToken ct)
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

        var s = settings();
        var typedEmail = (email ?? "").Trim();
        if (account is null || account.UserLevel < 0 || typedEmail.Length == 0
            || !string.Equals(account.Email.Trim(), typedEmail, StringComparison.OrdinalIgnoreCase)
            || Recovery.RuleFor(s, AdminRule.RoleOf(s, account)) == RecoveryRule.Off)
        {
            Refused(typed, ip, null);
            return ResetAsk.Sent;
        }
        if (!limits.SendAllowed(account.PrincipalId, typedEmail))
        {
            Refused(typed, ip, "account limit");
            return ResetAsk.Sent;
        }

        limits.CountSend(account.PrincipalId, typedEmail);
        var token = tokens.Issue(Purpose, account.PrincipalId, LinkLifetime);
        var link = $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}{ResetPath}?token={token}";
        var full = $"{account.FirstName} {account.LastName}";
        var queued = sender.Queue(EmailTemplates.ResetPassword, account.Email.Trim(), full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = link, ["ExpiresIn"] = ExpiresIn }, LinkLifetime);
        if (queued != QueueResult.Queued)
            log.LogWarning("Password reset: the email for first name {First} couldn't be queued: {Result}", typed.ForLog, queued);
        audit.Write(new AuditEntry(full, account.PrincipalId, AdminRule.RoleOf(s, account).AuditName(), ip?.ToString(),
            "reset-requested", null, queued == QueueResult.Queued ? "ok" : "failed"));
        return ResetAsk.Sent;
    }

    private void Refused(AvatarName typed, IPAddress? ip, string? why) =>
        // Only the cleaned first name: what was typed could be anything, and the email is never written.
        audit.Write(new AuditEntry(typed.ForLog, null, StaffRole.None.AuditName(), ip?.ToString(), "reset-refused", why, "refused"));
}
