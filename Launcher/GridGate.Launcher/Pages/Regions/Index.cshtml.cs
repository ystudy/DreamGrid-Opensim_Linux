using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Regions;

/// <summary>One row of the page's plain region list (the page works without JavaScript); <c>Owner</c> is the guest who has region rights over it.</summary>
public sealed record RegionRow(Guid Id, string Name, string StateText, string RunsWithText, int X, int Y, int Size, string? Owner = null);

/// <summary>The Regions page (spec 5.2): region types, the map (drawn by map.js) and a plain list of the regions.</summary>
public sealed class IndexModel(IGridController grid, RegionRights rights) : PageModel
{
    public IReadOnlyList<RegionType> Types { get; private set; } = [];

    public IReadOnlyList<RegionRow> Rows { get; private set; } = [];

    /// <summary>The region the page was opened for (<c>?region=&lt;id&gt;</c>); the map opens its panel and the list highlights it.</summary>
    [BindProperty(SupportsGet = true, Name = "region")]
    public Guid? Selected { get; set; }

    /// <summary>The region being moved (<c>?move=&lt;id&gt;</c>): the map then asks for its new spot instead of a new region's.</summary>
    [BindProperty(SupportsGet = true, Name = "move")]
    public Guid? Move { get; set; }

    public RegionSettings? Moving { get; private set; }

    /// <summary>How many regions are in Recently deleted.</summary>
    public int DeletedCount { get; private set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>Whether the Landing section is shown: only where this computer runs the grid (a Region Server's grid decides).</summary>
    public bool LandingShown { get; private set; }

    /// <summary>The automatic default's name (the first region), shown as the select's first choice.</summary>
    public string? AutomaticName { get; private set; }

    /// <summary>The chosen default region's UUID; empty for automatic.</summary>
    [BindProperty] public string? LandingDefault { get; set; }

    /// <summary>The fallback regions' UUIDs.</summary>
    [BindProperty] public List<string> LandingFallbacks { get; set; } = [];

    public void OnGet()
    {
        var s = grid.CurrentSettings();
        LandingShown = PlanCatalog.RunsRobust(s);
        AutomaticName = RegionModel.First(s)?.Name;
        LandingDefault = Landing.DefaultChosen(s) ? Landing.Default(s)!.Uuid.ToString() : "";
        LandingFallbacks = [.. Landing.Fallbacks(s).Select(r => r.Uuid.ToString())];
        Fill();
    }

    /// <summary>
    /// Saves the landing regions and writes the config; Robust reads them when the world restarts. A value that is not one of
    /// this computer's live regions is a 400 and nothing is saved.
    /// </summary>
    public IActionResult OnPostLanding()
    {
        var s = grid.CurrentSettings();
        if (!PlanCatalog.RunsRobust(s)) return NotFound();
        var live = RegionModel.Live(s).Select(r => r.Uuid).ToHashSet();
        Guid? Parse(string? v) => Guid.TryParse(v, out var id) && live.Contains(id) ? id : null;

        Guid? chosen = null;
        if (!string.IsNullOrEmpty(LandingDefault) && (chosen = Parse(LandingDefault)) is null) return BadRequest();
        var fallbacks = new List<Guid>();
        foreach (var v in LandingFallbacks)
        {
            if (Parse(v) is not { } id) return BadRequest();
            if (id != chosen && !fallbacks.Contains(id)) fallbacks.Add(id);
        }

        grid.UpdateSettings(x => x with { Landing = new LandingSettings(chosen, fallbacks) }, writeConfig: true);
        Notice = "Landing saved. It applies when your world restarts.";
        return Redirect("/regions");
    }

    private void Fill()
    {
        Types = RegionTypes.All(grid.CurrentSettings());
        Moving = Move is { } id && RegionModel.Find(grid.CurrentSettings(), id) is { DeletedAt: null } r ? r : null;
        DeletedCount = grid.CurrentSettings().Regions.Count(r => r.DeletedAt is not null);
        var live = grid.GetStatus().Regions.ToDictionary(r => r.Id);
        // Region rights exist only on a plan with a guest side.
        var showOwners = AdminRule.HasStaffPages(grid.CurrentSettings());
        Rows = grid.CurrentSettings().Regions.Where(r => r.DeletedAt is null).Select(r =>
        {
            // A live region the status does not know yet counts as stopped.
            live.TryGetValue(r.Uuid, out var status);
            var others = status?.RunsWith ?? [];
            return new RegionRow(r.Uuid, r.Name, StatusApi.RegionStateText(status?.State ?? RegionState.Stopped),
                others.Count == 0 ? "Runs on its own" : "Runs with " + string.Join(", ", others), r.X, r.Y, r.Size,
                showOwners && rights.Of(r.Uuid) is { Held: true } right ? right.OwnerName : null);
        }).ToList();
    }
}
