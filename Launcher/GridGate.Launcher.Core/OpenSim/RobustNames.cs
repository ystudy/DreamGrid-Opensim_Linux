using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.OpenSim;

/// <summary>The database can't be asked to rename an avatar (it isn't running, it answered with an error, or the account is gone).</summary>
public sealed class RobustNamesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Renames an avatar: its first and last name in Robust's accounts.</summary>
public interface IRobustNames
{
    /// <summary>
    /// Throws <see cref="NameTakenException"/> when another account has that name in any case, and
    /// <see cref="RobustNamesUnavailableException"/> when it could not be written.
    /// </summary>
    Task SetAsync(Guid principal, string first, string last, CancellationToken ct);
}

/// <summary>
/// <see cref="IRobustNames"/> on Robust's database, written as robustuser over the private MariaDB port like
/// <see cref="RobustEmails"/>. Everything else (friends, groups, inventory, regions) is tied to the account's ID, so only
/// <c>UserAccounts</c> changes; Robust's account service keeps no cache, and regions read the new name at the next login. The
/// taken check and the write are one transaction, and the table's collation compares without case, as the login does.
/// </summary>
public sealed class RobustNames(Func<LauncherSettings> settings) : IRobustNames
{
    private const uint TimeoutSeconds = 5;

    public const string Taken =
        "SELECT COUNT(*) FROM robust.UserAccounts WHERE FirstName = @first AND LastName = @last AND PrincipalID <> @id FOR UPDATE";

    public const string Update = "UPDATE robust.UserAccounts SET FirstName = @first, LastName = @last WHERE PrincipalID = @id";

    public async Task SetAsync(Guid principal, string first, string last, CancellationToken ct)
    {
        int rows;
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var check = Command(Taken, conn, tx, principal, first, last))
                if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) throw new NameTakenException(first, last);
            await using (var update = Command(Update, conn, tx, principal, first, last))
                rows = await update.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        // A unique index on the name, where Robust has one, says the same as the check.
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new NameTakenException(first, last);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new RobustNamesUnavailableException("The name could not be written.", e);
        }
        // MySqlConnector counts matched rows (UseAffectedRows is off by default), so none means no such account.
        if (rows == 0) throw new RobustNamesUnavailableException("The account wasn't found.");
    }

    private static MySqlCommand Command(string sql, MySqlConnection conn, MySqlTransaction tx, Guid principal, string first, string last)
    {
        var cmd = new MySqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", principal.ToString("D"));
        cmd.Parameters.AddWithValue("@first", first);
        cmd.Parameters.AddWithValue("@last", last);
        return cmd;
    }
}
