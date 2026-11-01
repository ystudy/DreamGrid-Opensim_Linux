using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Tells approvers by email that sign-ups are waiting (spec 2026-11-01-sign-up-approval-design §8), from each clean-up pass. It sends
/// only with email set up, at most once every <see cref="MinGap"/>, and only when a shown sign-up started waiting since the last
/// email. Each staff member for whom <see cref="ApprovalRules.CanApprove"/> holds, with an address and not opted out
/// (<see cref="ApprovalNotices"/>), gets <see cref="EmailTemplates.SignUpsWaiting"/>: the count and names of everyone waiting, never
/// an applicant's address, linking the owner and admins to the dashboard and supervisors to the sign-ups list. A database that
/// doesn't answer is tried again on the next pass.
/// </summary>
public sealed class SignUpNotices(Func<LauncherSettings> settings, SignUpBoard board, ApprovalNotices notices, IStaffDirectory directory,
    IRobustClient robust, EmailSender sender, TimeProvider time, ILogger<SignUpNotices> log)
{
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(1);
    public const string ListPath = "/people/sign-ups";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!sender.IsReady) return;
        if (notices.LastSent is { } last && time.GetUtcNow() - last < MinGap) return;
        var waiting = board.Shown().Where(e => e.Waiting).OrderBy(e => e.Made).ToList();
        if (waiting.Count == 0 || waiting[^1].Made <= notices.NewestNoticed) return;

        var s = settings();
        List<RobustAccount> staff;
        try
        {
            staff = [.. await directory.ListAtOrAboveAsync(s.Security.SupervisorLevel, ct)];
            // The owner may sit below the staff levels; the owner can always approve.
            if (s.Setup.Owner is { } o && staff.All(a => a.PrincipalId != o.PrincipalId)
                && await robust.FindAccountByIdAsync(o.PrincipalId, ct) is { } owner)
                staff.Add(owner);
        }
        catch (Exception e) when (e is StaffDirectoryUnavailableException or HttpRequestException or TaskCanceledException or IOException
            or InvalidOperationException or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Sign-ups waiting: the staff list couldn't be read, trying next pass: {Message}", e.InnerException?.Message ?? e.Message);
            return;
        }

        var site = $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";
        var names = string.Join("\n", waiting.Select(e => e.Name.Length > 0 ? e.Name : "(name not known yet)"));
        foreach (var account in staff)
        {
            var role = AdminRule.RoleOf(s, account);
            if (!ApprovalRules.CanApprove(role, s) || account.Email.Trim().Length == 0 || notices.OptedOut(account.PrincipalId)) continue;
            var link = site + (role == StaffRole.Supervisor ? ListPath : "/");
            sender.Queue(EmailTemplates.SignUpsWaiting, account.Email.Trim(), $"{account.FirstName} {account.LastName}",
                new Dictionary<string, string> { ["Count"] = waiting.Count.ToString(), ["Names"] = names, ["Link"] = link });
        }
        notices.Sent(waiting[^1].Made);
    }
}
