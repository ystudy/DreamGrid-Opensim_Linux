using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>
/// Changing how a region runs (spec 5.4): on its own, or with another group, offered with the add form's choices and
/// texts. The change applies at once, so the page names every region it restarts, in both groups, and asks first.
/// </summary>
public sealed class RunsModel(IGridController grid, RegionActions actions, Func<long?> freeMemory) : PageModel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>The other live regions it runs with now (empty when it runs on its own).</summary>
    public IReadOnlyList<string> Others { get; private set; } = [];

    [BindProperty] public string? Group { get; set; }
    /// <summary>The choices whose "will restart" box was ticked.</summary>
    [BindProperty] public List<string> Confirm { get; set; } = [];

    public RunsChoices Choices { get; private set; } = null!;
    public bool HasChoices => Choices.Own is not null || Choices.Groups.Count > 0;

    [TempData] public string? Notice { get; set; }

    private string _instance = "";
    private string _physics = "";
    private bool _ran;
    private GridStatus _status = null!;
    private IReadOnlyList<string> _leaving = [];
    private LauncherSettings _settings = null!;

    public IActionResult OnGet(Guid id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    public IActionResult OnPost(Guid id)
    {
        if (!Load(id)) return NotFound();

        var group = Choices.Groups.FirstOrDefault(g => g.Value == Group);
        var own = Choices.Own is not null && Group == RunsChoices.OwnValue;
        if (!own && group is null)
            ModelState.AddModelError(nameof(Group), "Pick how the region should run.");
        else if (Choices.Restarts(Group!) is { Count: > 0 } restarts && !Confirm.Contains(Group!))
            ModelState.AddModelError(nameof(Confirm), $"Tick the box to confirm that {RegionAdvice.Join(restarts)} will restart.");
        if (!ModelState.IsValid) return Page();

        if (!actions.TryChangeRunsWith(id, own ? null : group!.Instance)) return NotFound();
        Notice = $"Changing how {Name} runs. This can take a few minutes.";
        return Redirect("/regions/region/" + id);
    }

    private bool Load(Guid id)
    {
        var s = _settings = grid.CurrentSettings();
        if (RegionModel.Find(s, id) is not { DeletedAt: null } region) return false;
        var status = _status = grid.GetStatus();
        (Id, Name, _instance) = (id, region.Name, region.Instance);
        _physics = RegionModel.ProgramOf(s, region)?.Physics ?? "";
        Others = RegionModel.In(s, region.Instance).Where(r => r.Uuid != id).Select(r => r.Name).ToList();
        // The controller's rule: only a region an active program loads moves at once, restarting both groups. Otherwise
        // only the settings change and nothing restarts.
        _ran = status.Programs.FirstOrDefault(p => p.Instance == region.Instance) is { } program
            && RegionPowerPlan.IsActive(program.Process.State)
            && status.Regions.FirstOrDefault(r => r.Id == id)?.State is not (null or RegionState.Parked or RegionState.Stopped);
        _leaving = _ran ? RegionActions.SharingProgram(status, id) : [];

        var groups = GroupChoice.All(s, status).Where(g => g.Instance != region.Instance).ToList();
        // On its own is offered unless it already is.
        var ownAdvice = Others.Count > 0 ? RegionAdvice.OnItsOwn(status, freeMemory(), region.Size) : null;
        Choices = new RunsChoices(Group, ownAdvice, groups, g => RegionAdvice.Share(g.Names, Name), Restarts, Confirm, PhysicsNote);
        return true;
    }

    /// <summary>
    /// The regions a choice restarts when the region runs: those running in a group it joins, then the others running in
    /// the group it leaves. Nothing when it doesn't run.
    /// </summary>
    private IReadOnlyList<string> Restarts(string value)
    {
        if (!_ran) return [];
        if (value == RunsChoices.OwnValue) return _leaving;
        if (Choices.Groups.FirstOrDefault(g => g.Value == value) is not { } g) return [];
        var joined = g.Running
            ? _status.Regions.Where(r => r.Instance == g.Instance && r.State is RegionState.Running or RegionState.Starting).Select(r => r.Name)
            : [];
        return [.. joined, .. _leaving];
    }

    /// <summary>The group's settings win; physics is named when it differs.</summary>
    private string? PhysicsNote(GroupChoice g) =>
        _settings.Programs.FirstOrDefault(p => p.Instance == g.Instance)?.Physics is { } physics && physics != _physics
            ? $"Physics will change from {_physics} to {physics} to match {g.Names[0]}."
            : null;
}
