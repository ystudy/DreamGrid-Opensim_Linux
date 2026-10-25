using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>The dashboard (spec 6.4): status, controls, who's online and the address to connect to.</summary>
public sealed class IndexModel(IGridController grid, IOnlineUsers online, GridActions actions, LogReader logs, Func<LauncherSettings> settings)
    : PageModel
{
    private const int DetailLines = 20;

    public StatusView Status { get; private set; } = null!;

    /// <summary>For the "Show details" box: the raw start failure and the last log lines of each failed component.</summary>
    public IReadOnlyList<(string Label, IReadOnlyList<string> Lines)> FailedLogs { get; private set; } = [];

    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync()
    {
        var raw = grid.GetStatus();
        Status = await StatusApi.BuildAsync(grid, online, withOnline: true, HttpContext.RequestAborted);
        var g = Status.Grid;
        var details = new List<(string Label, IReadOnlyList<string> Lines)>();
        // The raw start message may name paths: only this admin page shows it, never /api/status. A port conflict's
        // plain text already says everything (its raw message uses internal port names).
        if (StatusApi.ShowsStartFailure(raw) && raw.StartFailure!.ConflictPort is null)
            details.Add(("Why the last start failed", [raw.StartFailure!.Message]));
        details.AddRange(new[] { g.Database, g.Robust, g.Region }
            .Where(c => c?.State == nameof(Core.Processes.ProcessState.Failed))
            .Select(c => (c!.Label + ": the last lines it wrote", LastLines(c.Name)))
            .Where(f => f.Item2.Count > 0));
        FailedLogs = details;
    }

    /// <summary>The controls without JavaScript: the action runs in the background and the page comes back at once.</summary>
    public IActionResult OnPostRun(string? gridAction)
    {
        if (gridAction is not null && actions.TryRun(gridAction))
            Notice = GridActions.Notices[gridAction];
        return Redirect("/");
    }

    private IReadOnlyList<string> LastLines(string component)
    {
        var source = component switch
        {
            "database" => "database",
            "robust" => "robust",
            _ => settings().Region is { } r ? "region-" + r.Instance : null,
        };
        if (source is null) return [];
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
