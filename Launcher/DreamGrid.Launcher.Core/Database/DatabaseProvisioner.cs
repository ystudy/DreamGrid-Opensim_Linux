using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>Creates the robust and opensim databases and their users; safe to run repeatedly.</summary>
public sealed class DatabaseProvisioner(LauncherSettings s)
{
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s));
        await conn.OpenAsync(ct);

        await Provision(conn, "robust", "robustuser", s.Db.RobustPassword, ct);
        await Provision(conn, "opensim", "opensimuser", s.Db.OpensimPassword, ct);
    }

    private static async Task Provision(MySqlConnection conn, string database, string user, string password, CancellationToken ct)
    {
        var pw = MySqlHelper.EscapeString(password);
        await Run(conn, $"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4", ct);
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            await Run(conn, $"CREATE USER IF NOT EXISTS '{user}'@'{host}' IDENTIFIED BY '{pw}'", ct);
            await Run(conn, $"ALTER USER '{user}'@'{host}' IDENTIFIED BY '{pw}'", ct);
            await Run(conn, $"GRANT ALL ON `{database}`.* TO '{user}'@'{host}'", ct);
        }
    }

    private static async Task Run(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
