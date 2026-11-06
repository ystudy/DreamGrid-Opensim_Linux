using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The dashboard (spec 6.4): status, controls, who's online, the address to connect to, and each module's
/// <see cref="IDashboardSwitch"/> (spec 6.9).
/// </summary>
public sealed class IndexModel(IGridController grid, IOnlineUsers online, GridActions actions, LogReader logs, Func<LauncherSettings> settings,
    IEnumerable<IDashboardSwitch> switches, DreamGrid.Launcher.Email.EmailSender email, SignUpBoard board, TimeProvider time,
    DreamGrid.Launcher.Dashboard.DashboardSnapshot dashboard) : PageModel
{
    /// <summary>
    /// Email's warnings (empty where there is no guest side). The owner sees each one and a link to the Email page; other staff
    /// only that email has a problem, with no account details.
    /// </summary>
    public IReadOnlyList<string> EmailWarnings { get; private set; } = [];

    public bool IsOwner => User.IsInRole(DreamGrid.Launcher.Security.AuthSchemes.OwnerRole);

    /// <summary>The New sign-ups card (spec 2026-11-01-sign-up-approval-design §7); null where there is no guest side.</summary>
    public SignUpRowsView? SignUps { get; private set; }

    public int SignUpsWaiting { get; private set; }

    private const int DetailLines = 20;

    public StatusView Status { get; private set; } = null!;

    /// <summary>For the "Show details" box: the raw start failure and the last log lines of each failed component.</summary>
    public IReadOnlyList<(string Label, IReadOnlyList<string> Lines)> FailedLogs { get; private set; } = [];

    /// <summary>False on a Region Server, whose grid service is the Grid Asset Server on another computer: there is nothing to restart.</summary>
    public bool RunsRobust { get; private set; } = true;

    [TempData] public string? Notice { get; set; }

    /// <summary>The numbers (design 2026-11-05-dashboard), at most a minute old.</summary>
    public DreamGrid.Launcher.Dashboard.DashboardView Dashboard { get; private set; } = null!;

    /// <summary>Where this computer runs the grid: the residents, land and profile numbers, and the People pages, exist.</summary>
    public bool GridHere { get; private set; }

    /// <summary>A Grid Asset Server: Region Servers keep their own regions' objects and parcels, so those counts are this computer's only.</summary>
    public bool ThisComputerOnly { get; private set; }

    /// <summary>A number as the dashboard writes it ("12,480"), or "—" when it couldn't be counted.</summary>
    public static string N(long? n) => n is { } v ? v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>A check's pill class: green Working, yellow Slow or Check this, red Down.</summary>
    public static string CheckClass(DreamGrid.Launcher.Dashboard.ServiceState s) => s switch
    {
        DreamGrid.Launcher.Dashboard.ServiceState.Working => "state-running",
        DreamGrid.Launcher.Dashboard.ServiceState.Down => "state-failed",
        _ => "state-starting",
    };

    public static string CheckText(DreamGrid.Launcher.Dashboard.ServiceState s) => s switch
    {
        DreamGrid.Launcher.Dashboard.ServiceState.Working => "Working",
        DreamGrid.Launcher.Dashboard.ServiceState.Slow => "Slow",
        DreamGrid.Launcher.Dashboard.ServiceState.CheckThis => "Check this",
        _ => "Down",
    };

    /// <summary>Square metres as square kilometres with one decimal, or "—".</summary>
    public static string Km2(long? m2) => m2 is { } v ? (v / 1_000_000.0).ToString("N1", System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>The features area; empty (and not shown) while no module has a switch.</summary>
    public IReadOnlyList<(IDashboardSwitch Switch, bool On)> Switches { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var raw = grid.GetStatus();
        RunsRobust = raw.RunsRobust;
        if (AdminRule.HasStaffPages(settings()))
        {
            EmailWarnings = email.Status().Warnings;
            var shown = board.Shown();
            SignUpsWaiting = shown.Count(e => e.Waiting);
            SignUps = new(SignUpRows.Default(shown).Take(SignUpRows.CardRows).ToList(),
                ApprovalRules.CanApprove(DreamGrid.Launcher.Security.StaffSignIn.RoleOf(User), settings()), "/", time.GetUtcNow());
        }
        Status = await StatusApi.BuildAsync(grid, online, withOnline: true, HttpContext.RequestAborted, settings());
        var g = Status.Grid;
        var details = new List<(string Label, IReadOnlyList<string> Lines)>();
        // The raw start message may name paths: only this admin page shows it, never /api/status. A port conflict's
        // plain text already says everything (its raw message uses internal port names).
        if (StatusApi.ShowsStartFailure(raw) && raw.StartFailure!.ConflictPort is null)
            details.Add(("Why the last start failed", [raw.StartFailure!.Message]));
        details.AddRange(new[] { g.Database, g.Robust }
            // A Region Server has no Robust log for the Grid Asset Server row: it runs on another computer.
            .Where(c => c.State == nameof(Core.Processes.ProcessState.Failed) && (raw.RunsRobust || c.Name != "robust"))
            .Select(c => (c.Label + ": the last lines it wrote", LastLines(c.Name)))
            .Where(f => f.Item2.Count > 0));
        // A failed region: its program's log (regions sharing a program share it).
        details.AddRange(raw.Regions.Where(r => r.State == RegionState.Failed)
            .DistinctBy(r => r.Instance)
            .Select(r => (r.Name + ": the last lines it wrote", LastLines("region-" + r.Instance)))
            .Where(f => f.Item2.Count > 0));
        FailedLogs = details;
        var s = settings();
        Switches = switches.Select(sw => (sw, sw.IsOn(s))).ToList();
        GridHere = AdminRule.HasStaffPages(s);
        ThisComputerOnly = s.Plan == OwnerPlan.GridAsset;
        try
        {
            Dashboard = await dashboard.CurrentAsync(HttpContext.RequestAborted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The numbers are an extra: never an error page because of them.
            Dashboard = new(Core.Dashboard.WorldCounts.Unknown, time.GetUtcNow(), [], time.GetUtcNow());
        }
    }

    /// <summary>The controls without JavaScript: the action runs in the background and the page comes back at once.</summary>
    public IActionResult OnPostRun(string? gridAction)
    {
        if (gridAction is not null && actions.TryRun(gridAction))
            Notice = GridActions.NoticeFor(gridAction, settings());
        return Redirect("/");
    }

    /// <summary>A module's on/off switch: saved (and the INIs rewritten) through UpdateSettings.</summary>
    public IActionResult OnPostSwitch(string? moduleId, bool on)
    {
        if (switches.FirstOrDefault(sw => sw.ModuleId == moduleId) is not { } sw) return NotFound();
        grid.UpdateSettings(s => sw.Toggle(s, on), writeConfig: true);
        Notice = $"{sw.Label} is now {(on ? "on" : "off")}. Restart your world to use it.";
        return Redirect("/");
    }

    private IReadOnlyList<string> LastLines(string component)
    {
        var source = component;
        try
        {
            return logs.Tail(source, DetailLines).Lines;
        }
        catch (ArgumentException)
        {
            return []; // the region was removed meanwhile
        }
    }
}
