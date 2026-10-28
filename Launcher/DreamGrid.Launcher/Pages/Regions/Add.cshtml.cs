using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Setup;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One "runs with" choice: the regions that already run together. <see cref="Value"/> is the first region's id (never a folder name).</summary>
public sealed record GroupChoice(string Value, string Instance, IReadOnlyList<string> Names, bool Running)
{
    public string Label => "Runs with " + RegionAdvice.GroupLabel(Names);

    /// <summary>"Home", "Home and Beach", "Home, Beach and Forest".</summary>
    public string NamesText => Names.Count <= 1 ? string.Concat(Names) : $"{string.Join(", ", Names.Take(Names.Count - 1))} and {Names[^1]}";
}

/// <summary>
/// Add a region (spec 5.3): its name, type, map position, size and how it runs. A valid post starts <see cref="RegionJob"/>
/// (one add at a time) and shows its progress at /regions/add?working=1; the map opens this page with a type and position.
/// </summary>
public sealed class AddModel(IGridController grid, RegionJob job, Func<long?> freeMemory) : PageModel
{
    private const string WorkingUrl = "/regions/add?working=1";
    private const string OwnValue = "own";
    private const int MaxCoordinate = 65535;

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Type { get; set; }
    [BindProperty] public string? X { get; set; }
    [BindProperty] public string? Y { get; set; }
    [BindProperty] public string? Size { get; set; }
    [BindProperty] public string? Group { get; set; }
    /// <summary>The groups whose "will restart" box was ticked.</summary>
    [BindProperty] public List<string> Confirm { get; set; } = [];

    public IReadOnlyList<RegionType> Types { get; } = RegionTypes.BuiltIn;
    public IReadOnlyList<GroupChoice> Groups { get; private set; } = [];
    public OwnAdvice Own { get; private set; } = null!;
    public string? PlacementText { get; private set; }
    public string? SizeRisk { get; private set; }
    /// <summary>Every size's risk sentence (empty for 1×1), for the script that refreshes it.</summary>
    public IReadOnlyList<string> RiskBySize { get; } = Enumerable.Range(1, RegionPlacement.MaxSize).Select(n => RegionAdvice.SizeRisk(n) ?? "").ToList();
    public SetupProgressSnapshot Progress { get; private set; } = new("", 0, false, null, null);
    public Guid? SavedId { get; private set; }
    public string? JobName { get; private set; }
    /// <summary>True when the world is not running, so nothing can be added yet.</summary>
    public bool WorldStopped { get; private set; }
    private bool ShowResult { get; set; }

    public bool Running => Progress.Running;
    public bool Ready => ShowResult && !Progress.Running && Progress.Error is null && Progress.Stage == SetupProgress.Ready;
    public bool Failed => ShowResult && !Progress.Running && Progress.Error is not null;
    /// <summary>A failed add whose region stayed saved: it is retried from its panel, so the form is not offered.</summary>
    public bool FailedButSaved => Failed && SavedId is not null;
    public bool ShowForm => !Running && !Ready && !FailedButSaved;
    public string ChosenGroup => Group ?? OwnValue;

    public IActionResult OnGet(string? type, int? x, int? y, int? size, string? working)
    {
        Load(working is not null);
        Type = type is not null && RegionTypes.Find(type) is not null ? type : RegionTypes.BuiltIn[0].Id;
        (X, Y) = (x?.ToString(CultureInfo.InvariantCulture), y?.ToString(CultureInfo.InvariantCulture));
        Size = size is >= 1 and <= RegionPlacement.MaxSize ? size.Value.ToString(CultureInfo.InvariantCulture) : "1";
        Prepare();
        // Plenty of memory: on its own. Short: the first group to share with, when there is one.
        Group = Own.Short && Groups.Count > 0 ? Groups[0].Value : OwnValue;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (job.Snapshot().Running) return Redirect(WorkingUrl); // one add at a time; its progress is shown
        Load(false);
        Prepare();

        var s = grid.CurrentSettings();
        var name = Name ?? "";
        var size = 0;
        var (x, y) = (0, 0);
        // The checks run in this order and the first mistake is shown at its field.
        if (name == "")
            ModelState.AddModelError(nameof(Name), "Give your region a name.");
        else if (!GridController.IsValidRegionName(name))
            ModelState.AddModelError(nameof(Name),
                "Use 2 to 32 letters, digits, spaces, hyphens (-), underscores (_) or apostrophes ('), with no space at the start or end.");
        else if (RegionModel.FindByName(s, name) is not null)
            ModelState.AddModelError(nameof(Name), $"There is already a region called {name}.");
        else if (Type is null || RegionTypes.Find(Type) is null)
            ModelState.AddModelError(nameof(Type), "Pick what your region should look like.");
        else if (!TryParse(Size, out size) || size is < 1 or > RegionPlacement.MaxSize)
            ModelState.AddModelError(nameof(Size), "A region is 1 to 16 squares on each side.");
        else if (!TryParse(X, out x) || !TryParse(Y, out y) || x < 0 || y < 0 || x > MaxCoordinate - size || y > MaxCoordinate - size)
            ModelState.AddModelError(nameof(X), "That spot is off the map.");
        else if (RegionPlacement.Check(s, x, y, size) is { Ok: false } taken)
            ModelState.AddModelError(nameof(X), RegionAdvice.Placement(taken));
        else if (Group != OwnValue && Groups.FirstOrDefault(g => g.Value == Group) is null)
            ModelState.AddModelError(nameof(Group), "Pick how the region should run.");
        else if (Groups.FirstOrDefault(g => g.Value == Group) is { Running: true } running && !Confirm.Contains(running.Value))
            ModelState.AddModelError(nameof(Confirm), $"Tick the box to confirm that {running.NamesText} will restart.");
        else if (grid.GetStatus().Robust.State != ProcessState.Running)
            WorldStopped = true;
        if (!ModelState.IsValid || WorldStopped) return Page();

        var share = Groups.FirstOrDefault(g => g.Value == Group)?.Instance;
        job.TryStart(new NewRegion(name, x, y, size, Type!, share), (r, ct, p) => grid.AddRegionAsync(r, ct, p));
        return Redirect(WorkingUrl);
    }

    private static bool TryParse(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>The job's state, and the groups of regions that can be shared with.</summary>
    private void Load(bool showResult)
    {
        Progress = job.Snapshot();
        JobName = job.Name;
        SavedId = job.RegionId;
        ShowResult = showResult;

        var s = grid.CurrentSettings();
        var programs = grid.GetStatus().Programs.ToDictionary(p => p.Instance, p => p.Process.State);
        Groups = s.Programs
            .Select(p => (Program: p, Regions: RegionModel.In(s, p.Instance).ToList()))
            .Where(g => g.Regions.Count > 0)
            .Select(g => new GroupChoice(g.Regions[0].Uuid.ToString(), g.Program.Instance, g.Regions.Select(r => r.Name).ToList(),
                programs.TryGetValue(g.Program.Instance, out var state) && RegionPowerPlan.IsActive(state)))
            .ToList();
    }

    /// <summary>The advice that depends on the choices made so far (computed here, so it also updates on submit).</summary>
    private void Prepare()
    {
        var size = TryParse(Size, out var n) && n is >= 1 and <= RegionPlacement.MaxSize ? n : 1;
        Own = RegionAdvice.OnItsOwn(grid.GetStatus(), freeMemory(), size);
        SizeRisk = RegionAdvice.SizeRisk(size);
        if (TryParse(X, out var x) && TryParse(Y, out var y))
        {
            PlacementText = x < 0 || y < 0 || x > MaxCoordinate - size || y > MaxCoordinate - size
                ? "That spot is off the map."
                : RegionAdvice.Placement(RegionPlacement.Check(grid.CurrentSettings(), x, y, size));
        }
    }

    /// <summary>The sharing sentence for <paramref name="group"/>, naming the region being added.</summary>
    public string ShareText(GroupChoice group) =>
        RegionAdvice.Share(group.Names, string.IsNullOrWhiteSpace(Name) ? "this region" : Name.Trim());
}
