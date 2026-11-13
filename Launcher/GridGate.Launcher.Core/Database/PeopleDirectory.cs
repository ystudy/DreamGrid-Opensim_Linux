using System.Text;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Database;

/// <summary>The database can't be asked (it isn't running yet, or it answered with an error).</summary>
public sealed class PeopleDirectoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Which people the People list shows.</summary>
public enum PeopleFilter { Everyone, Staff, Suspended, Waiting }

/// <summary>An account with when it last logged in and out (Robust's GridUser) and the region it is in now (Presence), if any.</summary>
public sealed record PersonRow(RobustAccount Account, DateTimeOffset? LastLogin, DateTimeOffset? LastLogout, Guid? OnlineRegion);

/// <summary>One page of the People list: its rows, how many match in all, which page (from 1) and the page size.</summary>
public sealed record PeoplePage(IReadOnlyList<PersonRow> Rows, int Total, int Page, int PageSize);

/// <summary>Searching the grid's accounts by part of a name, a page at a time (Robust's account service can only look up one name).</summary>
public interface IPeopleDirectory
{
    /// <summary>
    /// The accounts whose "First Last" contains <paramref name="text"/> (all of them when it is empty), by last then first name,
    /// page <paramref name="page"/> (from 1). <paramref name="waiting"/> are the sign-ups waiting for staff or email:
    /// <see cref="PeopleFilter.Waiting"/> shows only them and <see cref="PeopleFilter.Suspended"/> leaves them out.
    /// <see cref="PeopleFilter.Staff"/> is everyone at or above <paramref name="staffLevel"/>. Throws
    /// <see cref="PeopleDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task<PeoplePage> SearchAsync(string? text, PeopleFilter filter, IReadOnlyCollection<Guid> waiting, int staffLevel, int page, CancellationToken ct);

    /// <summary>One account with its last login and online region, or null when there is none.</summary>
    Task<PersonRow?> GetAsync(Guid principal, CancellationToken ct);
}

/// <summary>
/// <see cref="IPeopleDirectory"/> on Robust's database, read as robustuser over the private MariaDB port like
/// <see cref="StaffDirectory"/>. Every query is limited to one page, so a large grid never loads every row.
/// </summary>
public sealed class PeopleDirectory(Func<LauncherSettings> settings) : IPeopleDirectory
{
    public const int PageSize = 25;
    public const int MaxSearch = 64;

    private const uint TimeoutSeconds = 3;
    private const string Zero = "00000000-0000-0000-0000-000000000000";

    private const string Columns =
        "SELECT u.PrincipalID, u.FirstName, u.LastName, u.UserLevel, u.Email, u.Created, g.Login, g.Logout, " +
        "(SELECT p.RegionID FROM robust.Presence p WHERE p.UserID = u.PrincipalID AND p.RegionID <> '" + Zero + "' LIMIT 1) " +
        "FROM robust.UserAccounts u LEFT JOIN robust.GridUser g ON g.UserID = u.PrincipalID";

    /// <summary>The search text as a <c>LIKE</c> pattern: trimmed, cut to <see cref="MaxSearch"/>, with <c>\ % _</c> escaped, inside <c>%…%</c>.</summary>
    public static string LikePattern(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > MaxSearch) t = t[..MaxSearch];
        if (t.Length == 0) return "%";
        var b = new StringBuilder("%");
        foreach (var c in t)
        {
            if (c is '\\' or '%' or '_') b.Append('\\');
            b.Append(c);
        }
        return b.Append('%').ToString();
    }

    public async Task<PeoplePage> SearchAsync(string? text, PeopleFilter filter, IReadOnlyCollection<Guid> waiting, int staffLevel, int page,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        if (filter == PeopleFilter.Waiting && waiting.Count == 0) return new PeoplePage([], 0, page, PageSize);
        var where = new StringBuilder("CONCAT(u.FirstName, ' ', u.LastName) LIKE @q ESCAPE '\\\\'");
        var p = new List<(string, object)> { ("@q", LikePattern(text)) };
        var ids = string.Join(", ", waiting.Select((_, i) => "@w" + i));
        p.AddRange(waiting.Select((id, i) => ("@w" + i, (object)id.ToString())));
        switch (filter)
        {
            case PeopleFilter.Staff:
                where.Append(" AND u.UserLevel >= @staff");
                p.Add(("@staff", staffLevel));
                break;
            case PeopleFilter.Suspended:
                where.Append(" AND u.UserLevel < 0");
                if (waiting.Count > 0) where.Append($" AND u.PrincipalID NOT IN ({ids})");
                break;
            case PeopleFilter.Waiting:
                where.Append($" AND u.PrincipalID IN ({ids})");
                break;
        }
        return await WithConnectionAsync(async conn =>
        {
            await using var count = Command(conn, $"SELECT COUNT(*) FROM robust.UserAccounts u WHERE {where}", p);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            var query = $"{Columns} WHERE {where} ORDER BY u.LastName, u.FirstName LIMIT {PageSize} OFFSET {(page - 1) * PageSize}";
            return new PeoplePage(await ReadAsync(conn, query, p, ct), total, page, PageSize);
        });
    }

    public Task<PersonRow?> GetAsync(Guid principal, CancellationToken ct) =>
        WithConnectionAsync(async conn =>
            (await ReadAsync(conn, $"{Columns} WHERE u.PrincipalID = @id", [("@id", principal.ToString())], ct)).FirstOrDefault());

    private async Task<T> WithConnectionAsync<T>(Func<MySqlConnection, Task<T>> work)
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
            throw new PeopleDirectoryUnavailableException("The database could not be read.", e);
        }
    }

    private static MySqlCommand Command(MySqlConnection conn, string sql, IEnumerable<(string Name, object Value)> p)
    {
        var cmd = new MySqlCommand(sql, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private static async Task<IReadOnlyList<PersonRow>> ReadAsync(MySqlConnection conn, string sql, IEnumerable<(string, object)> p, CancellationToken ct)
    {
        await using var cmd = Command(conn, sql, p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PersonRow>();
        while (await reader.ReadAsync(ct))
        {
            // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
            if (AsGuid(reader.GetValue(0)) is not { } id) continue;
            var created = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5));
            var account = new RobustAccount(id, reader.GetString(1), reader.GetString(2), Convert.ToInt32(reader.GetValue(3)),
                reader.IsDBNull(4) ? "" : reader.GetString(4), UnixTime(created));
            var region = reader.IsDBNull(8) ? null : AsGuid(reader.GetValue(8));
            list.Add(new PersonRow(account, Stamp(reader, 6), Stamp(reader, 7), region == Guid.Empty ? null : region));
        }
        return list;
    }

    private static Guid? AsGuid(object value) =>
        value is Guid g ? g : Guid.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;

    // GridUser keeps Login and Logout as Unix seconds in text; "0" or empty is none.
    private static DateTimeOffset? Stamp(MySqlDataReader reader, int column) =>
        reader.IsDBNull(column) || !long.TryParse(Convert.ToString(reader.GetValue(column)), out var seconds) ? null : UnixTime(seconds);

    private static DateTimeOffset? UnixTime(long seconds) => seconds is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
