using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>The database can't be asked (it isn't running yet, or it answered with an error).</summary>
public sealed class StaffDirectoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The accounts at or above a level, for the Staff page (Robust's account service can only look up one name).</summary>
public interface IStaffDirectory
{
    /// <summary>Every account whose level is at least <paramref name="level"/>, by last then first name. Throws <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.</summary>
    Task<IReadOnlyList<RobustAccount>> ListAtOrAboveAsync(int level, CancellationToken ct);

    /// <summary>
    /// An account whose email, trimmed and lower-case, is <paramref name="normalEmail"/>, or null. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task<RobustAccount?> FindByEmailAsync(string normalEmail, CancellationToken ct);

    /// <summary>
    /// Every account whose email, trimmed and lower-case, is <paramref name="normalEmail"/>, at any level. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task<IReadOnlyList<RobustAccount>> ListByEmailAsync(string normalEmail, CancellationToken ct);
}

/// <summary>
/// <see cref="IStaffDirectory"/> on Robust's database, read as robustuser over the private MariaDB port. The settings are read per
/// call, so a changed port or password is used at once.
/// </summary>
public sealed class StaffDirectory(Func<LauncherSettings> settings) : IStaffDirectory
{
    // Short: a page waits on this and must never hang on a database that is down.
    private const uint TimeoutSeconds = 3;

    private const string Query =
        "SELECT PrincipalID, FirstName, LastName, UserLevel, Email, Created FROM robust.UserAccounts WHERE UserLevel >= @level ORDER BY LastName, FirstName";

    private const string ByEmail =
        "SELECT PrincipalID, FirstName, LastName, UserLevel, Email, Created FROM robust.UserAccounts WHERE LOWER(TRIM(Email)) = @email LIMIT 1";

    public const string AllByEmail =
        "SELECT PrincipalID, FirstName, LastName, UserLevel, Email, Created FROM robust.UserAccounts WHERE LOWER(TRIM(Email)) = @email";

    public Task<IReadOnlyList<RobustAccount>> ListAtOrAboveAsync(int level, CancellationToken ct) =>
        ReadAsync(Query, ("@level", level), ct);

    public async Task<RobustAccount?> FindByEmailAsync(string normalEmail, CancellationToken ct) =>
        (await ReadAsync(ByEmail, ("@email", normalEmail), ct)).FirstOrDefault();

    public Task<IReadOnlyList<RobustAccount>> ListByEmailAsync(string normalEmail, CancellationToken ct) =>
        ReadAsync(AllByEmail, ("@email", normalEmail), ct);

    private async Task<IReadOnlyList<RobustAccount>> ReadAsync(string query, (string Name, object Value) parameter, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<RobustAccount>();
            while (await reader.ReadAsync(ct))
            {
                // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
                if (reader.GetValue(0) is not Guid id && !Guid.TryParse(Convert.ToString(reader.GetValue(0)), out id)) continue;
                var created = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5));
                list.Add(new RobustAccount(id, reader.GetString(1), reader.GetString(2), Convert.ToInt32(reader.GetValue(3)),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    created is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(created) : null));
            }
            return list;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidCastException or FormatException or OverflowException)
        {
            throw new StaffDirectoryUnavailableException("The database could not be read.", e);
        }
    }
}
