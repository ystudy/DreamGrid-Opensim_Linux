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

    private const string MarkerName = ".dreamgrid-initialized";

    /// <summary>True once install-db finished and root was secured (marker written last).</summary>
    public bool IsInitialized => File.Exists(MarkerPath);

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

    /// <summary>Connection string for one of the grid's database users over TCP to the private port.</summary>
    internal static string ConnectionString(LauncherSettings s, string database, string user, string password, uint timeoutSeconds) =>
        new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)s.MariaDbPort,
            Database = database,
            UserID = user,
            Password = password,
            Pooling = false,
            SslMode = MySqlSslMode.Disabled,
            ConnectionTimeout = timeoutSeconds,
            DefaultCommandTimeout = timeoutSeconds,
        }.ConnectionString;

    /// <summary>
    /// Creates the datadir and secures root. The marker is written only once root has its password; any
    /// failure empties the datadir (an uninitialized one holds no user data) so a retry starts clean.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsInitialized) return;
        ClearDataDir();
        try
        {
            await RunInstallDb(ct);
            await SecureWithTemporaryServer(ct);
            await File.WriteAllTextAsync(MarkerPath, "", ct);
        }
        catch
        {
            ClearDataDir();
            throw;
        }
    }

    private void ClearDataDir()
    {
        if (!Directory.Exists(paths.MariaDbData)) return;
        // Without the marker the datadir should hold nothing of value; the grid databases mean the marker was lost.
        if (!IsInitialized && new[] { "robust", "opensim" }.FirstOrDefault(d => Directory.Exists(Path.Combine(paths.MariaDbData, d))) is { } db)
            throw new InvalidOperationException(
                $"The MariaDB datadir {paths.MariaDbData} holds the '{db}' database but no {MarkerName} marker; refusing to empty it. "
                + $"If the data is still wanted, create an empty {MarkerName} file there; otherwise move the datadir away.");
        foreach (var entry in Directory.GetFileSystemEntries(paths.MariaDbData))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
    }

    private async Task SecureWithTemporaryServer(CancellationToken ct)
    {
        // Windows install-db already set the root password; on Linux root starts empty.
        var initialPassword = OperatingSystem.IsWindows() ? s.Db.RootPassword : "";
        using var server = Process.Start(Prepare(BootstrapStartInfo()))
            ?? throw new InvalidOperationException("Could not start mariadbd to secure the root account.");
        try
        {
            await WaitUntilAnswering(server, initialPassword, ct);
            await SecureRootAccounts(initialPassword, ct);
            await ShutdownAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await server.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("mariadbd did not stop after the initial setup.");
            }
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(true);
                await server.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private string MarkerPath => Path.Combine(paths.MariaDbData, MarkerName);

    public ProcessStartInfo StartInfo() => StartInfo(Path.Combine(paths.Pids, "mariadb.pid"));

    /// <summary>The root-securing temporary server: its own PID file, so <c>mariadb.pid</c> only ever names the supervised server.</summary>
    public ProcessStartInfo BootstrapStartInfo() => StartInfo(Path.Combine(paths.MariaDbTmp, "bootstrap.pid"));

    private ProcessStartInfo StartInfo(string pidFile)
    {
        Directory.CreateDirectory(paths.MariaDbTmp);
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
            "--pid-file=" + pidFile,
            "--log-error=" + Path.Combine(paths.Logs, "mariadb.err"),
            // mariadbd deletes every "#sql*" file in its tmpdir at startup: a shared one (/tmp, %TEMP%) would
            // destroy the temporary tables of any other MariaDB running or bootstrapping there.
            "--tmpdir=" + paths.MariaDbTmp,
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
        return Processes.ProcessImage.WithoutServiceManagerEnvironment(psi);
    }

    private async Task RunInstallDb(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunInstallDb(["--datadir=" + paths.MariaDbData, "--password=" + s.Db.RootPassword], ct);
            return;
        }

        var plan = PlanInstallDb(paths, Environment.IsPrivilegedProcess, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(paths.MariaDbTmp);
        try
        {
            // Inside the try, so the finally removes whichever link was made when the second one fails.
            if (plan.DataLink is not null) Directory.CreateSymbolicLink(plan.DataLink, paths.MariaDbData);
            if (plan.TmpLink is not null) Directory.CreateSymbolicLink(plan.TmpLink, paths.MariaDbTmp);
            await RunInstallDb(plan.Arguments.ToList(), ct);
        }
        finally
        {
            foreach (var l in new[] { plan.DataLink, plan.TmpLink })
                if (l is not null && Directory.Exists(l)) new DirectoryInfo(l).Delete();
        }
    }

    /// <summary>The Linux mariadb-install-db arguments, and the /tmp symlinks they need (<paramref name="linkId"/> names them).</summary>
    public sealed record InstallDbPlan(IReadOnlyList<string> Arguments, string? DataLink, string? TmpLink);

    public static InstallDbPlan PlanInstallDb(LauncherPaths paths, bool asRoot, string linkId)
    {
        // mariadb-install-db chowns the datadir unquoted when given --user, and word-splits every other option
        // it passes on (args="$args $arg", expanded unquoted), whoever runs it. A path with unusual characters
        // is therefore handed over through a space-free symlink with a random name: the datadir when root, the
        // tmpdir always.
        var link = asRoot && NeedsLink(paths.MariaDbData) ? $"/tmp/dg-{linkId}-data" : null;
        var tmpLink = NeedsLink(paths.MariaDbTmp) ? $"/tmp/dg-{linkId}-tmp" : null;
        var args = new List<string>
        {
            "--no-defaults", "--datadir=" + (link ?? paths.MariaDbData),
            // The bootstrap server's temporary tables must not sit in a tmpdir another mariadbd empties.
            "--tmpdir=" + (tmpLink ?? paths.MariaDbTmp),
            "--auth-root-authentication-method=normal", "--skip-test-db",
        };
        if (asRoot) args.Add("--user=root");
        return new InstallDbPlan(args, link, tmpLink);
    }

    private static bool NeedsLink(string path) =>
        path.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-'));

    private async Task RunInstallDb(List<string> args, CancellationToken ct)
    {
        var psi = Prepare(new ProcessStartInfo(bin.InstallDb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + bin.InstallDb);
        try
        {
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(bin.InstallDb)} failed with exit code {p.ExitCode}: {await stdout} {await stderr}");
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private async Task WaitUntilAnswering(Process server, string rootPassword, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited) throw new InvalidOperationException("mariadbd exited during initial setup; see mariadb.err.");
            try
            {
                await using var conn = new MySqlConnection(ConnectionString(s, rootPassword));
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { last = e; }
            await Task.Delay(300, ct);
        }
        throw new TimeoutException("mariadbd did not answer within 60 s during initial setup.", last);
    }

    /// <summary>Gives every root account the password and drops anonymous accounts.</summary>
    private async Task SecureRootAccounts(string currentPassword, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(ConnectionString(s, currentPassword));
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
