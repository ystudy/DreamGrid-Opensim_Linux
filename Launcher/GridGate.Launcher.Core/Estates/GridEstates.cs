using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Estates;

/// <summary>The regions' database can't be asked (it isn't running, or it answered with an error).</summary>
public sealed class EstatesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One line of the Estates list. <see cref="OwnerName"/> is the owner's UUID when the account isn't on the grid.</summary>
public sealed record EstateSummary(int Id, string Name, Guid Owner, string OwnerName, int Regions, int Managers);

public sealed record EstatePerson(Guid Id, string Name);

public sealed record EstateDetail(int Id, string Name, Guid Owner, string OwnerName, IReadOnlyList<EstatePerson> Managers, IReadOnlyList<Guid> Regions);

public enum EstateChange { Done, NoEstate, NameTaken, AlreadyManager, TooManyManagers, NotAManager }

/// <summary>
/// OpenSim's estate tables in the regions' database (<c>opensim</c>: <c>estate_settings</c>, <c>estate_managers</c>,
/// <c>estate_map</c>), for the admin's Estates pages (spec 2026-11-05-hypergrid-links-estates-design). Every method throws
/// <see cref="EstatesUnavailableException"/> when the database can't be asked; every change is one transaction. Running regions
/// keep their estate in memory: the caller tells them to read it again.
/// </summary>
public interface IGridEstates
{
    Task<IReadOnlyList<EstateSummary>> ListAsync(CancellationToken ct);

    /// <summary>The estate with its managers and regions, or null.</summary>
    Task<EstateDetail?> GetAsync(int id, CancellationToken ct);

    /// <summary>Whether another estate (not <paramref name="except"/>) has the name, ignoring case.</summary>
    Task<bool> NameTakenAsync(string name, int? except, CancellationToken ct);

    Task<EstateChange> RenameAsync(int id, string name, CancellationToken ct);

    Task<EstateChange> SetOwnerAsync(int id, Guid owner, CancellationToken ct);

    Task<EstateChange> AddManagerAsync(int id, Guid manager, CancellationToken ct);

    Task<EstateChange> RemoveManagerAsync(int id, Guid manager, CancellationToken ct);

    /// <summary>The estate each region is in, for the regions that are in one.</summary>
    Task<IReadOnlyDictionary<Guid, int>> RegionEstatesAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IGridEstates"/> on the <c>opensim</c> database as opensimuser, written the way OpenSim's <c>MySQLEstateStore</c> writes
/// the rows. Names come from Robust's <c>UserAccounts</c> (as robustuser) in a second connection; a name that can't be read shows
/// the UUID. The settings are read per call.
/// </summary>
public sealed class GridEstates(Func<LauncherSettings> settings) : IGridEstates
{
    private const uint TimeoutSeconds = 5;

    /// <summary>The viewer's own limit on an estate's managers.</summary>
    public const int MaxManagers = 10;

    public Task<IReadOnlyList<EstateSummary>> ListAsync(CancellationToken ct) => RunAsync<IReadOnlyList<EstateSummary>>(async conn =>
    {
        var rows = new List<(int Id, string Name, Guid Owner, int Regions, int Managers)>();
        await using (var cmd = new MySqlCommand(
            "SELECT s.EstateID, s.EstateName, s.EstateOwner, " +
            "(SELECT COUNT(*) FROM estate_map m WHERE m.EstateID = s.EstateID), " +
            "(SELECT COUNT(*) FROM estate_managers g WHERE g.EstateID = s.EstateID) " +
            "FROM estate_settings s ORDER BY s.EstateName", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add((Convert.ToInt32(r.GetValue(0)), r.IsDBNull(1) ? "" : r.GetString(1), Id(r, 2), Convert.ToInt32(r.GetValue(3)), Convert.ToInt32(r.GetValue(4))));
        var names = await NamesAsync(rows.Select(x => x.Owner), ct);
        return rows.Select(x => new EstateSummary(x.Id, x.Name, x.Owner, NameOf(names, x.Owner), x.Regions, x.Managers)).ToList();
    });

    public Task<EstateDetail?> GetAsync(int id, CancellationToken ct) => RunAsync<EstateDetail?>(async conn =>
    {
        string name;
        Guid owner;
        await using (var cmd = Command(conn, null, "SELECT EstateName, EstateOwner FROM estate_settings WHERE EstateID = @e", ("@e", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (name, owner) = (r.IsDBNull(0) ? "" : r.GetString(0), Id(r, 1));
        }
        var managers = await IdsAsync(conn, "SELECT uuid FROM estate_managers WHERE EstateID = @e", id, ct);
        var regions = await IdsAsync(conn, "SELECT RegionID FROM estate_map WHERE EstateID = @e", id, ct);
        var names = await NamesAsync(managers.Append(owner), ct);
        return new EstateDetail(id, name, owner, NameOf(names, owner),
            managers.Distinct().Select(m => new EstatePerson(m, NameOf(names, m))).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            regions);
    });

    public Task<bool> NameTakenAsync(string name, int? except, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var cmd = Command(conn, null, "SELECT COUNT(*) FROM estate_settings WHERE LOWER(EstateName) = LOWER(@n) AND EstateID <> @e",
            ("@n", name.Trim()), ("@e", except ?? -1));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    });

    public Task<EstateChange> RenameAsync(int id, string name, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ExistsAsync(conn, tx, id, ct)) return EstateChange.NoEstate;
        await using (var taken = Command(conn, tx, "SELECT COUNT(*) FROM estate_settings WHERE LOWER(EstateName) = LOWER(@n) AND EstateID <> @e",
                         ("@n", name), ("@e", id)))
            if (Convert.ToInt64(await taken.ExecuteScalarAsync(ct)) > 0) return EstateChange.NameTaken;
        await ExecAsync(conn, tx, "UPDATE estate_settings SET EstateName = @n WHERE EstateID = @e", ct, ("@n", name), ("@e", id));
        await tx.CommitAsync(ct);
        return EstateChange.Done;
    });

    public Task<EstateChange> SetOwnerAsync(int id, Guid owner, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ExistsAsync(conn, tx, id, ct)) return EstateChange.NoEstate;
        await ExecAsync(conn, tx, "UPDATE estate_settings SET EstateOwner = @o WHERE EstateID = @e", ct, ("@o", owner.ToString("D")), ("@e", id));
        // An owner is never a manager too.
        await ExecAsync(conn, tx, "DELETE FROM estate_managers WHERE EstateID = @e AND uuid = @o", ct, ("@e", id), ("@o", owner.ToString("D")));
        await tx.CommitAsync(ct);
        return EstateChange.Done;
    });

    public Task<EstateChange> AddManagerAsync(int id, Guid manager, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ExistsAsync(conn, tx, id, ct)) return EstateChange.NoEstate;
        var managers = await IdsAsync(conn, "SELECT uuid FROM estate_managers WHERE EstateID = @e FOR UPDATE", id, ct, tx);
        if (managers.Contains(manager)) return EstateChange.AlreadyManager;
        if (managers.Distinct().Count() >= MaxManagers) return EstateChange.TooManyManagers;
        await ExecAsync(conn, tx, "INSERT INTO estate_managers (EstateID, uuid) VALUES (@e, @m)", ct, ("@e", id), ("@m", manager.ToString("D")));
        await tx.CommitAsync(ct);
        return EstateChange.Done;
    });

    public Task<EstateChange> RemoveManagerAsync(int id, Guid manager, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ExistsAsync(conn, tx, id, ct)) return EstateChange.NoEstate;
        var removed = await ExecAsync(conn, tx, "DELETE FROM estate_managers WHERE EstateID = @e AND uuid = @m", ct, ("@e", id), ("@m", manager.ToString("D")));
        await tx.CommitAsync(ct);
        return removed > 0 ? EstateChange.Done : EstateChange.NotAManager;
    });

    public Task<IReadOnlyDictionary<Guid, int>> RegionEstatesAsync(CancellationToken ct) => RunAsync<IReadOnlyDictionary<Guid, int>>(async conn =>
    {
        var map = new Dictionary<Guid, int>();
        await using var cmd = new MySqlCommand("SELECT RegionID, EstateID FROM estate_map", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Id(r, 0) is var region && region != Guid.Empty) map[region] = Convert.ToInt32(r.GetValue(1));
        return map;
    });

    private static async Task<bool> ExistsAsync(MySqlConnection conn, MySqlTransaction tx, int id, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, "SELECT COUNT(*) FROM estate_settings WHERE EstateID = @e FOR UPDATE", ("@e", id));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<List<Guid>> IdsAsync(MySqlConnection conn, string sql, int id, CancellationToken ct, MySqlTransaction? tx = null)
    {
        var list = new List<Guid>();
        await using var cmd = Command(conn, tx, sql, ("@e", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Id(r, 0) is var g && g != Guid.Empty) list.Add(g);
        return list;
    }

    /// <summary>The names of the accounts among <paramref name="ids"/>, from Robust's database; empty when it can't be read.</summary>
    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        if (wanted.Count == 0) return names;
        try
        {
            var s = settings();
            await using var conn = await GridDb.OpenRobustAsync(s, TimeoutSeconds, ct);
            var ps = wanted.Select((g, i) => ($"@a{i}", (object)g.ToString("D"))).ToArray();
            await using var cmd = Command(conn, null,
                $"SELECT PrincipalID, FirstName, LastName FROM UserAccounts WHERE PrincipalID IN ({string.Join(", ", ps.Select(p => p.Item1))})", ps);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (Id(r, 0) is var g && g != Guid.Empty) names[g] = $"{r.GetString(1)} {r.GetString(2)}";
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException) { }
        return names;
    }

    private static string NameOf(Dictionary<Guid, string> names, Guid id) => names.TryGetValue(id, out var n) ? n : id.ToString("D");

    private async Task<T> RunAsync<T>(Func<MySqlConnection, Task<T>> work)
    {
        try
        {
            var s = settings();
            await using var conn = await GridDb.OpenOpensimAsync(s, TimeoutSeconds, CancellationToken.None);
            return await work(conn);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidCastException or FormatException or OverflowException)
        {
            throw new EstatesUnavailableException("The regions' database could not be read.", e);
        }
    }

    private static MySqlCommand Command(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object Value)[] ps)
    {
        var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
    private static Guid Id(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? Guid.Empty : r.GetValue(i) is Guid g ? g : Guid.TryParse(Convert.ToString(r.GetValue(i)), out var p) ? p : Guid.Empty;
}
