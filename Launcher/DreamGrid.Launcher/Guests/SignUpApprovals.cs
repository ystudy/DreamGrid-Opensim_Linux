using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// The one lock a sign-up's level change takes, shared by <see cref="SignUps.ConfirmAsync"/> and <see cref="SignUpApprovals"/>: a link
/// click and a staff approval landing together raise the level once, and the loser sees the winner's state.
/// </summary>
public sealed class SignUpLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>How an approve or deny went.</summary>
public enum ApprovalResult { Done, AlreadyDone, NotAllowed, NotFound, Busy }

/// <summary>An approve or deny's result; <c>Message</c> is the sentence to show staff, when there is one.</summary>
public sealed record ApprovalOutcome(ApprovalResult Result, string? Message = null);

/// <summary>
/// Staff approve or deny a waiting sign-up (spec 2026-11-01-sign-up-approval-design §5). Who may is <see cref="ApprovalRules.CanApprove"/>,
/// checked on every call. Only a shown, waiting entry (<see cref="SignUpBoard"/>) whose account is still at −1 is ever touched: an
/// unlisted one (a decoy) is "not found", a finished one "already done". Approve raises the level to 0 and, with email set up, sends
/// <see cref="EmailTemplates.AccountApproved"/>. Deny deletes the account (its name is free again) and sends
/// <see cref="EmailTemplates.SignUpDenied"/> with the staff member's optional note, cut to <see cref="NoteLimit"/> characters. A
/// failed level change or delete leaves the entry waiting and sends nothing. Both are audited (<c>signup-approved</c>,
/// <c>signup-denied</c>); the note never is.
/// </summary>
public sealed class SignUpApprovals(Func<LauncherSettings> settings, IGridController grid, IRobustClient robust, IAccountRemover remover,
    SignUpBoard board, SignUpLock gate, EmailSender sender, AuditLog audit, ILogger<SignUpApprovals> log)
{
    public const int NoteLimit = 500;
    public const string ApproveBusy = "Couldn't approve right now. Try again in a few minutes.";
    public const string DenyBusy = "Couldn't deny right now. Try again in a few minutes.";

    public Task<ApprovalOutcome> ApproveAsync(Guid principal, string staffName, StaffRole role, IPAddress? ip, CancellationToken ct) =>
        ActAsync(principal, staffName, role, ip, ApproveBusy, ct, async account =>
        {
            try
            {
                await grid.SetUserLevelAsync(account.FirstName, account.LastName, 0, CancellationToken.None);
            }
            catch (Exception e) when (IsDown(e) || e is LevelNotConfirmedException or UnauthorizedAccessException)
            {
                log.LogWarning("Sign-up approval: the level of {Principal} couldn't be raised: {Message}", principal, e.InnerException?.Message ?? e.Message);
                return new(ApprovalResult.Busy, ApproveBusy);
            }
            board.TryFinish(principal, SignUpState.Approved, staffName);
            var s = settings();
            if (sender.IsReady && account.Email.Trim().Length > 0)
            {
                var full = Full(account);
                sender.Queue(EmailTemplates.AccountApproved, SignUpRules.NormalEmail(account.Email), full,
                    new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = $"{Site(s)}/guest/sign-in" });
            }
            audit.Write(new AuditEntry(staffName, null, role.AuditName(), ip?.ToString(), "signup-approved", Full(account), "ok"));
            return new(ApprovalResult.Done);
        });

    public Task<ApprovalOutcome> DenyAsync(Guid principal, string staffName, StaffRole role, string? note, IPAddress? ip, CancellationToken ct) =>
        ActAsync(principal, staffName, role, ip, DenyBusy, ct, async account =>
        {
            try
            {
                await remover.DeleteAsync(principal, CancellationToken.None);
            }
            catch (Exception e) when (e is StaffDirectoryUnavailableException or IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Sign-up approval: {Principal} couldn't be deleted: {Message}", principal, e.InnerException?.Message ?? e.Message);
                return new(ApprovalResult.Busy, DenyBusy);
            }
            board.TryFinish(principal, SignUpState.Denied, staffName);
            if (sender.IsReady && account.Email.Trim().Length > 0)
            {
                var words = (note ?? "").Trim();
                if (words.Length > NoteLimit) words = words[..NoteLimit];
                var full = Full(account);
                sender.Queue(EmailTemplates.SignUpDenied, SignUpRules.NormalEmail(account.Email), full,
                    new Dictionary<string, string> { ["AvatarName"] = full, ["Note"] = words });
            }
            audit.Write(new AuditEntry(staffName, null, role.AuditName(), ip?.ToString(), "signup-denied", Full(account), "ok"));
            return new(ApprovalResult.Done);
        });

    private async Task<ApprovalOutcome> ActAsync(Guid principal, string staffName, StaffRole role, IPAddress? ip, string busy, CancellationToken ct,
        Func<RobustAccount, Task<ApprovalOutcome>> act)
    {
        if (!ApprovalRules.CanApprove(role, settings()))
        {
            audit.Write(new AuditEntry(staffName, null, role.AuditName(), ip?.ToString(), "signup-approval-refused", null, "refused"));
            return new(ApprovalResult.NotAllowed);
        }
        await gate.Gate.WaitAsync(ct);
        try
        {
            if (board.Get(principal) is not { } entry || entry.State == SignUpState.Unlisted) return new(ApprovalResult.NotFound);
            if (!entry.Waiting) return new(ApprovalResult.AlreadyDone, entry.By is { } by ? $"Already done by {by}." : "Already done.");
            RobustAccount? account;
            try
            {
                account = await robust.FindAccountByIdAsync(principal, ct);
            }
            catch (Exception e) when (IsDown(e) && !ct.IsCancellationRequested)
            {
                log.LogWarning("Sign-up approval: the account service did not answer: {Message}", e.Message);
                return new(ApprovalResult.Busy, busy);
            }
            if (account is not { UserLevel: -1 })
            {
                // Gone, or changed outside this flow: it isn't a waiting sign-up any more.
                board.Remove(principal);
                return new(ApprovalResult.NotFound);
            }
            return await act(account);
        }
        finally { gate.Gate.Release(); }
    }

    private static bool IsDown(Exception e) =>
        e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException
            or StaffDirectoryUnavailableException;

    private static string Site(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";

    private static string Full(RobustAccount a) => $"{a.FirstName} {a.LastName}";
}
