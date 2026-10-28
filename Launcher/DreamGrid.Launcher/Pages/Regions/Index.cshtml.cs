using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One row of the page's plain region list (the page works without JavaScript).</summary>
public sealed record RegionRow(Guid Id, string Name, string StateText, string RunsWithText, int X, int Y, int Size);

/// <summary>The Regions page (spec 5.2): region types, the map (drawn by map.js) and a plain list of the regions.</summary>
public sealed class IndexModel(IGridController grid) : PageModel
{
    public IReadOnlyList<RegionType> Types { get; } = RegionTypes.BuiltIn;

    public IReadOnlyList<RegionRow> Rows { get; private set; } = [];

    /// <summary>The region the page was opened for (<c>?region=&lt;id&gt;</c>); the map opens its panel and the list highlights it.</summary>
    [BindProperty(SupportsGet = true, Name = "region")]
    public Guid? Selected { get; set; }

    public void OnGet()
    {
        var live = grid.GetStatus().Regions.ToDictionary(r => r.Id);
        Rows = grid.CurrentSettings().Regions.Where(r => r.DeletedAt is null).Select(r =>
        {
            // A live region the status does not know yet counts as stopped.
            live.TryGetValue(r.Uuid, out var status);
            var others = status?.RunsWith ?? [];
            return new RegionRow(r.Uuid, r.Name, StatusApi.RegionStateText(status?.State ?? RegionState.Stopped),
                others.Count == 0 ? "Runs on its own" : "Runs with " + string.Join(", ", others), r.X, r.Y, r.Size);
        }).ToList();
    }
}
