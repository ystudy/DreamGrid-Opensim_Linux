using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's Security page (OwnerPolicy; <c>/security</c>). Three things:
/// <list type="bullet">
/// <item>The switch that makes admins and supervisors use two-factor sign-in (never the owner).</item>
/// <item>The networks that may reach the website at all (<see cref="NetworkList"/>, enforced by <see cref="AdminNetworkFilter"/>). A list
/// that would not let the request's own address in is refused, so the owner can't shut themselves out.</item>
/// <item>Who has two-factor on, each with "Reset": two-factor is turned off for them and all their sessions end, so they sign in
/// with their password and set it up again.</item>
/// </list>
/// A save goes through IGridController.UpdateSettings (no config files change) and is audited as <c>security-settings</c> (a
/// save that changes nothing is not); a reset as <c>two-factor-reset</c> with the person's name as the target.
/// </summary>
public sealed class IndexModel(IGridController grid, Func<LauncherSettings> settings, TwoFactorStore twoFactor, StaffSessions sessions,
    AuditLog audit, ResidentTwoFactor residents, GuestStamps guestStamps) : PageModel
{
    public const int MaxEntries = 50;

    /// <summary>The switch as shown or posted (an unchecked box posts nothing, which reads as off).</summary>
    [BindProperty] public bool RequireTwoFactor { get; set; }

    /// <summary>The allow-list as typed, one entry per line.</summary>
    [BindProperty] public string? Networks { get; set; }

    public IReadOnlyList<(Guid Principal, string Name)> Enrolled { get; private set; } = [];

    /// <summary>Residents with two-factor on (their own file, <see cref="ResidentTwoFactor"/>).</summary>
    public IReadOnlyList<(Guid Principal, string Name)> Residents { get; private set; } = [];

    /// <summary>The residents' two-factor file can't be read: every resident's website sign-in stops at the code step until it is set aside.</summary>
    public bool ResidentsDamaged { get; private set; }

    /// <summary>The two-factor file can't be read, so nobody can be listed or reset here (the verb on the grid computer sets it aside).</summary>
    public bool TwoFactorDamaged { get; private set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public void OnGet()
    {
        var s = settings().Security;
        RequireTwoFactor = s.RequireTwoFactorForStaff;
        Networks = string.Join('\n', s.AdminNetworks ?? []);
        LoadPeople();
    }

    public IActionResult OnPostSave()
    {
        LoadPeople();
        var entries = Entries(Networks);
        if (entries.Count > MaxEntries)
            return Refuse($"Use {MaxEntries} entries or fewer.");
        if (NetworkList.Validate(entries) is { } bad)
            return Refuse(bad);
        if (entries.Count > 0 && !Allowed(entries))
            return Refuse($"This list would lock out the address you're using now ({Caller()}).");

        var before = settings().Security;
        var wanted = entries.Count == 0 ? null : (IReadOnlyList<string>)entries;
        var changes = new List<string>();
        if (before.RequireTwoFactorForStaff != RequireTwoFactor)
            changes.Add("require-two-factor=" + (RequireTwoFactor ? "on" : "off"));
        if (!(before.AdminNetworks ?? []).SequenceEqual(wanted ?? []))
            changes.Add("admin-networks=" + (wanted is null ? "any address" : wanted.Count == 1 ? "1 entry" : $"{wanted.Count} entries"));

        grid.UpdateSettings(s => s with
        {
            Security = s.Security with { RequireTwoFactorForStaff = RequireTwoFactor, AdminNetworks = wanted },
        }, writeConfig: false);
        if (changes.Count > 0)
            Write("security-settings", string.Join("; ", changes), "ok");
        Notice = "Saved. This applies now.";
        Networks = string.Join('\n', entries);
        return Page();
    }

    public IActionResult OnPostReset(Guid id)
    {
        var person = twoFactor.Enrolled().Where(p => p.Principal == id).Select(p => ((Guid, string)?)p).FirstOrDefault();
        if (person is not { } who)
        {
            Fill();
            Notice = "That person doesn't have two-factor sign-in on.";
            return Page();
        }

        try
        {
            twoFactor.Disable(who.Item1);
            // A change made at the same moment by another request could have put it back.
            if (twoFactor.StateOf(who.Item1) != TwoFactorState.Off)
                throw new IOException("The two-factor file changed while it was being saved.");
        }
        catch (InvalidOperationException)
        {
            Fill();
            return Refuse("The two-factor file is damaged and has not been changed. Run reset-two-factor --all-damaged on the grid computer.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fill();
            return Refuse(TwoFactorStore.SaveFailed);
        }

        sessions.EndAll(who.Item1);
        Write("two-factor-reset", LogText.Clean(who.Item2, 64), "ok");
        Fill();
        Notice = $"Two-factor sign-in is off for {who.Item2}. They have been signed out and sign in with their password again.";
        return Page();
    }

    /// <summary>
    /// A resident lost their phone and backup codes: two-factor is turned off in the residents' file and their guest sessions end
    /// (their stamp changes), so they sign in with their password. Audited as <c>two-factor-reset</c>.
    /// </summary>
    public IActionResult OnPostResetResident(Guid id)
    {
        var store = residents.Store;
        var person = store.Enrolled().Where(p => p.Principal == id).Select(p => ((Guid, string)?)p).FirstOrDefault();
        if (person is not { } who)
        {
            Fill();
            Notice = "That person doesn't have two-factor sign-in on.";
            return Page();
        }

        try
        {
            store.Disable(who.Item1);
            if (store.StateOf(who.Item1) != TwoFactorState.Off)
                throw new IOException("The two-factor file changed while it was being saved.");
        }
        catch (InvalidOperationException)
        {
            Fill();
            return Refuse("The residents' two-factor file is damaged and has not been changed.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fill();
            return Refuse(TwoFactorStore.SaveFailed);
        }

        guestStamps.Rotate(who.Item1);
        Write("two-factor-reset", LogText.Clean(who.Item2, 64), "ok");
        Fill();
        Notice = $"Two-factor sign-in is off for {who.Item2}. They have been signed out and sign in with their password again.";
        return Page();
    }

    /// <summary>
    /// Sets a damaged residents' two-factor file aside (renamed <c>twofactor.json.damaged-…</c>, as the <c>reset-two-factor
    /// --all-damaged</c> verb does for the staff file), so residents sign in with their password again and set two-factor up
    /// anew. Audited as <c>two-factor-set-aside</c>. A file that reads fine is left alone.
    /// </summary>
    public IActionResult OnPostSetAsideResidents()
    {
        try
        {
            TwoFactorStore.MoveAsideIfDamaged(residents.File);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fill();
            return Refuse("The residents' two-factor file couldn't be set aside. Try again.");
        }
        Fill();
        if (ResidentsDamaged) return Refuse("The residents' two-factor file couldn't be read just now, but it doesn't look damaged. Try again.");
        Write("two-factor-set-aside", "residents", "ok");
        Notice = "The residents' two-factor file was set aside. Residents sign in with their password and can set two-factor up again.";
        return Page();
    }

    private void Fill()
    {
        var s = settings().Security;
        RequireTwoFactor = s.RequireTwoFactorForStaff;
        Networks = string.Join('\n', s.AdminNetworks ?? []);
        LoadPeople();
    }

    private void LoadPeople()
    {
        TwoFactorDamaged = twoFactor.StateOf(Guid.Empty) == TwoFactorState.Unknown;
        Enrolled = [.. twoFactor.Enrolled().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        Residents = [.. residents.Store.Enrolled().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        ResidentsDamaged = residents.Store.StateOf(Guid.Empty) == TwoFactorState.Unknown;
    }

    private PageResult Refuse(string message)
    {
        Error = message;
        return Page();
    }

    /// <summary>The typed lines, trimmed, without empty ones or repeats.</summary>
    private static List<string> Entries(string? text) =>
        [.. (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private bool Allowed(List<string> entries) =>
        HttpContext.Connection.RemoteIpAddress is { } caller && NetworkList.Allows(entries, caller);

    private string Caller() =>
        HttpContext.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "unknown";

    private void Write(string action, string target, string result)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result));
    }
}
