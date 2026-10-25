using DreamGrid.Launcher.Core.OpenSim;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The public surface of <see cref="GridController"/>, so the host can use a test double.</summary>
public interface IGridController
{
    /// <summary>Stages, writes the INIs, checks ports, then starts (or adopts) MariaDB, Robust and the region.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops region, Robust, then MariaDB.</summary>
    Task StopAllAsync(CancellationToken ct);

    /// <summary>Stops monitoring; every child keeps running (spec 4.12).</summary>
    Task DetachAsync();

    Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct);

    /// <summary><paramref name="content"/> is "starter" or "island".</summary>
    Task CreateRegionAsync(string name, string content, CancellationToken ct);

    GridStatus GetStatus();

    /// <summary><paramref name="name"/> is "database", "robust" or "region".</summary>
    Task RestartComponentAsync(string name, CancellationToken ct);

    /// <summary>Reloads the settings and rewrites the INIs without restarting anything.</summary>
    void RegenerateConfig();

    void OnRegionAlert(RegionAlert alert);
}
