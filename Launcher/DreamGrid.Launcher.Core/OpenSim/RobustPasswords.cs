using System.Security.Cryptography;
using System.Text;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>The database can't be asked to change a password (it isn't running, or it answered with an error).</summary>
public sealed class RobustPasswordsUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Sets an account's grid password, the one the viewer and the website both check.</summary>
public interface IRobustPasswords
{
    /// <summary>Throws <see cref="RobustPasswordsUnavailableException"/> when the password could not be written.</summary>
    Task SetAsync(Guid principal, string password, CancellationToken ct);
}

/// <summary>OpenSim's password hash (<c>AuthenticationServiceBase.SetPassword</c>): md5(md5(password) + ":" + salt), lower-case hex.</summary>
public static class OpenSimPassword
{
    public static string Hash(string password, string salt) => Md5Hex(Md5Hex(password) + ":" + salt);

    /// <summary>A new salt as OpenSim makes one: the MD5 hex of a random UUID.</summary>
    public static string NewSalt() => Md5Hex(Guid.NewGuid().ToString());

    private static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>
/// <see cref="IRobustPasswords"/> on Robust's database, written as robustuser over the private MariaDB port, the same row
/// OpenSim's own SetPassword writes. Robust's <c>setpassword</c> call is not used: it would need <c>AllowSetPassword</c> on the
/// private port, which is unauthenticated and open to the LAN. The settings are read per call.
/// </summary>
public sealed class RobustPasswords(Func<LauncherSettings> settings) : IRobustPasswords
{
    private const uint TimeoutSeconds = 5;

    private const string Upsert =
        "INSERT INTO robust.auth (UUID, passwordHash, passwordSalt, webLoginKey, accountType) " +
        "VALUES (@id, @hash, @salt, '00000000-0000-0000-0000-000000000000', 'UserAccount') " +
        "ON DUPLICATE KEY UPDATE passwordHash = @hash, passwordSalt = @salt";

    public async Task SetAsync(Guid principal, string password, CancellationToken ct)
    {
        var salt = OpenSimPassword.NewSalt();
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(Upsert, conn);
            cmd.Parameters.AddWithValue("@id", principal.ToString("D"));
            cmd.Parameters.AddWithValue("@hash", OpenSimPassword.Hash(password, salt));
            cmd.Parameters.AddWithValue("@salt", salt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new RobustPasswordsUnavailableException("The password could not be written.", e);
        }
    }
}
