using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One item of a region's power menu: the action as posted, its label and what it does.</summary>
public sealed record PowerItem(string Action, string Label, string Help);

/// <summary>
/// One region's info panel (spec 5.4): state, who's here, running time and memory, place on the map, how it runs and its
/// power menu. <c>?partial=1</c> renders only the fragment, which the map puts in its side panel.
/// </summary>
public sealed class RegionPanelModel(IGridController grid, IOnlineUsers online, RegionActions actions, LogReader logs) : PageModel
{
    private const int LogLines = 40;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public RegionState State { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Size { get; private set; }
    public bool IsPartial { get; private set; }

    /// <summary>The failed program's message (raw; this page is admin-only) and its last log lines; empty unless the region failed.</summary>
    public string? FailureMessage { get; private set; }
    public IReadOnlyList<string> FailureLog { get; private set; } = [];

    /// <summary>Names of the people here; null when the list is unavailable.</summary>
    public IReadOnlyList<string>? Here { get; private set; }

    /// <summary>Running time and memory of the region's program; null while it isn't running.</summary>
    public string? RunningTime { get; private set; }
    public string? Memory { get; private set; }

    /// <summary>The other regions that share this region's program.</summary>
    public IReadOnlyList<string> RunsWith { get; private set; } = [];

    /// <summary>The neighbours, each with the side of this region it lies on.</summary>
    public IReadOnlyList<Neighbour> Neighbours { get; private set; } = [];
    public IReadOnlyList<PowerItem> Menu { get; private set; } = [];

    /// <summary>Set when a power post needs a yes first: the action, the question and the yes button.</summary>
    public RegionPower? Asking { get; private set; }
    public string? AskingText { get; private set; }
    public string? AskingYes { get; private set; }

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        IsPartial = Request.Query["partial"] == "1";
        if (!await LoadAsync(id)) return NotFound();
        return Page();
    }

    /// <summary>
    /// A power post. When the action also restarts other regions and the admin hasn't said yes, the confirmation shows
    /// instead; otherwise it runs and the page comes back with the notice.
    /// </summary>
    public async Task<IActionResult> OnPostPowerAsync(Guid id, string? action, bool confirmed)
    {
        if (!Enum.TryParse<RegionPower>(action, ignoreCase: true, out var power) || !Enum.IsDefined(power)) return BadRequest();
        if (!await LoadAsync(id)) return NotFound();

        var affected = Affected(id, power);
        if (affected.Count > 0 && !confirmed)
        {
            Asking = power;
            AskingText = $"{Verb(power)} {Name} also restarts {RegionAdvice.Join(affected)}, and drops everyone in {(affected.Count == 1 ? "it" : "them")}. Go ahead?";
            AskingYes = "Yes, " + YesWord(power);
            return Page();
        }
        if (!actions.TryRun(id, power)) return NotFound();
        Notice = RegionActions.Notice(power, Name);
        return Redirect("/regions/region/" + id);
    }

    private async Task<bool> LoadAsync(Guid id)
    {
        var settings = grid.CurrentSettings();
        if (Core.Settings.RegionModel.Find(settings, id) is not { DeletedAt: null } r) return false;
        var status = grid.GetStatus();
        // A live region the status does not know yet counts as stopped (or parked).
        var live = status.Regions.FirstOrDefault(x => x.Id == id);
        var program = status.Programs.FirstOrDefault(p => p.Instance == r.Instance);

        Id = id;
        Name = r.Name;
        (X, Y, Size) = (r.X, r.Y, r.Size);
        State = live?.State ?? (r.Parked ? RegionState.Parked : RegionState.Stopped);
        StateText = StatusApi.RegionStateText(State);
        RunsWith = live?.RunsWith ?? [];
        // Check gives the side of the neighbour that touches this region; the panel says where the neighbour lies.
        Neighbours = RegionPlacement.Check(settings, r.X, r.Y, r.Size, ignore: r.Uuid).Neighbours
            .Select(n => n with { Side = Opposite(n.Side) }).ToList();

        if (program is { Process.State: ProcessState.Running } running)
        {
            if (running.Process.Uptime is { } up) RunningTime = StatusApi.Uptime(up);
            if (running.Process.MemoryBytes is { } bytes) Memory = RegionAdvice.Size(bytes);
        }
        if (State == RegionState.Failed)
        {
            FailureMessage = program?.Process.LastError;
            FailureLog = FailedLog(r.Instance);
        }

        var people = await online.ListAsync(HttpContext.RequestAborted);
        Here = people.Available ? people.Avatars.Where(a => a.RegionId == id).Select(a => a.Name).ToList() : null;
        Menu = MenuFor(State);
        return true;
    }

    /// <summary>
    /// The other regions an action restarts or drops: those sharing this region's program that are running or starting,
    /// while that program is up. Unpark changes nothing that runs. (The controller's held set is its own; the page asks
    /// whenever the program is up and others run in it.)
    /// </summary>
    private IReadOnlyList<string> Affected(Guid id, RegionPower action)
    {
        if (action == RegionPower.Unpark) return [];
        return RegionActions.SharingProgram(grid.GetStatus(), id);
    }

    private IReadOnlyList<string> FailedLog(string instance)
    {
        try
        {
            return logs.Tail("region-" + instance, LogLines).Lines;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return []; // the region was removed meanwhile, or its log cannot be read
        }
    }

    private static IReadOnlyList<PowerItem> MenuFor(RegionState state)
    {
        var restart = new PowerItem("Restart", "Restart", "Stops it and starts it again.");
        var stop = new PowerItem("Stop", "Stop", "Goes offline until the next time your world starts.");
        var start = new PowerItem("Start", "Start", "Brings it online.");
        var park = new PowerItem("Park", "Park", "Goes offline and stays offline, even when your world starts.");
        return state switch
        {
            RegionState.Running or RegionState.Starting => [restart, stop, park],
            RegionState.Parked =>
            [
                new PowerItem("UnparkAndStart", "Unpark and start", "Brings it online, and it starts with your world again."),
                new PowerItem("Unpark", "Unpark", "It starts with your world again but stays offline for now."),
            ],
            // Stopped or failed (and stopping, which settles into stopped).
            _ => [start, park],
        };
    }

    private static string Verb(RegionPower action) => action switch
    {
        RegionPower.Restart => "Restarting",
        RegionPower.Stop => "Stopping",
        RegionPower.Park => "Parking",
        _ => "Starting",
    };

    private static string YesWord(RegionPower action) => action switch
    {
        RegionPower.Restart => "restart",
        RegionPower.Stop => "stop",
        RegionPower.Park => "park",
        _ => "start",
    };

    private static string Opposite(string side) => side switch
    {
        "east" => "west",
        "west" => "east",
        "north" => "south",
        _ => "north",
    };
}
