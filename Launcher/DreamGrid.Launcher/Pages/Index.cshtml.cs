using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The dashboard (spec 6.4): status, controls, who's online, the address to connect to, and each module's
/// <see cref="IDashboardSwitch"/> (spec 6.9).
/// </summary>
public sealed class IndexModel(IGridController grid, IOnlineUsers online, GridActions actions, LogReader logs, Func<LauncherSettings> settings,
    IEnumerable<IDashboardSwitch> switches, DreamGrid.Launcher.Email.EmailSender email) : PageModel
{
    /// <summary>
    /// Email's warnings (empty where there is no guest side). The owner sees each one and a link to the Email page; other staff
    /// only that email has a problem, with no account details.
    /// </summary>
    public IReadOnlyList<string> EmailWarnings { get; private set; } = [];

    public bool IsOwner => User.IsInRole(DreamGrid.Launcher.Security.AuthSchemes.OwnerRole);

    private const int DetailLines = 20;

    public StatusView Status { get; private set; } = null!;

    /// <summary>For the "Show details" box: the raw start failure and the last log lines of each failed component.</summary>
    public IReadOnlyList<(string Label, IReadOnlyList<string> Lines)> FailedLogs { get; private set; } = [];

    /// <summary>False on a Region Server, whose grid service is the Grid Asset Server on another computer: there is nothing to restart.</summary>
    public bool RunsRobust { get; private set; } = true;

    [TempData] public string? Notice { get; set; }

    /// <summary>The features area; empty (and not shown) while no module has a switch.</summary>
    public IReadOnlyList<(IDashboardSwitch Switch, bool On)> Switches { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var raw = grid.GetStatus();
        RunsRobust = raw.RunsRobust;
        if (AdminRule.HasStaffPages(settings())) EmailWarnings = email.Status().Warnings;
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
