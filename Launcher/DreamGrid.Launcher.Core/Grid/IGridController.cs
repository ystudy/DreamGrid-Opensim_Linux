using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The public surface of <see cref="GridController"/>, so the host can use a test double.</summary>
public interface IGridController
{
    /// <summary>
    /// Stages, writes the INIs, checks ports, then starts (or adopts) MariaDB, Robust and the region. A Region Server or a
    /// Connected Server never starts Robust: it reads the other grid's information (the Grid Asset Server's join
    /// information, the other grid's get_grid_info) again instead, and throws a <see cref="GridUnreachableException"/>
    /// (starting no region) when that grid doesn't answer.
    /// </summary>
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
    /// Makes the avatar called <paramref name="first"/> <paramref name="last"/> on the grid the estate owner, without a
    /// password: the grid service looks the name up, and the account's own spelling is saved as the owner. Throws
    /// <see cref="ArgumentException"/> in plain words when the grid has no such avatar, and
    /// <see cref="GridUnreachableException"/> on a Region Server or a Connected Server whose other grid doesn't answer.
    /// </summary>
    Task<RobustAccount> SetEstateOwnerAsync(string first, string last, CancellationToken ct);

    /// <summary>
    /// On a plan that runs Robust, how many regions on its map are not this computer's: the regions other computers joined
    /// to this grid. 0 on a plan that doesn't run Robust. Throws <see cref="InvalidOperationException"/> when Robust
    /// doesn't answer.
    /// </summary>
    Task<int> JoinedRegionCountAsync(CancellationToken ct);

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

    /// <summary>
    /// Moves a region to (<paramref name="x"/>, <paramref name="y"/>) and saves it. Throws <see cref="ArgumentException"/> with the
    /// reason in plain words when the spot is off the map or taken, and <see cref="InvalidOperationException"/> for a missing or
    /// deleted region. A running program that loads the region restarts (the others in it are logged); otherwise the new spot
    /// applies at the next start.
    /// </summary>
    Task MoveRegionAsync(Guid regionId, int x, int y, CancellationToken ct);

    /// <summary>
    /// Changes how a region runs, at once: with the program named <paramref name="shareWith"/>, or in a new program of its
    /// own when it is null (with the old program's physics). Throws <see cref="ArgumentException"/> for an unknown group,
    /// its own group, or "on its own" when it already is; <see cref="InvalidOperationException"/> for a missing or deleted
    /// region. The old program then stops when no loadable region is left in it (and is removed once it has no regions),
    /// or restarts without the region; the new one starts, or restarts, to load it. The region keeps its UDP port.
    /// </summary>
    Task ChangeRunsWithAsync(Guid regionId, string? shareWith, CancellationToken ct);

    /// <summary>
    /// Renames a region that no running program has loaded, saving only its name: its old region file goes off, and it
    /// registers under the new name at its next start. Throws <see cref="ArgumentException"/> for a name that breaks the name
    /// rules or that another region (live or deleted, in any letter case) holds; a change of its own name's letter case is
    /// allowed. Throws <see cref="InvalidOperationException"/> for a missing or deleted region, while it is loaded ("Stop
    /// &lt;Name&gt; first; a region is renamed while it is offline.") and while its program runs an OAR job.
    /// </summary>
    Task RenameRegionAsync(Guid regionId, string name, CancellationToken ct);

    /// <summary>
    /// Saves a region's size (1 to <see cref="RegionPlacement.MaxSize"/> squares on each side); nothing restarts, so it applies at
    /// its program's next start. Throws <see cref="ArgumentException"/> in plain words for a size out of range or an area that
    /// is off the map or taken, and <see cref="InvalidOperationException"/> for a missing or deleted region and while its
    /// program runs an OAR job.
    /// </summary>
    Task ResizeRegionAsync(Guid regionId, int size, CancellationToken ct);

    /// <summary>
    /// Saves a region's limits and access options; they apply at its program's next start. Throws
    /// <see cref="ArgumentException"/> carrying the first validation error, and <see cref="InvalidOperationException"/> for a
    /// missing or deleted region.
    /// </summary>
    Task SaveRegionOptionsAsync(Guid regionId, RegionOptions options, CancellationToken ct);

    /// <summary>
    /// Saves a program's physics (null: the grid default) and options. When its cores or priority changed and it runs, the
    /// operating system applies them at once and this returns true; everything else applies at its next start, and it
    /// returns false. Throws <see cref="ArgumentException"/> carrying the first validation error or for an unknown program,
    /// and <see cref="InvalidOperationException"/> in plain words when the cores or priority could not be applied (they stay
    /// saved).
    /// </summary>
    Task<bool> SaveProgramOptionsAsync(string instance, string? physics, ProgramOptions options, CancellationToken ct);

    /// <summary>
    /// True while the program runs and was started (or adopted) from other settings than the current ones: its physics,
    /// scripts or other options, or a loadable region's name, size or options. Cores and priority apply at once and never count.
    /// </summary>
    bool NeedsRestart(string instance);

    /// <summary>
    /// Moves a region to Recently deleted at once, even a broken one: it keeps its database rows, name, spot, ports and
    /// folder name. Its program then stops when no loadable region is left in it (and is removed once it has no live
    /// regions), or restarts without the region when it had loaded it; a failed or stopped program is left alone. The
    /// region's row in Robust's map is removed when the database runs. Throws <see cref="InvalidOperationException"/> for a
    /// missing or deleted region and while its program runs an OAR job ("&lt;job&gt; is still running. Try again when it
    /// finishes.").
    /// </summary>
    Task DeleteRegionAsync(Guid regionId, CancellationToken ct);

    /// <summary>
    /// Brings a deleted region back, stopped: into its old program, or into a new one under the same folder name with fresh
    /// ports when that program went. Nothing restarts. Throws <see cref="InvalidOperationException"/> ("That region isn't
    /// deleted.") for a live region.
    /// </summary>
    Task RestoreRegionAsync(Guid regionId, CancellationToken ct);

    /// <summary>
    /// Deletes a deleted region forever: its database rows go, then the region leaves the settings, freeing its name, spot,
    /// ports and folder name (the folder itself stays, with its logs). Throws <see cref="InvalidOperationException"/> for a
    /// live region ("That region isn't deleted.") and while the database isn't running ("Start the database first.").
    /// </summary>
    Task EraseRegionAsync(Guid regionId, CancellationToken ct);

    /// <summary>An estimate of the bytes each deleted region still holds in the database; null while the database isn't running.</summary>
    Task<IReadOnlyDictionary<Guid, long>?> DeletedSpaceAsync(CancellationToken ct);

    /// <summary>
    /// Runs an OAR job on a running region: Import loads a library file, Save writes the region to the library as
    /// <c>&lt;Region&gt;-&lt;yyyy-MM-dd&gt;.oar</c> (numbered when taken), MakeLand loads the region's own type again. One job
    /// runs per program at a time and holds no global lock, so other programs and the world keep working; while it runs,
    /// the power menu, a move and a change of how the region runs are refused for every region of its program. Returns
    /// the library name a Save wrote, and null otherwise. Throws <see cref="ArgumentException"/> in plain words for a bad
    /// request (an unknown region, a file not in the library), and <see cref="InvalidOperationException"/> when the
    /// region is not running ("Start &lt;Region&gt; first; an OAR needs it running."), when its program already runs a job
    /// ("&lt;job&gt; is still running. Try again when it finishes."), and when the job fails.
    /// <paramref name="progress"/> hears each stage and, while a save runs, the bytes written so far.
    /// </summary>
    Task<string?> RunOarJobAsync(OarJobRequest request, CancellationToken ct, IProgress<OarJobProgress>? progress = null);

    /// <summary>
    /// The text of the OAR job running in a program ("Importing castle.oar into Beach", "Saving Home as an OAR",
    /// "Making the land of Home again"), or null when it runs none.
    /// </summary>
    string? BusyWith(string instance);

    /// <summary>The controller's cached settings (what the grid was configured from); the host reads these per request.</summary>
    LauncherSettings CurrentSettings();

    /// <summary>
    /// <paramref name="name"/> is "database" or "robust"; regions use <see cref="RegionPowerAsync"/>. A Region Server or a
    /// Connected Server refuses "robust" with <see cref="InvalidOperationException"/>, as do <see cref="StartComponentAsync"/>,
    /// <see cref="StopComponentAsync"/> and <see cref="CreateOwnerAsync"/>.
    /// </summary>
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
