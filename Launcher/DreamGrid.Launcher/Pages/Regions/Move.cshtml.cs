using System.Globalization;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>
/// Moving a region (spec 5.4): the new spot by number or from the map (<c>/regions?move=&lt;id&gt;</c>, which comes back
/// with x and y). When the region runs with others the move restarts them, so the page asks first.
/// </summary>
public sealed class MoveModel(IGridController grid, RegionActions actions) : PageModel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public int Size { get; private set; }

    [BindProperty(SupportsGet = true)] public string? X { get; set; }
    [BindProperty(SupportsGet = true)] public string? Y { get; set; }

    /// <summary>The box "I understand ... will restart".</summary>
    [BindProperty] public bool Confirm { get; set; }

    /// <summary>The other regions the move restarts (empty when the region runs on its own or its program is down).</summary>
    public IReadOnlyList<string> Affected { get; private set; } = [];

    /// <summary>Whether the spot in X and Y is free or joins neighbours, in words; empty unless both numbers are given.</summary>
    public string PlacementText { get; private set; } = "";

    [TempData] public string? Notice { get; set; }

    public IActionResult OnGet(Guid id)
    {
        if (!Load(id, out var region)) return NotFound();
        X ??= region.X.ToString(CultureInfo.InvariantCulture);
        Y ??= region.Y.ToString(CultureInfo.InvariantCulture);
        Describe();
        return Page();
    }

    public IActionResult OnPost(Guid id)
    {
        if (!Load(id, out _)) return NotFound();

        // The first mistake is shown at its field; the checks run in this order.
        int x = 0, y = 0;
        if (!TryParse(X, out x) || !TryParse(Y, out y) || !RegionPlacement.OnMap(x, y, Size))
            ModelState.AddModelError(nameof(X), "That spot is off the map.");
        else if (RegionPlacement.Check(grid.CurrentSettings(), x, y, Size, ignore: id) is { Ok: false } taken)
            ModelState.AddModelError(nameof(X), RegionAdvice.Placement(taken));
        else if (Affected.Count > 0 && !Confirm)
            ModelState.AddModelError(nameof(Confirm), $"Tick the box to confirm that {RegionAdvice.Join(Affected)} will restart.");
        if (!ModelState.IsValid)
        {
            Describe();
            return Page();
        }

        if (!actions.TryMove(id, x, y)) return NotFound();
        Notice = $"Moving {Name}…";
        return Redirect("/regions/region/" + id);
    }

    private bool Load(Guid id, out RegionSettings region)
    {
        region = null!;
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: null } r) return false;
        region = r;
        (Id, Name, Size) = (id, r.Name, r.Size);
        Affected = RegionActions.SharingProgram(grid.GetStatus(), id);
        return true;
    }

    private void Describe()
    {
        if (!TryParse(X, out var x) || !TryParse(Y, out var y)) return;
        PlacementText = !RegionPlacement.OnMap(x, y, Size)
            ? "That spot is off the map."
            : RegionAdvice.Placement(RegionPlacement.Check(grid.CurrentSettings(), x, y, Size, ignore: Id));
    }

    private static bool TryParse(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
