using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Email;
using GridGate.Launcher.Guests;

namespace GridGate.Launcher.Security;

/// <summary>A person as the People pages show them: their row, what the rules know about them, and their roles.</summary>
public sealed record PersonView(PersonRow Row, PersonFacts Facts, RoleView Role)
{
    public string FullName => $"{Row.Account.FirstName} {Row.Account.LastName}";
}

/// <summary>
/// The People pages' tasks (spec 2026-11-03-people-admin-design): edit a name and email, send a reset link, sign out
/// everywhere (in-world too), suspend and unsuspend. Each one asks <see cref="PeopleRules"/> again on the post, does the work,
/// tells the person by email where the spec says so, and writes the audit log (<c>name-changed</c>, <c>email-changed</c>,
/// <c>reset-sent</c>, <c>signed-out</c>; suspensions are <see cref="StaffActions"/>'s). Nothing here takes a password.
/// </summary>
public sealed class PeopleActions(IPeopleDirectory people, StaffActions staff, Func<LauncherSettings> settings, SignUpBoard pending,
    EmailSender sender, IRobustClient robust, IRobustNames names, IRobustEmails emails, IStaffDirectory directory, HeldNames held,
    PasswordResets resets, StaffSessions sessions, GuestStamps stamps, IGridController grid, IAccountRemover remover, IEstateRemover estates,
    RegionRights rights, UploadRights uploads, IarRights iarRights, AccountHolds holds, SuspendedStore suspended, EmailTokens tokens, PendingEmailChanges emailChanges, AuditLog audit,
    ResidentLanguages languages, EmailLanguage emailLanguage, ILogger<PeopleActions> log)
{
    public const string OnHold = "This account is on hold.";
    public const string Moving = "Their regions are moving back to the grid's estate. Try again in a minute.";

    public const string Gone = "That person isn't on the grid any more.";
    public const string DatabaseDown = "Your world's database isn't answering. Start your world, then try again.";
    public const string InWorld = "Sign them out first: they are in-world.";
    public const string KickMessage = "Your session was ended by staff.";
    public const string StillInWorld = "Signed out of the website. They are still in-world: the region they are in couldn't be reached.";

    /// <summary>The person with what the rules need, or null when there is no such account.</summary>
    public async Task<PersonView?> ViewAsync(ClaimsPrincipal actor, Guid principal, CancellationToken ct) =>
        await people.GetAsync(principal, ct) is { } row ? View(actor, row) : null;

    /// <summary>Every row of a page of the list, with what the rules need.</summary>
    public IReadOnlyList<PersonView> Views(ClaimsPrincipal actor, PeoplePage page) => [.. page.Rows.Select(r => View(actor, r))];

    public string? Refusal(ClaimsPrincipal actor, PersonView person, PersonTask task) =>
        PeopleRules.Refusal(StaffSignIn.RoleOf(actor), person.Facts, task);

    private PersonView View(ClaimsPrincipal actor, PersonRow row)
    {
        var a = row.Account;
        var role = staff.RoleOf(a);
        var facts = new PersonFacts(role.Judged, role.IsOwner, StaffSignIn.Read(actor)?.Principal == a.PrincipalId || ActorId(actor) == a.PrincipalId,
            a.UserLevel < 0, a.UserLevel < 0 && pending.Contains(a.PrincipalId), a.Email.Trim().Length > 0, sender.IsReady);
        return new PersonView(row, facts, role);
    }

    private static Guid? ActorId(ClaimsPrincipal actor) =>
        Guid.TryParse(actor.FindFirstValue(StaffSignIn.PrincipalClaim), out var id) ? id : null;

    public async Task<ChangeOutcome> EditAsync(ClaimsPrincipal actor, string? ip, Guid principal, string? first, string? last, string? email,
        CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var a = person.Row.Account;
        var oldName = person.FullName;
        first = first?.Trim() ?? "";
        last = last?.Trim() ?? "";
        var newEmail = (email ?? "").Trim().Length == 0 ? "" : SignUpRules.NormalEmail(email!);
        var renaming = first != a.FirstName || last != a.LastName;
        var changingEmail = newEmail != SignUpRules.NormalEmail(a.Email);
        var action = renaming ? "name-changed" : "email-changed";
        if (Refusal(actor, person, PersonTask.Edit) is { } refusal)
        {
            Write(actor, ip, action, LogText.Clean(oldName, 64), "refused");
            return new(false, refusal);
        }
        if (!renaming && !changingEmail) return new(false, "Nothing was changed.");

        var s = settings();
        var newName = $"{first} {last}";
        if (renaming)
        {
            // A change of case only is the same name to the login, and to the held list.
            var sameName = string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase);
            if (SignUpRules.NameProblem(first, last, s, sameName ? null : held.IsHeld) is { } badName) return new(false, badName);
            if (person.Row.OnlineRegion is not null) return new(false, InWorld);
            try
            {
                if (!sameName && await robust.FindAccountAsync(first, last, ct) is { } other && other.PrincipalId != principal)
                    return new(false, SignUps.NameTaken);
            }
            catch (Exception e) when (RobustFailure.IsUnreachable(e, ct))
            {
                return new(false, StaffActions.RobustDown);
            }
        }
        if (changingEmail && newEmail.Length > 0)
        {
            if (SignUpRules.EmailProblem(newEmail) is { } badEmail) return new(false, badEmail);
            try
            {
                if (await directory.FindByEmailAsync(newEmail, ct) is { } other && other.PrincipalId != principal)
                    return new(false, "That email address is used by another account.");
            }
            catch (StaffDirectoryUnavailableException)
            {
                return new(false, DatabaseDown);
            }
        }

        var said = new List<string>();
        if (renaming)
        {
            try
            {
                await names.SetAsync(principal, first, last, ct);
            }
            catch (NameTakenException)
            {
                return new(false, SignUps.NameTaken);
            }
            catch (RobustNamesUnavailableException e)
            {
                log.LogWarning("People: a name couldn't be written: {Message}", e.InnerException?.Message ?? e.Message);
                Write(actor, ip, "name-changed", LogText.Clean($"{oldName} -> {newName}", 140), "failed");
                return new(false, DatabaseDown);
            }
            if (!string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase) && !held.Hold(oldName, principal))
                log.LogWarning("People: the old name couldn't be held (the held-names file couldn't be saved).");
            if (a.Email.Trim() is { Length: > 0 } to)
                sender.Queue(EmailTemplates.NameChanged, emailLanguage.ForResident(principal), to, newName, new Dictionary<string, string> { ["OldName"] = oldName, ["AvatarName"] = newName });
            Write(actor, ip, "name-changed", LogText.Clean($"{oldName} -> {newName}", 140), "ok");
            said.Add($"{oldName} is now {newName}. Viewers may show the old name until they log in again, and other grids for longer.");
        }
        if (changingEmail)
        {
            try
            {
                await emails.SetAsync(principal, newEmail, ct);
            }
            catch (RobustEmailsUnavailableException e)
            {
                log.LogWarning("People: an email address couldn't be written: {Message}", e.InnerException?.Message ?? e.Message);
                Write(actor, ip, "email-changed", LogText.Clean(newName, 64), "failed");
                return new(said.Count > 0, string.Join(" ", [.. said, "The email address couldn't be saved. Try again."]));
            }
            // Links already sent went to whoever had the address: a waiting email change and every reset link stop working, so a
            // hijacker can't take the account back with one after staff gave it back.
            AccountChanges.CancelEmailChange(tokens, emailChanges, principal);
            tokens.Revoke(PasswordResets.Purpose, principal);
            tokens.Revoke(PasswordResets.StaffPurpose, principal);
            var forgot = $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}{PasswordResets.AskPath}";
            if (a.Email.Trim() is { Length: > 0 } old)
                sender.Queue(EmailTemplates.EmailChangedByStaff, emailLanguage.ForResident(principal), old, newName, new Dictionary<string, string> { ["AvatarName"] = newName, ["Link"] = forgot });
            if (newEmail.Length > 0)
                sender.Queue(EmailTemplates.EmailSetByStaff, emailLanguage.ForResident(principal), newEmail, newName, new Dictionary<string, string> { ["AvatarName"] = newName });
            Write(actor, ip, "email-changed", LogText.Clean(newName, 64), "ok");
            said.Add(newEmail.Length > 0 ? $"{newName}'s email address is changed." : $"{newName} has no email address now.");
        }
        return new(true, string.Join(" ", said));
    }

    public async Task<ChangeOutcome> SendResetAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var target = LogText.Clean(person.FullName, 64);
        if (Refusal(actor, person, PersonTask.ResetLink) is { } refusal)
        {
            Write(actor, ip, "reset-sent", target, "refused");
            return new(false, refusal);
        }
        var result = resets.SendByStaff(person.Row.Account);
        Write(actor, ip, "reset-sent", target, result == StaffResetResult.Sent ? "ok" : "failed");
        return result switch
        {
            StaffResetResult.Sent => new(true, $"A reset link was sent to {person.FullName}."),
            StaffResetResult.NoEmail => new(false, PeopleRules.NoEmail),
            StaffResetResult.Limited => new(false, $"{person.FullName} was sent 3 links in the last hour. Try again later."),
            _ => new(false, "The email couldn't be sent. Check the email settings."),
        };
    }

    public async Task<ChangeOutcome> SignOutAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var target = LogText.Clean(person.FullName, 64);
        if (Refusal(actor, person, PersonTask.SignOut) is { } refusal)
        {
            Write(actor, ip, "signed-out", target, "refused");
            return new(false, refusal);
        }
        sessions.EndAll(principal);
        stamps.Rotate(principal);
        var kicked = await KickAsync(person, ct);
        Write(actor, ip, "signed-out", target, kicked ? "ok" : "failed");
        return kicked ? new(true, $"{person.FullName} is signed out everywhere.") : new(true, StillInWorld);
    }

    /// <summary>The tasks an icon posts straight from a list or a person's page (edit and delete have pages of their own).</summary>
    public Task<ChangeOutcome> RunAsync(ClaimsPrincipal actor, string? ip, Guid principal, PersonTask task, CancellationToken ct) => task switch
    {
        PersonTask.ResetLink => SendResetAsync(actor, ip, principal, ct),
        PersonTask.SignOut => SignOutAsync(actor, ip, principal, ct),
        PersonTask.Suspend => SuspendAsync(actor, ip, principal, ct),
        PersonTask.Unsuspend => UnsuspendAsync(actor, ip, principal, ct),
        PersonTask.AllowUpload => UploadRightAsync(actor, ip, principal, true, ct),
        PersonTask.StopUpload => UploadRightAsync(actor, ip, principal, false, ct),
        _ => Task.FromResult(new ChangeOutcome(false, "Use the page for that.")),
    };

    /// <summary>Whether the person may upload models.</summary>
    public bool MayUpload(Guid principal) => uploads.Has(principal);

    /// <summary>Switches the person's right to upload models on or off. Already as asked is not a change: no audit line.</summary>
    private async Task<ChangeOutcome> UploadRightAsync(ClaimsPrincipal actor, string? ip, Guid principal, bool allow, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var name = person.FullName;
        if (Refusal(actor, person, allow ? PersonTask.AllowUpload : PersonTask.StopUpload) is { } refusal)
        {
            Write(actor, ip, "upload-right-changed", LogText.Clean(name, 64), "refused");
            return new(false, refusal);
        }
        if (uploads.Has(principal) == allow)
            return new(true, allow ? $"{name} may already upload models." : $"{name} could not upload models already.");
        if (allow) uploads.Grant(principal, name);
        else uploads.Remove(principal);
        Write(actor, ip, "upload-right-changed", $"{LogText.Clean(name, 64)}: {(allow ? "off -> on" : "on -> off")}", "ok");
        return new(true, allow ? $"{name} may upload models now." : $"{name} may not upload models any more.");
    }

    /// <summary>The person's inventory backup switches and own limits.</summary>
    public IarPersonRights IarRightsOf(Guid principal) => iarRights.For(principal);

    /// <summary>
    /// Saves the person's inventory backup switches and own limits (the rule of <see cref="PersonTask.IarRights"/>: the owner for
    /// anyone, admins for residents and themselves). Each change is audited as <c>iar-rights-changed</c>, for example
    /// <c>"Ann Lee: download on -> off"</c> or <c>"Ann Lee: UploadsPerDay grid -> 10"</c>; nothing changed is not audited.
    /// A limit out of the grid settings' range is refused and nothing is saved.
    /// </summary>
    public async Task<ChangeOutcome> SetIarRightsAsync(ClaimsPrincipal actor, string? ip, Guid principal, bool downloadOn, bool uploadOn,
        int? maxUploadMb, int? maxDownloadMb, int? downloadsPerDay, int? uploadsPerDay, int? downloadKbPerSecond, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var name = LogText.Clean(person.FullName, 64);
        if (Refusal(actor, person, PersonTask.IarRights) is { } refusal)
        {
            Write(actor, ip, "iar-rights-changed", name, "refused");
            return new(false, refusal);
        }
        var wanted = new IarPersonRights(principal, !downloadOn, !uploadOn, maxUploadMb, maxDownloadMb, downloadsPerDay, uploadsPerDay,
            downloadKbPerSecond);
        // The person's limits are checked as the grid's would be, field by field, against a copy of the grid's settings.
        var check = SettingsValidator.Iar(IarRights.Effective(settings().Iar, wanted));
        if (!check.Ok) return new(false, string.Join(" ", check.Errors.Values));

        var before = iarRights.For(principal);
        var changes = new List<string>();
        if (before.DownloadOff != wanted.DownloadOff) changes.Add($"download {OnOff(!before.DownloadOff)} -> {OnOff(downloadOn)}");
        if (before.UploadOff != wanted.UploadOff) changes.Add($"upload {OnOff(!before.UploadOff)} -> {OnOff(uploadOn)}");
        Limit(nameof(IarSettings.MaxUploadMb), before.MaxUploadMb, wanted.MaxUploadMb);
        Limit(nameof(IarSettings.MaxDownloadMb), before.MaxDownloadMb, wanted.MaxDownloadMb);
        Limit(nameof(IarSettings.DownloadsPerDay), before.DownloadsPerDay, wanted.DownloadsPerDay);
        Limit(nameof(IarSettings.UploadsPerDay), before.UploadsPerDay, wanted.UploadsPerDay);
        Limit(nameof(IarSettings.DownloadKbPerSecond), before.DownloadKbPerSecond, wanted.DownloadKbPerSecond);
        if (changes.Count == 0) return new(true, "Nothing changed.");

        iarRights.Set(wanted);
        foreach (var change in changes)
            Write(actor, ip, "iar-rights-changed", $"{name}: {change}", "ok");
        return new(true, $"Saved {person.FullName}'s inventory backup settings.");

        void Limit(string field, int? was, int? now)
        {
            if (was != now) changes.Add($"{field} {(was?.ToString() ?? "grid")} -> {(now?.ToString() ?? "grid")}");
        }

        static string OnOff(bool on) => on ? "on" : "off";
    }

    public Task<ChangeOutcome> SuspendAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct) =>
        SuspensionAsync(actor, ip, principal, StaffChange.Suspend, ct);

    public Task<ChangeOutcome> UnsuspendAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct) =>
        SuspensionAsync(actor, ip, principal, StaffChange.Unsuspend, ct);

    private async Task<ChangeOutcome> SuspensionAsync(ClaimsPrincipal actor, string? ip, Guid principal, StaffChange change, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        // Waiting sign-ups are the Sign-ups page's; every other refusal is StaffActions' own, which it audits.
        if (person.Facts.Waiting)
        {
            Write(actor, ip, change == StaffChange.Suspend ? "suspended" : "unsuspended", LogText.Clean(person.FullName, 64), "refused");
            return new(false, PeopleRules.Waiting);
        }
        var outcome = await staff.ApplyAsync(actor, ip, person.Row.Account, change, ct);
        if (outcome.Done && change == StaffChange.Suspend && !await KickAsync(person, ct))
            return outcome with { Message = outcome.Message + " They are still in-world: the region they are in couldn't be reached." };
        return outcome;
    }

    /// <summary>What the "Are you sure?" page shows: the person, the names of their regions that go back to the grid, and why not, if not.</summary>
    public sealed record DeletePreview(PersonView Person, IReadOnlyList<string> RegionsMoving, string? Refusal);

    public async Task<DeletePreview?> PreviewDeleteAsync(ClaimsPrincipal actor, Guid principal, CancellationToken ct)
    {
        if ((await LookAsync(actor, principal, ct)).Person is not { } person) return null;
        var s = settings();
        var regions = rights.OwnedBy(principal).Select(r => s.Regions.FirstOrDefault(x => x.Uuid == r.Region)?.Name ?? r.Region.ToString()).ToList();
        return new DeletePreview(person, regions, DeleteRefusal(actor, person));
    }

    private string? DeleteRefusal(ClaimsPrincipal actor, PersonView person) =>
        Refusal(actor, person, PersonTask.Delete) ?? holds.Refusal(person.Row.Account.PrincipalId);

    /// <summary>
    /// Deletes a suspended resident for good: their regions go back to the estate they came from first (the delete waits until
    /// they have), then everything in Robust tied to them, their now empty estate, and what the launcher kept about them.
    /// </summary>
    public async Task<ChangeOutcome> DeleteAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct)
    {
        var (person, problem) = await LookAsync(actor, principal, ct);
        if (person is null) return new(false, problem ?? Gone);
        var name = person.FullName;
        var target = LogText.Clean(name, 64);
        if (DeleteRefusal(actor, person) is { } refusal)
        {
            Write(actor, ip, "deleted", target, "refused");
            return new(false, refusal);
        }
        foreach (var right in rights.OwnedBy(principal).Where(r => r.Pending != EstateMove.ToGrid)) rights.Remove(right.Region);
        if (rights.PendingMoves().Any(r => r.Owner == principal)) return new(false, Moving);
        await KickAsync(person, ct);

        try
        {
            await remover.DeleteEverythingAsync(principal, ct);
        }
        catch (StaffDirectoryUnavailableException e)
        {
            log.LogWarning("People: an avatar couldn't be deleted: {Message}", e.InnerException?.Message ?? e.Message);
            Write(actor, ip, "deleted", target, "failed");
            return new(false, DatabaseDown);
        }

        var estateLeft = false;
        if (rights.EstateOf(principal) is { } estate)
        {
            try
            {
                if (!await estates.RemoveIfEmptyAsync(estate, ct))
                    log.LogWarning("People: estate {Estate} still has a region in it, so it was kept.", estate);
            }
            catch (StaffDirectoryUnavailableException e)
            {
                log.LogWarning("People: estate {Estate} couldn't be removed: {Message}", estate, e.InnerException?.Message ?? e.Message);
                estateLeft = true;
            }
        }
        rights.ForgetOwner(principal);
        if (!held.Release(principal)) log.LogWarning("People: the held-names file couldn't be saved.");
        try { suspended.Forget(principal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("People: the suspended list couldn't be saved ({Error}).", e.GetType().Name);
        }
        stamps.Rotate(principal);
        sessions.EndAll(principal);
        languages.Remove(principal);

        Write(actor, ip, "deleted", target, estateLeft ? "failed" : "ok");
        return estateLeft
            ? new(true, $"{name} was deleted, but their empty estate couldn't be removed. Check the log.")
            : new(true, $"{name} is deleted.");
    }

    /// <summary>Logs the person out of the region they are in, if any; false when they are in-world and that failed.</summary>
    private async Task<bool> KickAsync(PersonView person, CancellationToken ct)
    {
        if (person.Row.OnlineRegion is not { } region) return true;
        try
        {
            await grid.KickAsync(region, person.Row.Account.FirstName, person.Row.Account.LastName, KickMessage, ct);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or HttpRequestException or TimeoutException
                                      or ConsoleAuthException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("People: an avatar couldn't be logged out in-world: {Message}", LogText.Clean(e.Message, 300));
            return false;
        }
    }

    /// <summary>
    /// The person, or null with why: no such account (no reason), the database not answering, or the suspended list unreadable
    /// (who may change a suspended person depends on the level they get back).
    /// </summary>
    public async Task<(PersonView? Person, string? Problem)> LookAsync(ClaimsPrincipal actor, Guid principal, CancellationToken ct)
    {
        try
        {
            return (await ViewAsync(actor, principal, ct), null);
        }
        catch (PeopleDirectoryUnavailableException)
        {
            return (null, DatabaseDown);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("People: the suspended list could not be read ({Error}).", e.GetType().Name);
            return (null, StaffActions.SuspendedUnreadable);
        }
    }

    private void Write(ClaimsPrincipal actor, string? ip, string action, string target, string result)
    {
        var (who, id, role) = AuditActor.Of(actor);
        audit.Write(new AuditEntry(who, id, role, ip, action, target, result));
    }
}
