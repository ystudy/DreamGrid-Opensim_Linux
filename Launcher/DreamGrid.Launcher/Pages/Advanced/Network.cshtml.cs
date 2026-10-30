using System.Globalization;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The hostname and the seven ports. The hostname must suit the plan (<see cref="PlanCatalog.AddressError"/>). A save is validated first and goes through IGridController.UpdateSettings
/// (which also rewrites the INIs); nothing is saved while any field is wrong.
/// </summary>
public sealed class NetworkModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    [BindProperty] public string? Hostname { get; set; }

    /// <summary>The port fields as typed, keyed like <see cref="SettingsValidator.PortFields"/>.</summary>
    public Dictionary<string, string?> Ports { get; set; } = new();

    public string DetectedAddress { get; } = HostAddress.Detect();

    /// <summary>A Region Server's one hypergrid sentence (this page has no hypergrid switch for it); null on every other plan.</summary>
    public string? HypergridLine => settings() is { Plan: OwnerPlan.RegionServer } s
        ? $"Hypergrid is {(PlanCatalog.HypergridOn(s) ? "on" : "off")} for this grid; the Grid Asset Server decides."
        : null;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    /// <summary>Set after a save: the extra things the person must know beyond "restart your world".</summary>
    public IReadOnlyList<string>? SavedNotes { get; private set; }

    public void OnGet() => Fill(settings());

    public IActionResult OnPost()
    {
        // Ports post under their own names (WebPort=…), not Ports[WebPort], so the form needs no JavaScript or prefix.
        Ports = SettingsValidator.PortFields.ToDictionary(f => f.Key, f => (string?)Request.Form[f.Key].ToString());
        var host = string.IsNullOrEmpty(Hostname) ? null : Hostname;

        var parsed = new Dictionary<string, int>();
        var errors = new Dictionary<string, string>();
        foreach (var (key, _) in SettingsValidator.PortFields)
        {
            if (int.TryParse(Ports[key], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) parsed[key] = port;
            else errors[key] = $"Enter a whole number from {SettingsValidator.MinPort} to {SettingsValidator.MaxPort}.";
        }
        if (errors.Count == 0)
        {
            var result = SettingsValidator.Network(host, parsed["WebPort"], parsed["RobustPublicPort"], parsed["RobustPrivatePort"],
                parsed["RegionPort"], parsed["MariaDbPort"], parsed["RobustConsolePort"], parsed["RegionConsolePort"],
                PortAllocator.Assigned(settings()));
            foreach (var (k, v) in result.Errors) errors[k] = v;
        }
        // The address must also suit the plan: Simple Public Server needs a public one, Local a network one.
        if (!errors.ContainsKey("Hostname") && PlanCatalog.AddressError(settings().Plan, host) is { } hostError)
            errors["Hostname"] = hostError;
        if (errors.Count > 0)
        {
            Errors = errors;
            return Page();
        }

        var before = settings();
        var saved = grid.UpdateSettings(s => s with
        {
            Hostname = host,
            WebPort = parsed["WebPort"],
            RobustPublicPort = parsed["RobustPublicPort"],
            RobustPrivatePort = parsed["RobustPrivatePort"],
            RegionPort = parsed["RegionPort"],
            MariaDbPort = parsed["MariaDbPort"],
            RobustConsolePort = parsed["RobustConsolePort"],
            RegionConsolePort = parsed["RegionConsolePort"],
        }, writeConfig: true);
        SavedNotes = Notes(before, saved);
        Fill(saved);
        return Page();
    }

    /// <summary>The ports each region holds, for the table below the form.</summary>
    public IReadOnlyList<(string Region, string RunsWith, int Udp, int Web, int Console)> RegionPorts { get; private set; } = [];

    private void Fill(LauncherSettings s)
    {
        var live = RegionModel.Live(s).ToList();
        RegionPorts = live
            .Select(r => (r, p: RegionModel.ProgramOf(s, r)))
            .Where(x => x.p is not null)
            .Select(x => (x.r.Name, string.Join(", ", live.Where(o => o.Instance == x.r.Instance && o.Uuid != x.r.Uuid).Select(o => o.Name)),
                x.r.UdpPort, x.p!.HttpPort, x.p.ConsolePort))
            .ToList();
        Hostname = s.Hostname;
        Ports = new Dictionary<string, string?>
        {
            ["WebPort"] = Text(s.WebPort),
            ["RobustPublicPort"] = Text(s.RobustPublicPort),
            ["RobustPrivatePort"] = Text(s.RobustPrivatePort),
            ["RegionPort"] = Text(s.RegionPort),
            ["MariaDbPort"] = Text(s.MariaDbPort),
            ["RobustConsolePort"] = Text(s.RobustConsolePort),
            ["RegionConsolePort"] = Text(s.RegionConsolePort),
        };
    }

    private static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The launcher listens on the web port it started with, so a changed web port needs the launcher itself restarted.
    /// </summary>
    private List<string> Notes(LauncherSettings before, LauncherSettings after)
    {
        var notes = new List<string>();
        if (before.WebPort != after.WebPort)
            notes.Add($"The web port changed to {after.WebPort}, so this website's address changes too. It moves when you " +
                      "restart the launcher itself (restart the DreamGrid service, or the computer); restarting your world is not " +
                      $"enough. Afterwards open http://{after.Hostname ?? DetectedAddress}:{after.WebPort}/ instead of this address.");
        return notes;
    }
}
