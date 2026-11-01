using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>The database can't be asked to change an email address (it isn't running, it answered with an error, or the account is gone).</summary>
public sealed class RobustEmailsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sets an account's email address, the one password recovery looks up.</summary>
public interface IRobustEmails
{
    /// <summary>Throws <see cref="RobustEmailsUnavailableException"/> when the address could not be written.</summary>
    Task SetAsync(Guid principal, string email, CancellationToken ct);
}

/// <summary>
/// <see cref="IRobustEmails"/> on Robust's database, written as robustuser over the private MariaDB port, as
/// <see cref="RobustPasswords"/> writes the password. Not through the console: <c>reset user email</c> exists, but a console line
/// can't safely carry any text a person types. The settings are read per call.
/// </summary>
public sealed class RobustEmails(Func<LauncherSettings> settings) : IRobustEmails
{
    private const uint TimeoutSeconds = 5;

    public const string Update = "UPDATE robust.UserAccounts SET Email = @email WHERE PrincipalID = @id";

    public async Task SetAsync(Guid principal, string email, CancellationToken ct)
    {
        int rows;
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(Update, conn);
            cmd.Parameters.AddWithValue("@id", principal.ToString("D"));
            cmd.Parameters.AddWithValue("@email", email);
            rows = await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new RobustEmailsUnavailableException("The email address could not be written.", e);
        }
        // MySqlConnector counts matched rows (UseAffectedRows is off by default), so none means no such account.
        if (rows == 0) throw new RobustEmailsUnavailableException("The account wasn't found.");
    }
}
