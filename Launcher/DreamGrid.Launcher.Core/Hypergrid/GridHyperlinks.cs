using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Hypergrid;

/// <summary>The grid's database can't be asked (it isn't running, or it answered with an error).</summary>
public sealed class HyperlinksUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One hypergrid link: a row of Robust's <c>regions</c> with the Hyperlink flag. X and Y are in regions, not metres.</summary>
public sealed record Hyperlink(Guid Id, string Name, string Address, int X, int Y);

/// <summary>A square of the map something takes up, in regions: x, y, width and height.</summary>
public readonly record struct MapSpace(int X, int Y, int Width, int Height, bool Hyperlink)
{
    public bool Covers(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Hypergrid links in Robust's <c>regions</c> table (spec 2026-11-05-hypergrid-links-estates-design).</summary>
public interface IGridHyperlinks
{
    /// <summary>Every link, by name. Throws <see cref="HyperlinksUnavailableException"/> when the database can't be asked.</summary>
    Task<IReadOnlyList<Hyperlink>> ListAsync(CancellationToken ct);

    /// <summary>Everything on the map (the grid's regions and the links), in regions.</summary>
    Task<IReadOnlyList<MapSpace>> MapAsync(CancellationToken ct);

    /// <summary>Deletes the link's row (only a row with the Hyperlink flag); false when there was none.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct);
}

/// <summary>Where a new link goes on the map when staff don't say.</summary>
public static class HyperlinkPlace
{
    /// <summary>Where the map is empty and nothing is on the grid yet: the middle of the usual map.</summary>
    public const int EmptyX = 1000, EmptyY = 1000;

    public const int MaxCoordinate = 65535;

    /// <summary>
    /// The first free cell on the row one empty row above the grid's top region, from the grid's left-most column rightwards.
    /// Near the grid, so old viewers (which can't jump more than 4096 regions) can reach it.
    /// </summary>
    public static (int X, int Y) Free(IReadOnlyList<MapSpace> map)
    {
        var grid = map.Where(a => !a.Hyperlink).ToList();
        if (grid.Count == 0) grid = map.ToList();
        if (grid.Count == 0) return (EmptyX, EmptyY);
        var y = grid.Max(a => a.Y + a.Height) + 1;
        var x = grid.Min(a => a.X);
        while (y <= MaxCoordinate)
        {
            for (var cx = x; cx <= Math.Min(MaxCoordinate, x + 4096); cx++)
                if (!map.Any(a => a.Covers(cx, y))) return (cx, y);
            y++;
        }
        return (EmptyX, EmptyY);
    }

    /// <summary>Whether the cell is free.</summary>
    public static bool IsFree(IReadOnlyList<MapSpace> map, int x, int y) => !map.Any(a => a.Covers(x, y));
}

/// <summary>
/// <see cref="IGridHyperlinks"/> on the <c>robust</c> database as robustuser over the private MariaDB port. Rows are written by Robust
/// (<c>link-region</c>); this only reads them and removes one the way OpenSim's <c>unlink-region</c> does (one DELETE by UUID).
/// </summary>
public sealed class GridHyperlinks(Func<LauncherSettings> settings) : IGridHyperlinks
{
    private const uint TimeoutSeconds = 3;
    public const int HyperlinkFlag = 512;
    private const int RegionSize = 256;

    public Task<IReadOnlyList<Hyperlink>> ListAsync(CancellationToken ct) => RunAsync<IReadOnlyList<Hyperlink>>(async conn =>
    {
        var list = new List<Hyperlink>();
        await using var cmd = new MySqlCommand(
            $"SELECT uuid, regionName, serverURI, locX, locY FROM regions WHERE (flags & {HyperlinkFlag}) <> 0 ORDER BY regionName", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Guid.TryParse(Text(r, 0), out var id))
                list.Add(new Hyperlink(id, Text(r, 1), Text(r, 2), Convert.ToInt32(r.GetValue(3)) / RegionSize, Convert.ToInt32(r.GetValue(4)) / RegionSize));
        return list;
    });

    public Task<IReadOnlyList<MapSpace>> MapAsync(CancellationToken ct) => RunAsync<IReadOnlyList<MapSpace>>(async conn =>
    {
        var list = new List<MapSpace>();
        await using var cmd = new MySqlCommand("SELECT locX, locY, sizeX, sizeY, flags FROM regions", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int Cells(int i) => Math.Max(1, (Convert.ToInt32(r.GetValue(i)) + RegionSize - 1) / RegionSize);
            list.Add(new MapSpace(Convert.ToInt32(r.GetValue(0)) / RegionSize, Convert.ToInt32(r.GetValue(1)) / RegionSize, Cells(2), Cells(3),
                (Convert.ToInt32(r.GetValue(4)) & HyperlinkFlag) != 0));
        }
        return list;
    });

    public Task<bool> RemoveAsync(Guid id, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var cmd = new MySqlCommand($"DELETE FROM regions WHERE uuid = @id AND (flags & {HyperlinkFlag}) <> 0", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    });

    private async Task<T> RunAsync<T>(Func<MySqlConnection, Task<T>> work)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync();
            return await work(conn);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidCastException or FormatException or OverflowException)
        {
            throw new HyperlinksUnavailableException("The database could not be read.", e);
        }
    }

    private static string Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i) is Guid g ? g.ToString("D") : Convert.ToString(r.GetValue(i)) ?? "";
}
