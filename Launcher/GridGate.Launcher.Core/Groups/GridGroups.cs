using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Groups;

/// <summary>The grid's database can't be asked (it isn't running, or it answered with an error).</summary>
public sealed class GroupsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One line of the Groups list.</summary>
public sealed record GroupSummary(Guid Id, string Name, string Founder, int Members, bool OpenToJoin);

/// <summary>
/// One member of a group: <see cref="PrincipalId"/> exactly as stored (a UUID, or <c>uuid;url;name</c> for a hypergrid
/// visitor), the name to show, their role names, and whether they have the group's Owners role.
/// </summary>
public sealed record GroupMember(string PrincipalId, string Name, bool Local, IReadOnlyList<string> Roles, bool Owner);

public sealed record GroupDetail(Guid Id, string Name, string Charter, string Founder, bool OpenToJoin, Guid OwnerRoleId,
    IReadOnlyList<GroupMember> Members);

public enum RenameResult { Done, NoGroup, NameTaken }

public enum AddResult { Added, AlreadyMember, NoGroup }

/// <summary>
/// OpenSim's core group tables (<c>os_groups_*</c> in the <c>robust</c> database), for the admin's Groups pages (spec
/// 2026-11-02-groups-admin-design). Every method throws <see cref="GroupsUnavailableException"/> when the database can't be
/// asked; every change is one transaction.
/// </summary>
public interface IGridGroups
{
    /// <summary>Every group, by name.</summary>
    Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken ct);

    /// <summary>The group with its members by name, or null.</summary>
    Task<GroupDetail?> GetAsync(Guid group, CancellationToken ct);

    Task<RenameResult> UpdateAsync(Guid group, string name, string charter, CancellationToken ct);

    /// <summary>Puts a resident in the group's Everyone role. Their active group is left as it is; an invitation to the group goes.</summary>
    Task<AddResult> AddMemberAsync(Guid group, Guid principal, CancellationToken ct);

    /// <summary>Takes a member out of the group and every role in it; false when they weren't a member.</summary>
    Task<bool> RemoveMemberAsync(Guid group, string principalId, CancellationToken ct);

    /// <summary>Deletes the group and everything in it; false when there was no such group.</summary>
    Task<bool> DeleteAsync(Guid group, CancellationToken ct);

    /// <summary>The group's member IDs, exactly as stored.</summary>
    Task<IReadOnlyList<string>> MemberIdsAsync(Guid group, CancellationToken ct);

    /// <summary>Every account whose level is 0 or above.</summary>
    Task<IReadOnlyList<Guid>> ActiveResidentsAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IGridGroups"/> on the <c>robust</c> database as robustuser over the private MariaDB port, written the way
/// OpenSim's GroupsService writes the rows. Account names are read in a second query rather than joined: the group tables are
/// latin1 and the accounts table isn't. The settings are read per call.
/// </summary>
public sealed class GridGroups(Func<LauncherSettings> settings) : IGridGroups
{
    // Short: a page waits on this and must never hang on a database that is down.
    private const uint TimeoutSeconds = 3;
    private static readonly string Zero = GroupRules.Everyone.ToString();

    public Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken ct) => RunAsync<IReadOnlyList<GroupSummary>>(async conn =>
    {
        var rows = new List<(Guid Id, string Name, string Founder, int Members, bool Open)>();
        await using (var cmd = Command(conn, null,
            "SELECT g.GroupID, g.Name, g.FounderID, g.OpenEnrollment, (SELECT COUNT(*) FROM os_groups_membership m WHERE m.GroupID = g.GroupID) " +
            "FROM os_groups_groups g ORDER BY g.Name"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (Guid.TryParse(Text(r, 0), out var id))
                    rows.Add((id, r.GetString(1), Text(r, 2), Convert.ToInt32(r.GetValue(4)), Text(r, 3) == "1"));
        var names = await NamesAsync(conn, rows.Select(x => x.Founder), ct);
        return rows.Select(x => new GroupSummary(x.Id, x.Name, Founder(names, x.Founder), x.Members, x.Open)).ToList();
    });

    public Task<GroupDetail?> GetAsync(Guid group, CancellationToken ct) => RunAsync<GroupDetail?>(async conn =>
    {
        string name, charter, founder, ownerRole;
        bool open;
        await using (var cmd = Command(conn, null,
            "SELECT Name, Charter, FounderID, OpenEnrollment, OwnerRoleID FROM os_groups_groups WHERE GroupID = @g", ("@g", Id(group))))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (name, charter, founder, open, ownerRole) = (r.GetString(0), r.GetString(1), Text(r, 2), Text(r, 3) == "1", Text(r, 4));
        }

        var members = new List<string>();
        await using (var cmd = Command(conn, null, "SELECT PrincipalID FROM os_groups_membership WHERE GroupID = @g", ("@g", Id(group))))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) members.Add(r.GetString(0));

        var roles = new Dictionary<string, List<(string Role, bool Owner)>>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = Command(conn, null,
            "SELECT rm.PrincipalID, r.Name, r.RoleID FROM os_groups_rolemembership rm " +
            "JOIN os_groups_roles r ON r.GroupID = rm.GroupID AND r.RoleID = rm.RoleID WHERE rm.GroupID = @g ORDER BY r.Name", ("@g", Id(group))))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var who = r.GetString(0);
                if (!roles.TryGetValue(who, out var list)) roles[who] = list = [];
                list.Add((r.GetString(1), string.Equals(Text(r, 2), ownerRole, StringComparison.OrdinalIgnoreCase)));
            }

        var names = await NamesAsync(conn, members.Append(founder), ct);
        var detail = members.Select(id =>
        {
            var local = GroupRules.IsLocal(id, out var guid);
            var shown = local && names.TryGetValue(guid, out var n) ? GroupRules.DisplayName(id, n.First, n.Last) : GroupRules.DisplayName(id, null, null);
            var mine = roles.TryGetValue(id, out var l) ? l : [];
            return new GroupMember(id, shown, local, mine.Select(x => x.Role).ToList(), mine.Any(x => x.Owner));
        }).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new GroupDetail(group, name, charter, Founder(names, founder), open,
            Guid.TryParse(ownerRole, out var o) ? o : Guid.Empty, detail);
    });

    public Task<RenameResult> UpdateAsync(Guid group, string name, string charter, CancellationToken ct) => RunAsync(async conn =>
    {
        try
        {
            await using var cmd = Command(conn, null, "UPDATE os_groups_groups SET Name = @n, Charter = @c WHERE GroupID = @g",
                ("@n", name.Trim()), ("@c", charter), ("@g", Id(group)));
            // MySqlConnector counts matched rows (UseAffectedRows is off), so an unchanged save is still 1.
            return await cmd.ExecuteNonQueryAsync(ct) == 0 ? RenameResult.NoGroup : RenameResult.Done;
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return RenameResult.NameTaken;
        }
    });

    public Task<AddResult> AddMemberAsync(Guid group, Guid principal, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        string g = Id(group), p = Id(principal);
        if (await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM os_groups_groups WHERE GroupID = @g FOR UPDATE", ct, ("@g", g)) == 0)
            return AddResult.NoGroup;
        if (await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM os_groups_membership WHERE GroupID = @g AND PrincipalID = @p", ct, ("@g", g), ("@p", p)) > 0)
            return AddResult.AlreadyMember;
        await ExecAsync(conn, tx,
            "INSERT INTO os_groups_membership (GroupID, PrincipalID, SelectedRoleID, Contribution, ListInProfile, AcceptNotices, AccessToken) " +
            "VALUES (@g, @p, @z, 0, 1, 1, @t)", ct, ("@g", g), ("@p", p), ("@z", Zero), ("@t", Guid.NewGuid().ToString()));
        await ExecAsync(conn, tx, "INSERT IGNORE INTO os_groups_rolemembership (GroupID, RoleID, PrincipalID) VALUES (@g, @z, @p)", ct,
            ("@g", g), ("@z", Zero), ("@p", p));
        await ExecAsync(conn, tx, "DELETE FROM os_groups_invites WHERE GroupID = @g AND PrincipalID = @p", ct, ("@g", g), ("@p", p));
        await tx.CommitAsync(ct);
        return AddResult.Added;
    });

    public Task<bool> RemoveMemberAsync(Guid group, string principalId, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        string g = Id(group);
        var removed = await ExecAsync(conn, tx, "DELETE FROM os_groups_membership WHERE GroupID = @g AND PrincipalID = @p", ct, ("@g", g), ("@p", principalId));
        await ExecAsync(conn, tx, "DELETE FROM os_groups_rolemembership WHERE GroupID = @g AND PrincipalID = @p", ct, ("@g", g), ("@p", principalId));
        await ExecAsync(conn, tx, "DELETE FROM os_groups_invites WHERE GroupID = @g AND PrincipalID = @p", ct, ("@g", g), ("@p", principalId));
        await ExecAsync(conn, tx, "UPDATE os_groups_principals SET ActiveGroupID = @z WHERE PrincipalID = @p AND ActiveGroupID = @g", ct,
            ("@z", Zero), ("@p", principalId), ("@g", g));
        await tx.CommitAsync(ct);
        return removed > 0;
    });

    public Task<bool> DeleteAsync(Guid group, CancellationToken ct) => RunAsync(async conn =>
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        string g = Id(group);
        foreach (var table in new[] { "os_groups_rolemembership", "os_groups_membership", "os_groups_roles", "os_groups_invites", "os_groups_notices" })
            await ExecAsync(conn, tx, $"DELETE FROM {table} WHERE GroupID = @g", ct, ("@g", g));
        await ExecAsync(conn, tx, "UPDATE os_groups_principals SET ActiveGroupID = @z WHERE ActiveGroupID = @g", ct, ("@z", Zero), ("@g", g));
        var deleted = await ExecAsync(conn, tx, "DELETE FROM os_groups_groups WHERE GroupID = @g", ct, ("@g", g));
        await tx.CommitAsync(ct);
        return deleted > 0;
    });

    public Task<IReadOnlyList<string>> MemberIdsAsync(Guid group, CancellationToken ct) => RunAsync<IReadOnlyList<string>>(async conn =>
    {
        var list = new List<string>();
        await using var cmd = Command(conn, null, "SELECT PrincipalID FROM os_groups_membership WHERE GroupID = @g", ("@g", Id(group)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    });

    public Task<IReadOnlyList<Guid>> ActiveResidentsAsync(CancellationToken ct) => RunAsync<IReadOnlyList<Guid>>(async conn =>
    {
        var list = new List<Guid>();
        await using var cmd = Command(conn, null, "SELECT PrincipalID FROM UserAccounts WHERE UserLevel >= 0");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Guid.TryParse(Text(r, 0), out var id)) list.Add(id);
        return list;
    });

    /// <summary>First and last names of the local accounts among <paramref name="ids"/>.</summary>
    private static async Task<Dictionary<Guid, (string First, string Last)>> NamesAsync(MySqlConnection conn, IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => GroupRules.IsLocal(i, out var g) ? g : (Guid?)null).OfType<Guid>().Distinct().ToList();
        var names = new Dictionary<Guid, (string, string)>();
        if (wanted.Count == 0) return names;
        var ps = wanted.Select((g, i) => ($"@a{i}", (object)Id(g))).ToArray();
        await using var cmd = Command(conn, null,
            $"SELECT PrincipalID, FirstName, LastName FROM UserAccounts WHERE PrincipalID IN ({string.Join(", ", ps.Select(p => p.Item1))})", ps);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Guid.TryParse(Text(r, 0), out var id)) names[id] = (r.GetString(1), r.GetString(2));
        return names;
    }

    private static string Founder(Dictionary<Guid, (string First, string Last)> names, string founder) =>
        Guid.TryParse(founder, out var id) && names.TryGetValue(id, out var n) ? $"{n.First} {n.Last}" : founder;

    private async Task<T> RunAsync<T>(Func<MySqlConnection, Task<T>> work)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync();
            return await work(conn);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new GroupsUnavailableException("The database refused the change.", e);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidCastException or FormatException or OverflowException)
        {
            throw new GroupsUnavailableException("The database could not be read.", e);
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

    private static async Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static string Id(Guid id) => id.ToString("D");

    // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
    private static string Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i) is Guid g ? g.ToString("D") : Convert.ToString(r.GetValue(i)) ?? "";
}
