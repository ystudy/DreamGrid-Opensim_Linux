using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>A region's rows in the grid's databases: how much space they hold, its map entry, and erasing them.</summary>
public interface IRegionStore
{
    /// <summary>An estimate of the bytes each region's rows hold, for every id asked (0 when it has none).</summary>
    Task<IReadOnlyDictionary<Guid, long>> SpaceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Removes the region's row from Robust's <c>regions</c> table, so it leaves the map.</summary>
    Task DeregisterAsync(Guid id, CancellationToken ct);

    /// <summary>Deletes every row the region has in the opensim database, and its Robust row, in one transaction.</summary>
    Task EraseAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// <see cref="IRegionStore"/> on the launcher's MariaDB, as root. The table and column names are OpenSim's
/// (OpenSim/Data/MySQL/Resources: RegionStore, EstateStore and GridStore migrations). OpenSim makes its tables at its first
/// start, so a table that does not exist yet is skipped.
/// </summary>
public sealed class RegionStore(LauncherSettings s) : IRegionStore
{
    private const string OpenSim = "opensim";
    private const string Robust = "robust";

    /// <summary>
    /// The statements that erase a region, in order, each with the tables it names. Rows that hang off the region's prims
    /// (shapes, items) and parcels (access lists) go before them.
    /// </summary>
    private static readonly (string Sql, string[] Tables)[] Erase =
    [
        // Subqueries, not multi-table deletes: those resolve their target against the selected database, and none is.
        ("DELETE FROM `opensim`.`primitems` WHERE `primID` IN (SELECT `UUID` FROM `opensim`.`prims` WHERE `RegionUUID` = @id)", ["primitems", "prims"]),
        ("DELETE FROM `opensim`.`primshapes` WHERE `UUID` IN (SELECT `UUID` FROM `opensim`.`prims` WHERE `RegionUUID` = @id)", ["primshapes", "prims"]),
        ("DELETE FROM `opensim`.`prims` WHERE `RegionUUID` = @id", ["prims"]),
        ("DELETE FROM `opensim`.`terrain` WHERE `RegionUUID` = @id", ["terrain"]),
        ("DELETE FROM `opensim`.`bakedterrain` WHERE `RegionUUID` = @id", ["bakedterrain"]),
        ("DELETE FROM `opensim`.`landaccesslist` WHERE `LandUUID` IN (SELECT `UUID` FROM `opensim`.`land` WHERE `RegionUUID` = @id)", ["landaccesslist", "land"]),
        ("DELETE FROM `opensim`.`land` WHERE `RegionUUID` = @id", ["land"]),
        ("DELETE FROM `opensim`.`regionsettings` WHERE `regionUUID` = @id", ["regionsettings"]),
        ("DELETE FROM `opensim`.`regionwindlight` WHERE `region_id` = @id", ["regionwindlight"]),
        ("DELETE FROM `opensim`.`regionenvironment` WHERE `region_id` = @id", ["regionenvironment"]),
        ("DELETE FROM `opensim`.`spawn_points` WHERE `RegionID` = @id", ["spawn_points"]),
        ("DELETE FROM `opensim`.`regionextra` WHERE `RegionID` = @id", ["regionextra"]),
        ("DELETE FROM `opensim`.`regionban` WHERE `regionUUID` = @id", ["regionban"]),
        ("DELETE FROM `opensim`.`estate_map` WHERE `RegionID` = @id", ["estate_map"]),
    ];

    private const string Deregister = "DELETE FROM `robust`.`regions` WHERE `uuid` = @id";

    public async Task<IReadOnlyDictionary<Guid, long>> SpaceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var space = ids.Distinct().ToDictionary(id => id, _ => 0L);
        if (space.Count == 0) return space;
        await using var conn = await OpenAsync(ct);

        // Each prim table's average row length, times the region's rows in it; terrain is counted by its blobs.
        var avg = new Dictionary<string, long>();
        await using (var cmd = new MySqlCommand(
            "SELECT TABLE_NAME, AVG_ROW_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db " +
            "AND TABLE_NAME IN ('prims', 'primshapes', 'primitems', 'terrain', 'bakedterrain')", conn))
        {
            cmd.Parameters.AddWithValue("@db", OpenSim);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) avg[r.GetString(0)] = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
        }

        var parts = new List<string>();
        if (avg.ContainsKey("prims"))
        {
            parts.Add($"{avg["prims"]} * (SELECT COUNT(*) FROM `opensim`.`prims` WHERE `RegionUUID` = @id)");
            if (avg.TryGetValue("primshapes", out var shape))
                parts.Add($"{shape} * (SELECT COUNT(*) FROM `opensim`.`primshapes` h JOIN `opensim`.`prims` p ON h.`UUID` = p.`UUID` WHERE p.`RegionUUID` = @id)");
            if (avg.TryGetValue("primitems", out var item))
                parts.Add($"{item} * (SELECT COUNT(*) FROM `opensim`.`primitems` i JOIN `opensim`.`prims` p ON i.`primID` = p.`UUID` WHERE p.`RegionUUID` = @id)");
        }
        foreach (var terrain in new[] { "terrain", "bakedterrain" }.Where(avg.ContainsKey))
            parts.Add($"(SELECT COALESCE(SUM(LENGTH(`Heightfield`)), 0) FROM `opensim`.`{terrain}` WHERE `RegionUUID` = @id)");
        if (parts.Count == 0) return space;

        await using var query = new MySqlCommand("SELECT " + string.Join(" + ", parts), conn);
        var id = query.Parameters.Add("@id", MySqlDbType.VarChar);
        foreach (var region in space.Keys.ToList())
        {
            id.Value = region.ToString();
            space[region] = Convert.ToInt64(await query.ExecuteScalarAsync(ct));
        }
        return space;
    }

    public async Task DeregisterAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        if (!(await ExistingTablesAsync(conn, ct)).Contains((Robust, "regions"))) return;
        await RunAsync(conn, null, Deregister, id, ct);
    }

    public async Task EraseAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var existing = await ExistingTablesAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (sql, tables) in Erase)
            if (tables.All(t => existing.Contains((OpenSim, t))))
                await RunAsync(conn, tx, sql, id, ct);
        if (existing.Contains((Robust, "regions")))
            await RunAsync(conn, tx, Deregister, id, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(MariaDbServer.ConnectionString(s));
        try { await conn.OpenAsync(ct); }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return conn;
    }

    /// <summary>The (schema, table) pairs of the robust and opensim databases.</summary>
    private static async Task<HashSet<(string, string)>> ExistingTablesAsync(MySqlConnection conn, CancellationToken ct)
    {
        var tables = new HashSet<(string, string)>();
        await using var cmd = new MySqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA IN (@opensim, @robust)", conn);
        cmd.Parameters.AddWithValue("@opensim", OpenSim);
        cmd.Parameters.AddWithValue("@robust", Robust);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) tables.Add((r.GetString(0), r.GetString(1)));
        return tables;
    }

    private static async Task RunAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, Guid id, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
