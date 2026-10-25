using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>The launcher's private MariaDB: initialise the datadir, build start arguments, ping and shut down.</summary>
public sealed class MariaDbServer(MariaDbBinaries bin, LauncherPaths paths, LauncherSettings s)
{
    /// <summary>UNIX socket paths are limited to 107 bytes (sun_path minus the terminator).</summary>
    private const int MaxSocketBytes = 107;

    public bool IsInitialized => Directory.Exists(Path.Combine(paths.MariaDbData, "mysql"));

    /// <summary>The server socket: inside the data directory when short enough, else a hashed path under /tmp.</summary>
    public static string SocketPathFor(string data)
    {
        var inside = data.TrimEnd('/') + "/mariadb/mariadb.sock";
        if (Encoding.UTF8.GetByteCount(inside) <= MaxSocketBytes) return inside;
        return TempPathFor(data, ".sock");
    }

    private static string TempPathFor(string data, string suffix)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)), 0, 8).ToLowerInvariant();
        return $"/tmp/dg-{hash}{suffix}";
    }

    /// <summary>Connection string for root over TCP to the private port (the password may be empty during setup).</summary>
    internal static string ConnectionString(LauncherSettings s, string? rootPassword = null) =>
        new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)s.MariaDbPort,
            UserID = "root",
            Password = rootPassword ?? s.Db.RootPassword,
            Pooling = false,
            SslMode = MySqlSslMode.Disabled,
            ConnectionTimeout = 5,
        }.ConnectionString;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunInstallDb(["--datadir=" + paths.MariaDbData, "--password=" + s.Db.RootPassword], ct);
            return;
        }

        // mariadb-install-db chowns the datadir unquoted when given --user, so a path with whitespace
        // is handed over through a space-free symlink.
        var asRoot = Environment.IsPrivilegedProcess;
        var link = asRoot && paths.MariaDbData.Any(char.IsWhiteSpace) ? TempPathFor(paths.MariaDbData, "-data") : null;
        if (link is not null)
        {
            if (Directory.Exists(link) || File.Exists(link)) new DirectoryInfo(link).Delete();
            Directory.CreateSymbolicLink(link, paths.MariaDbData);
        }
        try
        {
            var args = new List<string>
            {
                "--no-defaults", "--datadir=" + (link ?? paths.MariaDbData),
                "--auth-root-authentication-method=normal", "--skip-test-db",
            };
            if (asRoot) args.Add("--user=root");
            await RunInstallDb(args, ct);
        }
        finally
        {
            if (link is not null && Directory.Exists(link)) new DirectoryInfo(link).Delete();
        }

        // Root starts with an empty password; set it, then stop the server again.
        using var server = Process.Start(Prepare(StartInfo()))
            ?? throw new InvalidOperationException("Could not start mariadbd to secure the root account.");
        try
        {
            await WaitUntilAnswering(server, "", ct);
            await SecureRootAccounts(ct);
            await ShutdownAsync(ct);
            if (!server.WaitForExit(30_000))
                throw new InvalidOperationException("mariadbd did not stop after the initial setup.");
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
        }
    }

    public ProcessStartInfo StartInfo()
    {
        var psi = new ProcessStartInfo(bin.Server);
        foreach (var a in new[]
        {
            "--no-defaults",
            "--datadir=" + paths.MariaDbData,
            "--port=" + s.MariaDbPort,
            "--bind-address=127.0.0.1",
            "--skip-name-resolve",
            "--character-set-server=utf8mb4",
            "--collation-server=utf8mb4_unicode_ci",
            "--pid-file=" + Path.Combine(paths.Pids, "mariadb.pid"),
            "--log-error=" + Path.Combine(paths.Logs, "mariadb.err"),
        })
            psi.ArgumentList.Add(a);

        if (!OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("--socket=" + SocketPathFor(paths.Data));
            // mariadbd refuses to run as root unless told to; the service-account case is plan 3's.
            if (Environment.IsPrivilegedProcess) psi.ArgumentList.Add("--user=root");
        }
        return psi;
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(ConnectionString(s));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SELECT 1", conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(ConnectionString(s));
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SHUTDOWN", conn);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (MySqlException) when (!ct.IsCancellationRequested) { /* the connection drops as the server stops */ }
    }

    private static ProcessStartInfo Prepare(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return psi;
    }

    private async Task RunInstallDb(List<string> args, CancellationToken ct)
    {
        var psi = Prepare(new ProcessStartInfo(bin.InstallDb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + bin.InstallDb);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(bin.InstallDb)} failed with exit code {p.ExitCode}: {await stdout} {await stderr}");
    }

    private async Task WaitUntilAnswering(Process server, string rootPassword, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited) throw new InvalidOperationException("mariadbd exited during initial setup; see mariadb.err.");
            try
            {
                await using var conn = new MySqlConnection(ConnectionString(s, rootPassword));
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            await Task.Delay(300, ct);
        }
        throw new TimeoutException("mariadbd did not answer within 60 s during initial setup.");
    }

    /// <summary>Gives every root account the password and drops anonymous accounts.</summary>
    private async Task SecureRootAccounts(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(ConnectionString(s, ""));
        await conn.OpenAsync(ct);

        async Task<List<string>> Hosts(string user)
        {
            var hosts = new List<string>();
            await using var cmd = new MySqlCommand("SELECT Host FROM mysql.global_priv WHERE User = @u", conn);
            cmd.Parameters.AddWithValue("@u", user);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) hosts.Add(r.GetString(0));
            return hosts;
        }

        async Task Run(string sql)
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var pw = MySqlHelper.EscapeString(s.Db.RootPassword);
        // The private server is only reached through loopback; the extra root@<machine-name> account
        // install-db creates is dropped rather than left behind with an empty password.
        var loopback = new[] { "localhost", "127.0.0.1", "::1" };
        foreach (var host in await Hosts("root"))
        {
            var h = MySqlHelper.EscapeString(host);
            await Run(loopback.Contains(host)
                ? $"ALTER USER 'root'@'{h}' IDENTIFIED BY '{pw}'"
                : $"DROP USER 'root'@'{h}'");
        }
        foreach (var host in await Hosts(""))
            await Run($"DROP USER ''@'{MySqlHelper.EscapeString(host)}'");
    }
}
