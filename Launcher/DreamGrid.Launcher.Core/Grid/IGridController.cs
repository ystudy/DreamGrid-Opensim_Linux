using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The public surface of <see cref="GridController"/>, so the host can use a test double.</summary>
public interface IGridController
{
    /// <summary>Stages, writes the INIs, checks ports, then starts (or adopts) MariaDB, Robust and the region.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops region, Robust, then MariaDB.</summary>
    Task StopAllAsync(CancellationToken ct);

    /// <summary>
    /// Stops monitoring; every child keeps running (spec 4.12). Detaching is final: monitoring is not resumed
    /// afterwards, so a later start or restart on this instance leaves its children unsupervised.
    /// </summary>
    Task DetachAsync();

    Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct);

    /// <summary>
    /// <paramref name="content"/> is "starter" or "island". <paramref name="progress"/> hears the plain-language stage
    /// as each real step begins: "Creating your region", "Loading the region content",
    /// "Waiting for the region to accept logins".
    /// </summary>
    Task CreateRegionAsync(string name, string content, CancellationToken ct, IProgress<string>? progress = null);

    GridStatus GetStatus();

    /// <summary>
    /// The world's Start (spec §6.8): clears stopped regions; with <paramref name="everything"/> unparks every region; then
    /// starts what isn't running and restarts each running program that doesn't load all its regions.
    /// </summary>
    Task StartWorldAsync(bool everything, CancellationToken ct);

    /// <summary>Validates, saves and starts a new region, loading its content; returns the saved region.</summary>
    Task<RegionSettings> AddRegionAsync(NewRegion request, CancellationToken ct, IProgress<string>? progress = null);

    /// <summary>A region's power menu action; other regions in its program may restart (they are logged).</summary>
    Task RegionPowerAsync(Guid regionId, RegionPower action, CancellationToken ct);

    /// <summary>The controller's cached settings (what the grid was configured from); the host reads these per request.</summary>
    LauncherSettings CurrentSettings();

    /// <summary><paramref name="name"/> is "database" or "robust"; regions use <see cref="RegionPowerAsync"/>.</summary>
    Task RestartComponentAsync(string name, CancellationToken ct);

    /// <summary>
    /// Starts (or adopts) one component: "database" or "robust". Robust needs the database running; otherwise it
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task StartComponentAsync(string name, CancellationToken ct);

    /// <summary>Stops one component; stopping the database stops the regions and Robust first.</summary>
    Task StopComponentAsync(string name, CancellationToken ct);

    /// <summary>
    /// Reloads the settings and rewrites the INIs without restarting anything. Running children keep the ports
    /// they started with; a port applies at that component's next start or restart. The launcher's own calls to
    /// Robust (IRobustClient) follow the Robust ports at once; only the web port needs a launcher restart.
    /// </summary>
    void RegenerateConfig();

    void OnRegionAlert(RegionAlert alert);

    /// <summary>
    /// Applies <paramref name="change"/> to the latest saved settings, saves them and (by default) rewrites the INIs,
    /// all under the short config lock, and returns the new settings. Every settings change goes through here: a
    /// long operation saves only its own fields this way, so a change made meanwhile is never lost. Plan 2b's
    /// Advanced Settings must use it too, never <see cref="SettingsStore.Save"/> directly.
    /// </summary>
    LauncherSettings UpdateSettings(Func<LauncherSettings, LauncherSettings> change, bool writeConfig = true);
}
