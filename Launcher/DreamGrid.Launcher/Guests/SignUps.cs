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
public enum SignUpOutcome { Closed, Invalid, NameTaken, CheckEmail, SignedUp, Busy }

/// <summary>A sign-up's outcome; <c>Problem</c> is the sentence to show, <c>Account</c> the avatar when it can be used at once.</summary>
public sealed record SignUpResult(SignUpOutcome Outcome, string? Problem = null, RobustAccount? Account = null);

/// <summary>Robust or the database didn't answer while a link was being confirmed: the link is still good.</summary>
public sealed class SignUpBusyException(string message, Exception inner) : Exception(message, inner);

/// <summary>Sign-up's own request counts (a second <see cref="ResetLimits"/> on its own file, so sign-ups and resets don't share).</summary>
public sealed record SignUpLimits(ResetLimits Counts);

/// <summary>
/// Resident sign-up behind the guest pages (spec 2026-10-31-sign-up-design). <see cref="SubmitAsync"/> checks, in order, that
/// sign-up is open, the address's limit, the name, the password, the email and the Terms tick, and that the name is free (said
/// plainly). With email set up, an address already on the grid is never said: its owner gets a short notice and the reply is the
/// normal "check your email". Otherwise the avatar is made (<see cref="IGridController.CreateResidentAsync"/>), its password
/// written through the database (<see cref="IRobustPasswords"/>, so it never goes on the console), and the Terms acceptance
/// recorded. With email it waits at level −1 on the <see cref="PendingSignUps"/> list for a single-use link valid
/// <see cref="LinkLifetime"/>; without email it is level 0 and ready. A failure after the avatar was made deletes it again.
/// <see cref="ConfirmAsync"/> raises a pending avatar to 0 and only then spends the link. The audit log gets every outcome,
/// never the typed email, the password or the token.
/// </summary>
public sealed class SignUps(Func<LauncherSettings> settings, IGridController grid, IRobustClient robust, IStaffDirectory directory,
    IAccountRemover remover, IRobustPasswords passwords, PendingSignUps pending, SignUpLimits limits, EmailTokens tokens,
    EmailSender sender, TermsAcceptances terms, AuditLog audit, ILogger<SignUps> log)
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

        try
        {
            if (await robust.GetAccountAsync(first, last, ct) is not null) return Taken(form, ip);
            if (withEmail && await directory.FindByEmailAsync(email, ct) is { } owner)
            {
                NoticeOwner(s, owner, email);
                Refused(form, ip, "email taken");
                return new(SignUpOutcome.CheckEmail);
            }
        }
        catch (Exception e) when (IsDown(e, ct))
        {
            log.LogWarning("Sign-up: the grid did not answer: {Message}", e.Message);
            return new(SignUpOutcome.Busy);
        }

        RobustAccount account;
        try
        {
            account = await grid.CreateResidentAsync(first, last, email, withEmail ? -1 : 0, ct);
        }
        catch (NameTakenException)
        {
            return Taken(form, ip);
        }
        catch (Exception e) when (IsDown(e, ct))
        {
            log.LogWarning("Sign-up: the avatar for first name {First} couldn't be made: {Message}", LogText.Clean(first, 32), e.Message);
            return new(SignUpOutcome.Busy);
        }

        try
        {
            await passwords.SetAsync(account.PrincipalId, form.Password!, ct);
            if (withEmail) pending.Add(account.PrincipalId);
        }
        catch (Exception e) when (e is RobustPasswordsUnavailableException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Sign-up: the avatar for first name {First} couldn't be finished, so it is deleted: {Message}",
                LogText.Clean(first, 32), e.InnerException?.Message ?? e.Message);
            await DeleteAsync(account.PrincipalId);
            return new(SignUpOutcome.Busy);
        }

        if (LegalPages.Get(s, LegalPages.Terms) is { } t) terms.Accept(account.PrincipalId, t.Version, ip?.ToString());
        audit.Write(new AuditEntry(Full(account), account.PrincipalId, StaffRole.None.AuditName(), ip?.ToString(), "signup-made",
            withEmail ? "waiting for email" : "ready", "ok"));
        if (!withEmail) return new(SignUpOutcome.SignedUp, Account: account);

        SendLink(s, account, email);
        return new(SignUpOutcome.CheckEmail);
    }

    /// <summary>
    /// The avatar behind a live confirm link, raised to level 0, or null for a link that is used, expired, unknown or not a
    /// pending sign-up's. Throws <see cref="SignUpBusyException"/> when Robust didn't answer; the link is then still good.
    /// </summary>
    public async Task<RobustAccount?> ConfirmAsync(string? token, IPAddress? ip, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (tokens.Find(Purpose, token) is not { } principal || !pending.Contains(principal)) return null;
            RobustAccount account;
            try
            {
                if (await robust.FindAccountByIdAsync(principal, ct) is not { } found) return null;
                account = found.UserLevel == 0 ? found : await grid.SetUserLevelAsync(found.FirstName, found.LastName, 0, ct);
            }
            catch (Exception e) when (IsDown(e, ct) || e is LevelNotConfirmedException)
            {
                throw new SignUpBusyException("The account couldn't be confirmed right now.", e);
            }
            tokens.TryRedeem(Purpose, principal, token);
            pending.Remove(principal);
            audit.Write(new AuditEntry(Full(account), principal, StaffRole.None.AuditName(), ip?.ToString(), "signup-confirmed", null, "ok"));
            return account;
        }
        finally { _one.Release(); }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

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
        if (account is not null && pending.Contains(account.PrincipalId) && account.Email.Trim().Length > 0)
            SendLink(settings(), account, SignUpRules.NormalEmail(account.Email));
        return SignUpOutcome.CheckEmail;
    }

    private void SendLink(LauncherSettings s, RobustAccount account, string email)
    {
        if (!Counts.SendAllowed(account.PrincipalId, email)) return;
        Counts.CountSend(account.PrincipalId, email);
        var token = tokens.Issue(Purpose, account.PrincipalId, LinkLifetime);
        // The clean-up counts from the newest link, so it never deletes an avatar whose link still works.
        pending.Add(account.PrincipalId);
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
        try { await remover.DeleteAsync(principal, CancellationToken.None); }
        catch (StaffDirectoryUnavailableException e)
        {
            // Not on the pending list, so the clean-up won't find it either; the owner sees the name on People.
            log.LogWarning("Sign-up: a half-made avatar {Principal} couldn't be deleted: {Message}", principal, e.InnerException?.Message ?? e.Message);
        }
    }

    private static bool IsDown(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException
            or StaffDirectoryUnavailableException && !ct.IsCancellationRequested;

    private static string Site(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";

    private static string Full(RobustAccount a) => $"{a.FirstName} {a.LastName}";
}
