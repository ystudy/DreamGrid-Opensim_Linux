using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Grid;

public sealed record GridRegion(Guid Id, string Name);

/// <param name="Available">False when the database could not be asked; <see cref="Regions"/> is then empty.</param>
public sealed record GridRegionList(IReadOnlyList<GridRegion> Regions, bool Available)
{
    public static readonly GridRegionList Unavailable = new([], false);
}

/// <summary>The regions registered with the grid service, on every machine of the grid; the host can swap in a test double.</summary>
public interface IGridRegions
{
    /// <summary>Never throws for a database that is down or not set up yet: returns <see cref="GridRegionList.Unavailable"/>.</summary>
    Task<GridRegionList> ListAsync(CancellationToken ct);
}

/// <summary>
/// Reads Robust's regions table as robustuser: a region is listed while it is registered with Robust, whichever machine
/// runs it (the grid controller only knows this machine's regions). Shaped like <see cref="OnlineUsers"/>: settings read per
/// call, a short timeout, and any failure but the caller's cancellation gives "unavailable", never an error page.
/// </summary>
public sealed class GridRegions(Func<LauncherSettings> settings, ILogger<GridRegions>? log = null) : IGridRegions
{
    private int _warned;

    // Short: the splash page asks while viewers wait at the login screen.
    private const uint TimeoutSeconds = 3;

    private const string Query = "SELECT uuid, regionName FROM regions ORDER BY regionName";

    public async Task<GridRegionList> ListAsync(CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(Query, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<GridRegion>();
            while (await reader.ReadAsync(ct))
            {
                // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
                var id = reader.GetValue(0) is Guid g ? g : Guid.TryParse(Convert.ToString(reader.GetValue(0)), out var p) ? p : Guid.Empty;
                list.Add(new GridRegion(id, reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }
            return new GridRegionList(list, true);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return GridRegionList.Unavailable; // the database is down or still starting: expected, not logged
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var level = Interlocked.Exchange(ref _warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
            log?.Log(level, "The grid's regions could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return GridRegionList.Unavailable;
        }
    }
}
