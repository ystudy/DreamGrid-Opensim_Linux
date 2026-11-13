using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Database;

/// <summary>The one way to open a connection to the grid's databases.</summary>
internal static class GridDb
{
    /// <summary>Opens a connection; on failure it is disposed and the driver's error is rethrown.</summary>
    public static async Task<MySqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new MySqlConnection(connectionString);
        try { await conn.OpenAsync(ct); }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return conn;
    }

    /// <summary>Opens a connection to the robust database as robustuser.</summary>
    public static Task<MySqlConnection> OpenRobustAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct) =>
        OpenAsync(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, timeoutSeconds), ct);

    /// <summary>Opens a connection to the opensim database as opensimuser.</summary>
    public static Task<MySqlConnection> OpenOpensimAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct) =>
        OpenAsync(MariaDbServer.ConnectionString(s, "opensim", "opensimuser", s.Db.OpensimPassword, timeoutSeconds), ct);
}
