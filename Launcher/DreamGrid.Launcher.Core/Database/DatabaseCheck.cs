using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>Advanced Settings' "Test connection": can robustuser open the robust database on the private port?</summary>
public static class DatabaseCheck
{
    private const uint TimeoutSeconds = 4;

    /// <summary>Null when the connection opened; otherwise the driver's message (never contains the password).</summary>
    public static async Task<string?> ConnectAsRobustUserAsync(LauncherSettings s, CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(
                MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            return null;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return e.Message;
        }
    }
}
