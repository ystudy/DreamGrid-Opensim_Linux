using System.Globalization;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// The setup link lands here: it always continues at the first unfinished step, so a refresh or restart resumes in place.
/// The port form every step shows while a port is taken posts here too (<c>?handler=Port</c>).
/// </summary>
public sealed class IndexModel(IGridController grid, Func<LauncherSettings> settings, BackgroundWork background, ILogger<IndexModel> log) : PageModel
{
    [BindProperty] public string? Port { get; set; }

    /// <summary>The current checklist step (1-4), for the page shown when a port change is refused.</summary>
    public int Step { get; private set; }

    public IActionResult OnGet() => Redirect(SetupSteps.FirstUnfinished(settings().Setup));

    /// <summary>
    /// Moves the port the last start failed on (its setting is found by value; the web port is the site being served,
    /// so it is never the one) and starts the grid again in the background.
    /// </summary>
    public IActionResult OnPostPort()
    {
        var current = settings();
        var next = SetupSteps.FirstUnfinished(current.Setup);
        var status = grid.GetStatus();
        var taken = StatusApi.ShowsStartFailure(status) ? status.StartFailure!.ConflictPort : null;
        var key = taken is { } t
            ? SettingsValidator.PortFields.Select(f => f.Key).FirstOrDefault(k => k != "WebPort" && PortOf(current, k) == t)
            : null;
        if (key is null) return Redirect(next); // nothing to change any more

        if (Validate(current, key) is { } error)
        {
            ViewData["PortError"] = error;
            ViewData["PortValue"] = Port;
            Step = Array.FindIndex([.. SetupSteps.All], s => s.Path == next) + 1;
            return Page();
        }

        var port = int.Parse(Port!, NumberStyles.None, CultureInfo.InvariantCulture);
        grid.UpdateSettings(s => WithPort(s, key, port), writeConfig: true);
        log.LogInformation("Setup: {Key} changed from {Old} to {New}; starting the grid again.", key, taken, port);
        background.Run("setup start after port change", async ct =>
        {
            try
            {
                await grid.StartAsync(ct);
            }
            catch (PortConflictException e)
            {
                log.LogCritical("{Message}", e.Message);
            }
        });
        return Redirect(next);
    }

    /// <summary>Plain words for a newcomer; null when <see cref="Port"/> is fine for <paramref name="key"/>.</summary>
    private string? Validate(LauncherSettings current, string key)
    {
        const string Whole = "Enter a whole number from 1024 to 65535.";
        if (!int.TryParse(Port?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return Whole;
        Port = Port!.Trim();
        var s = WithPort(current, key, port);
        var result = SettingsValidator.Network(s.Hostname, s.WebPort, s.RobustPublicPort, s.RobustPrivatePort, s.RegionPort,
            s.MariaDbPort, s.RobustConsolePort, s.RegionConsolePort);
        if (!result.Errors.TryGetValue(key, out var message)) return null;
        // The validator names the other ports by their technical labels.
        return port is < SettingsValidator.MinPort or > SettingsValidator.MaxPort
            ? message
            : $"Port {port} is already used by another part of DreamGrid. Pick a different number.";
    }

    private static int PortOf(LauncherSettings s, string key) => key switch
    {
        "WebPort" => s.WebPort,
        "RobustPublicPort" => s.RobustPublicPort,
        "RobustPrivatePort" => s.RobustPrivatePort,
        "RegionPort" => s.RegionPort,
        "MariaDbPort" => s.MariaDbPort,
        "RobustConsolePort" => s.RobustConsolePort,
        "RegionConsolePort" => s.RegionConsolePort,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static LauncherSettings WithPort(LauncherSettings s, string key, int port) => key switch
    {
        "RobustPublicPort" => s with { RobustPublicPort = port },
        "RobustPrivatePort" => s with { RobustPrivatePort = port },
        "RegionPort" => s with { RegionPort = port },
        "MariaDbPort" => s with { MariaDbPort = port },
        "RobustConsolePort" => s with { RobustConsolePort = port },
        "RegionConsolePort" => s with { RegionConsolePort = port },
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
