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

    /// <summary>Opens a connection to the money database as moneyuser (money design §7.1).</summary>
    public static Task<MySqlConnection> OpenMoneyAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct) =>
        OpenAsync(MariaDbServer.ConnectionString(s, "money", "moneyuser", s.Db.MoneyPassword, timeoutSeconds), ct);

    /// <summary>
    /// Opens a connection, runs <paramref name="work"/> and disposes the connection. A database failure is rethrown as
    /// whatever <paramref name="unavailable"/> makes of it; any other exception passes through.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<MySqlConnection>> open, Func<MySqlConnection, Task<T>> work, Func<Exception, Exception> unavailable)
    {
        try
        {
            await using var conn = await open();
            return await work(conn);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidCastException or FormatException or OverflowException)
        {
            throw unavailable(e);
        }
    }

    public static MySqlCommand Command(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    public static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
