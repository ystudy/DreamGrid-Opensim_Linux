using System.Globalization;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>
/// A region's settings (spec 5.4), one tab per group: Basics, Options, Map, Physics, Scripts, Permissions, Access and CPU.
/// Each tab is one plain form posting <c>?handler=&lt;tab&gt;</c>. A wrong value comes back at its field and nothing is
/// saved. Options belong to the region; the others belong to the program the region runs in, so every region in it shares them.
/// </summary>
public sealed class SettingsModel(IGridController grid, Func<CpuPriority, string?> priorityUnavailable) : PageModel
{
    public static readonly IReadOnlyList<(string Key, string Label)> Tabs =
    [
        ("basics", "Basics"), ("options", "Options"), ("map", "Map"), ("physics", "Physics"),
        ("scripts", "Scripts"), ("permissions", "Permissions"), ("access", "Access"), ("cpu", "CPU"),
    ];

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Tab { get; private set; } = "basics";
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Size { get; private set; }
    public string Instance { get; private set; } = "";

    /// <summary>The live regions that run in this region's program, this one included.</summary>
    public IReadOnlyList<string> Sharing { get; private set; } = [];

    public int Cores { get; } = Environment.ProcessorCount;
    public string DefaultPhysics { get; private set; } = "ubODE";
    public bool DefaultScripts { get; private set; } = true;

    // Fields as shown (typed text, so a wrong value comes back as it was typed).
    public string? MaxAgents { get; private set; }
    public string? MaxPrims { get; private set; }
    public string? MaxNonPhysicalSize { get; private set; }
    public string? MaxPhysicalSize { get; private set; }
    public bool ClampSize { get; private set; }
    public bool KeepResidentsOut { get; private set; }
    public bool AnnounceVisitors { get; private set; }
    /// <summary>"" (the grid default), "ubODE", "BulletSim" or "BulletSimThreaded".</summary>
    public string Physics { get; private set; } = "";
    /// <summary>"default", "on" or "off".</summary>
    public string Scripts { get; private set; } = "default";
    public string? Timer { get; private set; }
    public string? Fps { get; private set; }
    public MapQuality Map { get; private set; }
    public bool AllowGods { get; private set; }
    public bool OwnerIsGod { get; private set; }
    public bool ManagerIsGod { get; private set; }
    public IReadOnlyList<int> PickedCores { get; private set; } = [];
    public CpuPriority Priority { get; private set; }

    [TempData] public string? Notice { get; set; }

    private LauncherSettings _settings = null!;
    private RegionSettings _region = null!;
    private ProgramSettings _program = null!;

    public string Error(string key) => ModelState.TryGetValue(key, out var e) && e.Errors.Count > 0 ? e.Errors[0].ErrorMessage : "";

    /// <summary>Why a priority can't be chosen here, or null.</summary>
    public string? Unavailable(CpuPriority p) => priorityUnavailable(p);

    public IActionResult OnGet(Guid id, string? tab)
    {
        if (!Load(id, tab)) return NotFound();
        FillFromSettings();
        return Page();
    }

    public Task<IActionResult> OnPostOptionsAsync(Guid id) => SaveAsync(id, "options");
    public Task<IActionResult> OnPostMapAsync(Guid id) => SaveAsync(id, "map");
    public Task<IActionResult> OnPostPhysicsAsync(Guid id) => SaveAsync(id, "physics");
    public Task<IActionResult> OnPostScriptsAsync(Guid id) => SaveAsync(id, "scripts");
    public Task<IActionResult> OnPostPermissionsAsync(Guid id) => SaveAsync(id, "permissions");
    public Task<IActionResult> OnPostAccessAsync(Guid id) => SaveAsync(id, "access");
    public Task<IActionResult> OnPostCpuAsync(Guid id) => SaveAsync(id, "cpu");

    private async Task<IActionResult> SaveAsync(Guid id, string tab)
    {
        if (!Load(id, tab)) return NotFound();
        ModelState.Clear();
        FillFromSettings();

        var ct = HttpContext.RequestAborted;
        var current = _region.Options ?? new RegionOptions();
        var opts = _program.Options ?? new ProgramOptions();
        string? physics = _program.Physics;
        var applied = false;
        try
        {
            if (tab == "options")
            {
                MaxAgents = Form("MaxAgents"); MaxPrims = Form("MaxPrims");
                MaxNonPhysicalSize = Form("MaxNonPhysicalSize"); MaxPhysicalSize = Form("MaxPhysicalSize");
                ClampSize = Checked("ClampSize");
                var next = current with
                {
                    MaxAgents = Number(nameof(MaxAgents), MaxAgents), MaxPrims = Number(nameof(MaxPrims), MaxPrims),
                    MaxNonPhysicalSize = Number(nameof(MaxNonPhysicalSize), MaxNonPhysicalSize),
                    MaxPhysicalSize = Number(nameof(MaxPhysicalSize), MaxPhysicalSize),
                    ClampSize = ClampSize,
                };
                AddErrors(RegionSettingsRules.Options(next));
                if (!ModelState.IsValid) return Page();
                await grid.SaveRegionOptionsAsync(id, next, ct);
            }
            else if (tab == "access")
            {
                KeepResidentsOut = Checked("KeepResidentsOut");
                AnnounceVisitors = Checked("AnnounceVisitors");
                await grid.SaveRegionOptionsAsync(id, current with { KeepResidentsOut = KeepResidentsOut, AnnounceVisitors = AnnounceVisitors }, ct);
            }
            else
            {
                switch (tab)
                {
                    case "map":
                        Map = Enum.TryParse<MapQuality>(Form("Map"), out var m) && Enum.IsDefined(m) ? m : opts.Map;
                        opts = opts with { Map = Map };
                        break;
                    case "physics":
                        Physics = Form("Physics") ?? "";
                        physics = Physics == "" ? null : Physics;
                        break;
                    case "scripts":
                        Scripts = Form("Scripts") is "on" or "off" ? Form("Scripts")! : "default";
                        Timer = Form("Timer"); Fps = Form("Fps");
                        opts = opts with
                        {
                            Scripts = Scripts == "default" ? null : Scripts == "on",
                            MinTimerInterval = Seconds(nameof(ProgramOptions.MinTimerInterval), Timer),
                            FramesPerSecond = Number(nameof(ProgramOptions.FramesPerSecond), Fps),
                        };
                        break;
                    case "permissions":
                        (AllowGods, OwnerIsGod, ManagerIsGod) = (Checked("AllowGods"), Checked("OwnerIsGod"), Checked("ManagerIsGod"));
                        opts = opts with { AllowGods = AllowGods, OwnerIsGod = OwnerIsGod, ManagerIsGod = ManagerIsGod };
                        break;
                    case "cpu":
                        var picked = new List<int>();
                        foreach (var c in Request.Form["Cores"])
                        {
                            if (int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) picked.Add(n);
                            else ModelState.AddModelError(nameof(ProgramOptions.Cores), "Pick cores from the list.");
                        }
                        // Every core ticked is the same as none: it must not pin the cores when the settings move to another computer.
                        if (picked.Count == Cores && picked.Distinct().Count() == Cores && picked.All(c => c >= 0 && c < Cores)) picked = [];
                        PickedCores = picked;
                        if (Form("Priority") is { } p)
                        {
                            if (Enum.TryParse<CpuPriority>(p, out var pr) && Enum.IsDefined(pr))
                            {
                                Priority = pr;
                                if (priorityUnavailable(pr) is { } why && pr != opts.Priority)
                                    ModelState.AddModelError(nameof(ProgramOptions.Priority), why);
                            }
                            else ModelState.AddModelError(nameof(ProgramOptions.Priority), "Pick one of the priorities.");
                        }
                        else Priority = opts.Priority; // a disabled choice posts nothing: keep what it was
                        opts = opts with { Cores = picked, Priority = Priority };
                        break;
                }
                AddErrors(RegionSettingsRules.Program(physics, opts, Cores));
                if (!ModelState.IsValid) return Page();

                try
                {
                    applied = await grid.SaveProgramOptionsAsync(Instance, physics, opts, ct);
                }
                catch (InvalidOperationException e) when (tab == "cpu")
                {
                    var reason = e.Message.TrimEnd();
                    if (reason.Length > 0 && reason[^1] is not ('.' or '!' or '?')) reason += ".";
                    Notice = $"Saved, but it couldn't be applied now: {reason} {Applies()}";
                    return Redirect($"/regions/region/{id}/settings/{tab}");
                }
            }
        }
        catch (ArgumentException e)
        {
            ModelState.AddModelError("", e.Message);
            return Page();
        }
        catch (InvalidOperationException)
        {
            return NotFound(); // the region was deleted meanwhile
        }

        Notice = applied ? "Saved and applied." : $"Saved. {Applies(allowStart: tab == "cpu")}";
        return Redirect($"/regions/region/{id}/settings/{tab}");
    }

    private bool Load(Guid id, string? tab)
    {
        tab = string.IsNullOrEmpty(tab) ? "basics" : tab;
        if (!Tabs.Any(t => t.Key == tab)) return false;
        _settings = grid.CurrentSettings();
        if (RegionModel.Find(_settings, id) is not { DeletedAt: null } r) return false;
        if (RegionModel.ProgramOf(_settings, r) is not { } program) return false;
        (_region, _program) = (r, program);
        (Id, Tab, Name, Instance) = (id, tab, r.Name, r.Instance);
        (X, Y, Size) = (r.X, r.Y, r.Size);
        Sharing = RegionModel.In(_settings, r.Instance).Select(x => x.Name).ToList();
        (DefaultPhysics, DefaultScripts) = (_settings.DefaultPhysics, _settings.DefaultScripts);
        return true;
    }

    private void FillFromSettings()
    {
        var o = _region.Options ?? new RegionOptions();
        (MaxAgents, MaxPrims) = (Text(o.MaxAgents), Text(o.MaxPrims));
        (MaxNonPhysicalSize, MaxPhysicalSize) = (Text(o.MaxNonPhysicalSize), Text(o.MaxPhysicalSize));
        (ClampSize, KeepResidentsOut, AnnounceVisitors) = (o.ClampSize, o.KeepResidentsOut, o.AnnounceVisitors);
        var p = _program.Options ?? new ProgramOptions();
        Physics = _program.Physics ?? "";
        Scripts = p.Scripts is null ? "default" : p.Scripts.Value ? "on" : "off";
        Timer = p.MinTimerInterval?.ToString(CultureInfo.InvariantCulture);
        Fps = Text(p.FramesPerSecond);
        (Map, AllowGods, OwnerIsGod, ManagerIsGod) = (p.Map, p.AllowGods, p.OwnerIsGod, p.ManagerIsGod);
        PickedCores = RegionSettingsRules.UsableCores(p.Cores, Cores);
        Priority = p.Priority;
    }

    /// <summary>"It applies when Home and Beach restart." while their program runs, else "...start."</summary>
    private string Applies(bool allowStart = false)
    {
        var running = !allowStart || grid.GetStatus().Programs.FirstOrDefault(p => p.Instance == Instance)?.Process.State == ProcessState.Running;
        var names = RegionModel.In(grid.CurrentSettings(), Instance).Select(r => r.Name).ToList();
        return $"It applies when {RegionAdvice.Join(names)} {Verb(names.Count, running)}.";
    }

    /// <summary>The verb for a list of regions: "restarts" for one, "restart" for several (or "starts"/"start").</summary>
    public static string Verb(int count, bool running) => (running ? "restart" : "start") + (count == 1 ? "s" : "");

    private void AddErrors(ValidationResult result)
    {
        foreach (var (key, message) in result.Errors)
            if (!ModelState.TryGetValue(key, out var e) || e.Errors.Count == 0) ModelState.AddModelError(key, message);
    }

    private string? Form(string name) => Request.Form.TryGetValue(name, out var v) && v.Count > 0 ? v[0]!.Trim() : null;

    private bool Checked(string name) => Request.Form.TryGetValue(name, out var v) && v.Count > 0;

    private int? Number(string key, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        ModelState.AddModelError(key, "Use a whole number, or leave it empty for OpenSim's default.");
        return null;
    }

    private double? Seconds(string key, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return d;
        ModelState.AddModelError(key, "Use a number of seconds, or leave it empty for OpenSim's default.");
        return null;
    }

    private static string? Text(int? n) => n?.ToString(CultureInfo.InvariantCulture);
}
