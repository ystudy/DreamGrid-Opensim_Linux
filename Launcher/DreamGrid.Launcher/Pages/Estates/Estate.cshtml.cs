using DreamGrid.Launcher.Core.Estates;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Estates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Estates;

/// <summary>A region of this computer, for the estate page: its name and the estate it is in now.</summary>
public sealed record EstateRegion(Guid Id, string Name, string? Estate, bool Owned);

/// <summary>
/// One estate (<c>/estates/estate?id=…</c>): its name, owner, managers and regions, moving a region in, and deleting it when
/// empty (a ticked box). What may change depends on the kind of estate (<see cref="EstatesAdmin.KindOf"/>); every change goes
/// through <see cref="EstatesAdmin"/>, which checks the rules again and audits it.
/// </summary>
public sealed class EstateModel(IGridEstates estates, EstatesAdmin admin, IRobustClient robust, Func<LauncherSettings> settings, Guests.RegionRights rights,
    ILogger<EstateModel> log) : PageModel
{
    public EstateDetail? Estate { get; private set; }
    public EstateKind Kind { get; private set; }
    public string? MadeFor { get; private set; }
    public bool Unavailable { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string? TypedName { get; private set; }
    public string? TypedOwner { get; private set; }
    public string? TypedManager { get; private set; }

    /// <summary>This computer's regions in the estate.</summary>
    public IReadOnlyList<EstateRegion> InIt { get; private set; } = [];

    /// <summary>This computer's regions in other estates, that can be moved here.</summary>
    public IReadOnlyList<EstateRegion> Elsewhere { get; private set; } = [];

    /// <summary>Regions in the estate that aren't this computer's (left over from a deleted region, or another program's).</summary>
    public int OtherRegions { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Notice = TempData["EstateNotice"] as string;
        return await ShowAsync(id, ct);
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string? name, CancellationToken ct)
    {
        var outcome = await admin.RenameAsync(User, Ip, id, name, ct);
        if (!outcome.Done) TypedName = name;
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostOwnerAsync(int id, string? owner, CancellationToken ct)
    {
        var (account, problem) = await AvatarLookup.FindAsync(robust, owner, log, ct);
        var outcome = account is null ? new EstateOutcome(false, problem!) : await admin.SetOwnerAsync(User, Ip, id, account, ct);
        if (!outcome.Done) TypedOwner = owner;
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostAddManagerAsync(int id, string? manager, CancellationToken ct)
    {
        var (account, problem) = await AvatarLookup.FindAsync(robust, manager, log, ct);
        var outcome = account is null ? new EstateOutcome(false, problem!) : await admin.AddManagerAsync(User, Ip, id, account, ct);
        if (!outcome.Done) TypedManager = manager;
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostRemoveManagerAsync(int id, Guid manager, CancellationToken ct) =>
        await ShowAsync(id, ct, await admin.RemoveManagerAsync(User, Ip, id, manager, ct));

    public async Task<IActionResult> OnPostMoveAsync(int id, Guid region, CancellationToken ct) =>
        await ShowAsync(id, ct, await admin.MoveRegionAsync(User, Ip, id, region, ct));

    public async Task<IActionResult> OnPostDeleteAsync(int id, bool confirm, CancellationToken ct)
    {
        if (!confirm) return await ShowAsync(id, ct, new EstateOutcome(false, "Tick the box to say you want to delete this estate."));
        var outcome = await admin.DeleteAsync(User, Ip, id, ct);
        if (!outcome.Done) return await ShowAsync(id, ct, outcome);
        TempData["EstatesNotice"] = outcome.Message;
        return Redirect("/estates");
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task<IActionResult> ShowAsync(int id, CancellationToken ct, EstateOutcome? outcome = null)
    {
        if (outcome is not null)
        {
            if (outcome.Done) Notice = outcome.Message;
            else Error = outcome.Message;
        }
        IReadOnlyDictionary<Guid, int> where;
        IReadOnlyList<EstateSummary> all;
        try
        {
            Estate = await estates.GetAsync(id, ct);
            if (Estate is null) return NotFound();
            where = await estates.RegionEstatesAsync(ct);
            all = await estates.ListAsync(ct);
        }
        catch (EstatesUnavailableException)
        {
            Unavailable = true;
            return Page();
        }
        Kind = admin.KindOf(Estate.Id, Estate.Name);
        MadeFor = admin.MadeFor(Estate.Id);
        var names = all.ToDictionary(e => e.Id, e => e.Name);
        var ours = settings().Regions.Where(r => r.DeletedAt is null)
            .Select(r => new EstateRegion(r.Uuid, r.Name, where.TryGetValue(r.Uuid, out var e) ? names.GetValueOrDefault(e) : null, rights.Of(r.Uuid) is not null))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var inIt = Estate.Regions.ToHashSet();
        InIt = ours.Where(r => inIt.Contains(r.Id)).ToList();
        Elsewhere = ours.Where(r => !inIt.Contains(r.Id)).ToList();
        OtherRegions = Estate.Regions.Count - InIt.Count;
        return Page();
    }
}
