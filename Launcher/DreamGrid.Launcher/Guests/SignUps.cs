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

/// <summary>What the sign-up form typed.</summary>
public sealed record SignUpForm(string? First, string? Last, string? Email, string? Password, string? Again, bool AcceptTerms);

/// <summary>How a sign-up post or a "send the link again" went.</summary>
public enum SignUpOutcome { Closed, Invalid, NameTaken, CheckEmail, SignedUp, WaitingForStaff, Busy }

/// <summary>A sign-up's outcome; <c>Problem</c> is the sentence to show, <c>Account</c> the avatar when it can be used at once.</summary>
public sealed record SignUpResult(SignUpOutcome Outcome, string? Problem = null, RobustAccount? Account = null);

/// <summary>How a confirm link went: the account is in now, staff had already let it in, or the link is used, expired or unknown.</summary>
public enum ConfirmResult { Confirmed, AlreadyApproved, Bad }

/// <summary>Robust or the database didn't answer while a link was being confirmed: the link is still good.</summary>
public sealed class SignUpBusyException(string message, Exception inner) : Exception(message, inner);

/// <summary>Sign-up's own request counts (a second <see cref="ResetLimits"/> on its own file, so sign-ups and resets don't share).</summary>
public sealed record SignUpLimits(ResetLimits Counts);

/// <summary>
/// Resident sign-up behind the guest pages (spec 2026-10-31-sign-up-design). <see cref="SubmitAsync"/> checks, in order, that
/// sign-up is open, the address's limit, the name, the password, the email and the Terms tick, and that the name is free (said
/// plainly). The avatar is made at level -1 and put on the <see cref="SignUpBoard"/>
/// (<see cref="IGridController.CreateResidentAsync"/>), its password written through the database (<see cref="IRobustPasswords"/>,
/// so it never goes on the console), and the Terms acceptance recorded. With email it waits there for a single-use link valid
/// <see cref="LinkLifetime"/>; without email it is raised to 0 and taken off the list. With email set up, an address already on the
/// grid is never said: the avatar is made the same way but gets no link, its owner gets a short notice, and the clean-up frees the
/// name. A failure after the avatar was made deletes it again, or leaves it listed for the clean-up when that fails too.
/// <see cref="ConfirmAsync"/> raises a pending avatar to 0 and only then spends the link. The audit log gets every outcome,
/// never the typed email, the password or the token.
/// </summary>
public sealed class SignUps(Func<LauncherSettings> settings, IGridController grid, IRobustClient robust, IStaffDirectory directory,
    IAccountRemover remover, IRobustPasswords passwords, SignUpBoard pending, SignUpLimits limits, EmailTokens tokens,
    EmailSender sender, TermsAcceptances terms, SignUpLock gate, AuditLog audit, ILogger<SignUps> log)
{
    public const string Purpose = "confirm-signup";
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);
    public const string ExpiresIn = "24 hours";
    public const string ConfirmPath = "/guest/confirm";
    public const string NameTaken = "That name is taken. Try another.";
    public const string TermsProblem = "Tick the box to accept the Terms of Service and the Privacy notice.";
    public const string AddressProblem = "Too many sign-ups from your address. Try again in an hour.";

    private ResetLimits Counts => limits.Counts;

    public async Task<SignUpResult> SubmitAsync(SignUpForm form, IPAddress? ip, CancellationToken ct)
    {
        var s = settings();
        if (!SignUpRules.IsOpen(s)) return new(SignUpOutcome.Closed);
        if (!Counts.AddressAllowed(ip))
        {
            Refused(form, ip, "address limit");
            return new(SignUpOutcome.Invalid, AddressProblem);
        }
        Counts.CountAddress(ip);

        var first = form.First?.Trim() ?? "";
        var last = form.Last?.Trim() ?? "";
        var problem = SignUpRules.NameProblem(first, last, s)
            ?? PasswordRules.Problem(form.Password, form.Again, first, last)
            ?? SignUpRules.EmailProblem(form.Email)
            ?? (form.AcceptTerms ? null : TermsProblem);
        if (problem is not null)
            return problem == NameTaken ? Taken(form, ip) : new(SignUpOutcome.Invalid, problem);
        var email = SignUpRules.NormalEmail(form.Email!);
        var withEmail = sender.IsReady;

        RobustAccount? owner;
        try
        {
            // The strict lookup: a reply that isn't an account answer is Robust failing, never "the name is free".
            if (await robust.FindAccountAsync(first, last, ct) is not null) return Taken(form, ip);
            owner = withEmail ? await directory.FindByEmailAsync(email, ct) : null;
        }
        catch (Exception e) when (IsDown(e, ct))
        {
            log.LogWarning("Sign-up: the grid did not answer: {Message}", e.Message);
            return new(SignUpOutcome.Busy);
        }

        // Every sign-up is made at -1 and listed first, so one that fails half-way and can't be deleted is left for the clean-up.
        // From here the request may be given up, but the work runs to the end. An address already on the grid gets the same
        // avatar and reply (so neither the name nor the time taken says whether it is registered), but no link: its owner gets a
        // notice instead, and the clean-up frees the name.
        var none = CancellationToken.None;
        RobustAccount account;
        try
        {
            account = await grid.CreateResidentAsync(first, last, email, -1, none);
        }
        catch (NameTakenException)
        {
            return Taken(form, ip);
        }
        catch (Exception e) when (IsDown(e, none))
        {
            log.LogWarning("Sign-up: the avatar for first name {First} couldn't be made: {Error}", LogText.Clean(first, 32), e.GetType().Name);
            return new(SignUpOutcome.Busy);
        }

        try
        {
            pending.Add(account.PrincipalId, Full(account), SignUpState.Unlisted);
            await passwords.SetAsync(account.PrincipalId, form.Password!, none);
            if (withEmail)
            {
                // A decoy (address already on the grid) stays unlisted: never shown, never approved.
                if (owner is null) pending.Add(account.PrincipalId, Full(account), SignUpState.WaitingForEmail);
            }
            else if (ApprovalRules.WaitsForStaff(s, emailReady: false))
            {
                pending.Add(account.PrincipalId, Full(account), SignUpState.WaitingForStaff);
            }
            else
            {
                account = await grid.SetUserLevelAsync(account.FirstName, account.LastName, 0, none);
                pending.Add(account.PrincipalId, Full(account), SignUpState.LetIn);
            }
        }
        catch (Exception e) when (e is RobustPasswordsUnavailableException or UnauthorizedAccessException or LevelNotConfirmedException
            || IsDown(e, none))
        {
            log.LogWarning("Sign-up: the avatar for first name {First} couldn't be finished, so it is deleted: {Message}",
                LogText.Clean(first, 32), e.InnerException?.Message ?? e.Message);
            await DeleteAsync(account.PrincipalId);
            return new(SignUpOutcome.Busy);
        }

        if (LegalPages.Get(s, LegalPages.Terms) is { } t) terms.Accept(account.PrincipalId, t.Version, ip?.ToString());
        if (owner is not null)
        {
            NoticeOwner(s, owner, email);
            Refused(form, ip, "email taken");
            return new(SignUpOutcome.CheckEmail);
        }
        var waitsForStaff = !withEmail && account.UserLevel < 0;
        audit.Write(new AuditEntry(Full(account), account.PrincipalId, StaffRole.None.AuditName(), ip?.ToString(), "signup-made",
            withEmail ? "waiting for email" : waitsForStaff ? "waiting for staff" : "ready", "ok"));
        if (waitsForStaff) return new(SignUpOutcome.WaitingForStaff);
        if (!withEmail) return new(SignUpOutcome.SignedUp, Account: account);

        SendLink(s, account, email);
        return new(SignUpOutcome.CheckEmail);
    }

    /// <summary>
    /// What a confirm link does. A live link to a waiting (or unlisted) sign-up raises it to level 0, marks it self-approved and
    /// returns the account; a live link to one staff already approved is spent and says so; anything else is
    /// <see cref="ConfirmResult.Bad"/>. Throws <see cref="SignUpBusyException"/> when Robust didn't answer; the link is then still good.
    /// Takes <see cref="SignUpLock"/>, so a staff approval landing at the same moment raises the level only once.
    /// </summary>
    public async Task<(ConfirmResult Result, RobustAccount? Account)> ConfirmAsync(string? token, IPAddress? ip, CancellationToken ct)
    {
        await gate.Gate.WaitAsync(ct);
        try
        {
            if (tokens.Find(Purpose, token) is not { } principal) return (ConfirmResult.Bad, null);
            RobustAccount account;
            try
            {
                if (pending.Get(principal) is not { } entry) return (ConfirmResult.Bad, null);
                if (entry.State == SignUpState.Approved)
                {
                    tokens.TryRedeem(Purpose, principal, token);
                    return (ConfirmResult.AlreadyApproved, null);
                }
                if (!pending.Contains(principal)) return (ConfirmResult.Bad, null);
                if (await robust.FindAccountByIdAsync(principal, ct) is not { } found) return (ConfirmResult.Bad, null);
                account = found.UserLevel == 0 ? found : await grid.SetUserLevelAsync(found.FirstName, found.LastName, 0, ct);
                // Off the waiting list before the link is spent: a confirmed resident left on it could later be deleted as unconfirmed.
                // An unlisted one (carried over from the old list) leaves the board altogether: it was never shown, and isn't now.
                if (entry.State == SignUpState.Unlisted) pending.Remove(principal);
                else pending.TryFinish(principal, SignUpState.SelfApproved, null);
            }
            catch (Exception e) when (IsDown(e, ct) || e is LevelNotConfirmedException or UnauthorizedAccessException)
            {
                throw new SignUpBusyException("The account couldn't be confirmed right now.", e);
            }
            tokens.TryRedeem(Purpose, principal, token);
            audit.Write(new AuditEntry(Full(account), principal, StaffRole.None.AuditName(), ip?.ToString(), "signup-confirmed", null, "ok"));
            return (ConfirmResult.Confirmed, account);
        }
        finally { gate.Gate.Release(); }
    }


    /// <summary>
    /// Sends a new link (replacing the last) to a pending sign-up called <paramref name="first"/> <paramref name="last"/>, within the
    /// limits. Answers <see cref="SignUpOutcome.CheckEmail"/> whatever the name, and <see cref="SignUpOutcome.Busy"/> only when the
    /// grid can't send email.
    /// </summary>
    public async Task<SignUpOutcome> ResendAsync(string? first, string? last, IPAddress? ip, CancellationToken ct)
    {
        if (!sender.IsReady) return SignUpOutcome.Busy;
        if (!Counts.AddressAllowed(ip)) return SignUpOutcome.CheckEmail;
        Counts.CountAddress(ip);
        var typed = AvatarName.Parse($"{first?.Trim()} {last?.Trim()}");
        if (!typed.IsComplete) return SignUpOutcome.CheckEmail;
        RobustAccount? account;
        try
        {
            account = await robust.GetAccountAsync(typed.First, typed.Last, ct);
        }
        catch (Exception e) when (IsDown(e, ct))
        {
            log.LogWarning("Sign-up: the account service did not answer: {Message}", e.Message);
            return SignUpOutcome.CheckEmail;
        }
        // Only a sign-up waiting for its email gets a link: never a decoy, whose address belongs to someone else.
        if (account is not null && pending.Get(account.PrincipalId)?.State == SignUpState.WaitingForEmail && account.Email.Trim().Length > 0)
            SendLink(settings(), account, SignUpRules.NormalEmail(account.Email));
        return SignUpOutcome.CheckEmail;
    }

    private void SendLink(LauncherSettings s, RobustAccount account, string email)
    {
        if (!Counts.SendAllowed(account.PrincipalId, email)) return;
        Counts.CountSend(account.PrincipalId, email);
        var token = tokens.Issue(Purpose, account.PrincipalId, LinkLifetime);
        // The clean-up counts from the newest link, so it never deletes an avatar whose link still works.
        pending.Linked(account.PrincipalId);
        var link = $"{Site(s)}{ConfirmPath}?token={token}";
        var full = Full(account);
        var queued = sender.Queue(EmailTemplates.ConfirmEmail, email, full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = link, ["ExpiresIn"] = ExpiresIn }, LinkLifetime);
        if (queued != QueueResult.Queued)
            log.LogWarning("Sign-up: the confirm email for first name {First} couldn't be queued: {Result}", LogText.Clean(account.FirstName, 32), queued);
    }

    private void NoticeOwner(LauncherSettings s, RobustAccount owner, string email)
    {
        if (!Counts.SendAllowed(owner.PrincipalId, email)) return;
        Counts.CountSend(owner.PrincipalId, email);
        var full = Full(owner);
        sender.Queue(EmailTemplates.AlreadyHaveAccount, email, full,
            new Dictionary<string, string> { ["AvatarName"] = full, ["Link"] = $"{Site(s)}{PasswordResets.AskPath}" });
    }

    private SignUpResult Taken(SignUpForm form, IPAddress? ip)
    {
        Refused(form, ip, "name taken");
        return new(SignUpOutcome.NameTaken, NameTaken);
    }

    private void Refused(SignUpForm form, IPAddress? ip, string reason) =>
        audit.Write(new AuditEntry(LogText.Clean($"{form.First} {form.Last}".Trim(), 64), null, StaffRole.None.AuditName(), ip?.ToString(),
            "signup-refused", reason, "refused"));

    private async Task DeleteAsync(Guid principal)
    {
        try
        {
            await remover.DeleteAsync(principal, CancellationToken.None);
            pending.Remove(principal);
        }
        catch (Exception e) when (e is StaffDirectoryUnavailableException or IOException or UnauthorizedAccessException)
        {
            // Still on the pending list at -1 (when the list could be written), so the clean-up deletes it later.
            log.LogWarning("Sign-up: a half-made avatar {Principal} couldn't be deleted now: {Message}", principal, e.InnerException?.Message ?? e.Message);
        }
    }

    private static bool IsDown(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException
            or StaffDirectoryUnavailableException && !ct.IsCancellationRequested;

    private static string Site(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";

    private static string Full(RobustAccount a) => $"{a.FirstName} {a.LastName}";
}
