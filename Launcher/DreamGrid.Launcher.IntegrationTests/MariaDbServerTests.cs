using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;

namespace DreamGrid.Launcher.IntegrationTests;

public sealed class MariaDbServerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Dréam Grid " + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            p.Dispose();
        }
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch { Thread.Sleep(500); }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Password() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private (MariaDbServer Server, LauncherPaths Paths, LauncherSettings Settings) Make(string? subDir = null)
    {
        var bin = IntegrationEnvironment.RequireMariaDbBin();
        var data = subDir is null ? _root : Path.Combine(_root, subDir);
        var paths = new LauncherPaths(data, IntegrationEnvironment.OpenSimBin, bin);
        paths.EnsureCreated();
        var settings = new LauncherSettings
        {
            Db = new DatabaseSecrets(Password(), Password(), Password()),
            Console = new ConsoleSecrets("u", Password()),
            MachineId = "test",
            MariaDbPort = FreePort(),
        };
        var server = new MariaDbServer(MariaDbBinaries.Locate(bin, OperatingSystem.IsWindows()), paths, settings);
        return (server, paths, settings);
    }

    private Process Start(MariaDbServer server)
    {
        var psi = server.StartInfo();
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private static async Task WaitForPing(MariaDbServer server, Process p)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(p.HasExited, "mariadbd exited while waiting for it to answer.");
            if (await server.PingAsync(TestContext.Current.CancellationToken)) return;
            await Task.Delay(500);
        }
        Assert.Fail("MariaDB did not answer within 60 s.");
    }

    private static async Task AssertConnectsTo(LauncherSettings s, string user, string password, string database)
    {
        var cs = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = user,
            Password = password, Database = database,
        }.ConnectionString;
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new MySqlCommand("SELECT DATABASE()", conn);
        Assert.Equal(database, (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initialize_start_provision_and_connect_as_robustuser()
    {
        var (server, _, s) = Make();
        Assert.False(server.IsInitialized);
        await server.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(server.IsInitialized);

        var p = Start(server);
        await WaitForPing(server, p);
        await new DatabaseProvisioner(s).EnsureAsync(TestContext.Current.CancellationToken);

        await AssertConnectsTo(s, "robustuser", s.Db.RobustPassword, "robust");

        await server.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.True(p.WaitForExit(30_000), "mariadbd did not exit within 30 s of SHUTDOWN.");
    }

    [Fact]
    public async Task Root_requires_the_password_after_initialize()
    {
        var (server, _, s) = Make();
        await server.InitializeAsync(TestContext.Current.CancellationToken);
        var p = Start(server);
        await WaitForPing(server, p);

        var noPassword = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = "root", Password = "",
        }.ConnectionString;
        await using var conn = new MySqlConnection(noPassword);
        await Assert.ThrowsAsync<MySqlException>(() => conn.OpenAsync(TestContext.Current.CancellationToken));

        // Only loopback root accounts may remain, and no anonymous ones.
        var tokenX = TestContext.Current.CancellationToken;
        await using (var admin = new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = "root", Password = s.Db.RootPassword,
        }.ConnectionString))
        {
            await admin.OpenAsync(tokenX);
            await using var cmd = new MySqlCommand(
                "SELECT User, Host FROM mysql.global_priv WHERE User = 'root' OR User = ''", admin);
            await using var r = await cmd.ExecuteReaderAsync(tokenX);
            var loopback = new[] { "localhost", "127.0.0.1", "::1" };
            var rows = 0;
            while (await r.ReadAsync(tokenX))
            {
                rows++;
                Assert.Equal("root", r.GetString(0));
                Assert.Contains(r.GetString(1), loopback);
            }
            Assert.True(rows > 0);
        }

        // No anonymous account may let an arbitrary user in without a password.
        var anonymous = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = "nobody", Password = "",
        }.ConnectionString;
        await using var anon = new MySqlConnection(anonymous);
        await Assert.ThrowsAsync<MySqlException>(() => anon.OpenAsync(TestContext.Current.CancellationToken));

        await server.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.True(p.WaitForExit(30_000));
    }

    [Fact]
    public async Task A_failed_initialization_is_retried_from_a_clean_datadir()
    {
        var (server, paths, s) = Make();

        // Hold the port so the temporary setup server cannot start; Windows initialises with the same flow.
        var holder = new TcpListener(IPAddress.Loopback, s.MariaDbPort);
        holder.Start();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => server.InitializeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            holder.Stop();
        }
        Assert.False(server.IsInitialized);
        Assert.Empty(Directory.GetFileSystemEntries(paths.MariaDbData));

        await server.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(server.IsInitialized);
        var p = Start(server);
        await WaitForPing(server, p);

        var noPassword = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = "root", Password = "",
        }.ConnectionString;
        await using var conn = new MySqlConnection(noPassword);
        await Assert.ThrowsAsync<MySqlException>(() => conn.OpenAsync(TestContext.Current.CancellationToken));

        await server.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.True(p.WaitForExit(30_000));
    }

    [Fact]
    public async Task Provisioning_twice_is_idempotent_and_applies_a_changed_password()
    {
        var (server, _, s) = Make();
        await server.InitializeAsync(TestContext.Current.CancellationToken);
        var p = Start(server);
        await WaitForPing(server, p);

        await new DatabaseProvisioner(s).EnsureAsync(TestContext.Current.CancellationToken);
        await new DatabaseProvisioner(s).EnsureAsync(TestContext.Current.CancellationToken);

        var changed = s with { Db = s.Db with { OpensimPassword = Password() } };
        await new DatabaseProvisioner(changed).EnsureAsync(TestContext.Current.CancellationToken);

        await AssertConnectsTo(s, "opensimuser", changed.Db.OpensimPassword, "opensim");
        await Assert.ThrowsAsync<MySqlException>(
            () => AssertConnectsTo(s, "opensimuser", s.Db.OpensimPassword, "opensim"));

        await server.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.True(p.WaitForExit(30_000));
    }

    [Fact]
    public async Task Port_in_use_fails_fast_with_named_port()
    {
        var (server, paths, s) = Make();
        await server.InitializeAsync(TestContext.Current.CancellationToken);

        using var holder = new TcpListener(IPAddress.Loopback, s.MariaDbPort);
        holder.Start();

        var p = Start(server);
        Assert.True(p.WaitForExit(20_000), "mariadbd did not exit although its port was taken.");
        Assert.NotEqual(0, p.ExitCode);
        var err = File.ReadAllText(Path.Combine(paths.Logs, "mariadb.err"));
        Assert.True(err.Contains("Address already in use") || err.Contains("Can't start server")
                    || err.Contains("Do you already have another"), err);
    }

    [Fact]
    public async Task A_very_long_data_directory_still_starts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows MAX_PATH is the limit there, not the socket.");
        // On Linux this exceeds the 107-byte UNIX socket limit and must use the /tmp fallback.
        var (server, _, _) = Make(new string('p', 70) + "/" + new string('q', 70));
        await server.InitializeAsync(TestContext.Current.CancellationToken);
        var p = Start(server);
        await WaitForPing(server, p);
        await server.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.True(p.WaitForExit(30_000));
    }
}
