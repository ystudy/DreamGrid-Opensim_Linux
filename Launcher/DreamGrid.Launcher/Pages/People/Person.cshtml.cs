using System.Globalization;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// One person (<c>/people/{id}</c>, StaffPolicy by the folder rule): their details, the same task icons as the list with their
/// words, and the last <see cref="HistoryCount"/> audit lines about them, so staff see what was done to the account before.
/// </summary>
public sealed class PersonModel(PeopleActions people, RegionRights rights, Func<LauncherSettings> settings, AuditLog audit, TakedownStore takedowns,
    AccountHolds holds) : PageModel
{
    public const int HistoryCount = 20;

    public PersonView? Person { get; private set; }
    public IReadOnlyList<string> Regions { get; private set; } = [];
    public string? Estate { get; private set; }
    public string? OnlineIn { get; private set; }
    public IReadOnlyList<AuditEntryRead> History { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>For the owner and admins: cases naming them (open), cases where they held a copy, and the open case holding them.</summary>
    public (int Named, int NamedOpen, int Copies)? Takedowns { get; private set; }
    public string? HeldBy { get; private set; }

    public TaskIcons Icons => TaskIcons.For(people, User, Person!, $"/people/{Person!.Row.Account.PrincipalId}?handler=Task", words: true);

    public static string When(DateTimeOffset? at) =>
        at is { } t ? t.UtcDateTime.ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture) : "Never";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => await LoadAsync(id, ct) || Error is not null ? Page() : NotFound();

    public async Task<IActionResult> OnPostTaskAsync(Guid id, PersonTask task, CancellationToken ct)
    {
        var outcome = await people.RunAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), id, task, ct);
        if (outcome.Done) Notice = outcome.Message;
        else Error = outcome.Message;
        return await LoadAsync(id, ct) || Error is not null ? Page() : NotFound();
    }

    /// <summary>The audit lines about <paramref name="name"/>: exactly them, a change to them ("Name: 0 -> 100"), or a rename from or to them.</summary>
    public static bool IsAbout(string? target, string name) =>
        target is not null && (string.Equals(target, name, StringComparison.OrdinalIgnoreCase)
                               || target.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
                               || target.StartsWith(name + " -> ", StringComparison.OrdinalIgnoreCase)
                               || target.EndsWith(" -> " + name, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        var (person, problem) = await people.LookAsync(User, id, ct);
        Person = person;
        Error ??= problem;
        if (Person is null) return false;
        var s = settings();
        var owned = rights.OwnedBy(id).Select(r => r.Region).ToHashSet();
        Regions = [.. s.Regions.Where(r => r.DeletedAt is null && owned.Contains(r.Uuid)).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase)];
        Estate = rights.OwnerEstateOf(id)?.Name;
        if (Person.Row.OnlineRegion is { } region)
            OnlineIn = s.Regions.FirstOrDefault(r => r.Uuid == region)?.Name ?? "a region on another computer";
        var name = Person.FullName;
        var staffView = TakedownAccess.MayUse(User);
        History = audit.Recent(e => IsAbout(e.Entry.Target, name) && (staffView || !TakedownRules.IsTakedownAudit(e.Entry.Action)), HistoryCount);
        if (staffView)
        {
            var cases = takedowns.Naming(id);
            var named = cases.Where(c => c.People.Any(p => p.Principal == id && p.Kind == PersonOnCase.Named)).ToList();
            var copies = cases.Count(c => c.People.Any(p => p.Principal == id && p.Kind == PersonOnCase.CopyHolder));
            if (cases.Count > 0) Takedowns = (named.Count, named.Count(c => c.Open), copies);
            HeldBy = holds.OpenCasesOf(id).FirstOrDefault();
        }
        return true;
    }
}
