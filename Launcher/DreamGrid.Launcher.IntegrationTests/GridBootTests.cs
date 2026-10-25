using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Boots the real MariaDB, Robust and one region through <see cref="GridController"/>.</summary>
public sealed class GridBootTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Dréam Grid " + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        // DREAMGRID_TEST_KEEP=1 keeps the data directory (logs, generated INIs) for diagnosis.
        if (Environment.GetEnvironmentVariable("DREAMGRID_TEST_KEEP") == "1") return;
        for (var i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    /// <summary>Kills every process whose image or command line mentions the test's data directory.</summary>
    private static void KillEverythingUnder(string root)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId || !ProcessImage.Matches(p.Id, root)) continue;
                try { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static bool AnyProcessMatches(string image) =>
        Process.GetProcesses().Any(p => { using (p) return p.Id != Environment.ProcessId && ProcessImage.Matches(p.Id, image); });

    private static int[] FreePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            while (listeners.Count < count)
            {
                var l = new TcpListener(IPAddress.Any, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                try { new UdpClient(new IPEndPoint(IPAddress.Any, port)).Dispose(); }
                catch (SocketException) { l.Stop(); continue; } // UDP side busy (the region needs both)
                listeners.Add(l);
            }
            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally { foreach (var l in listeners) l.Stop(); }
    }

    private sealed record Rig(GridController Grid, LauncherPaths Paths, LauncherSettings Settings, RobustClient Robust);

    private Rig Make()
    {
        var bin = IntegrationEnvironment.MariaDbBin;
        Assert.SkipWhen(bin is null, "No MariaDB binaries (set DREAMGRID_MARIADB_BIN).");
        string opensim;
        try { opensim = IntegrationEnvironment.OpenSimBin; }
        catch (DirectoryNotFoundException) { Assert.Skip("No built bin/ with OpenSim.dll."); throw; }

        var paths = new LauncherPaths(_root, opensim, bin);
        paths.EnsureCreated();
        var store = new SettingsStore(paths.SettingsFile);
        var p = FreePorts(7);
        var settings = store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            GridName = "Test Grid",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        };
        store.Save(settings);

        var robust = new RobustClient(_http,
            new Uri($"http://127.0.0.1:{settings.RobustPrivatePort}"),
            new Uri($"http://127.0.0.1:{settings.RobustPublicPort}"));
        var grid = new GridController(paths, store, MariaDbBinaries.Locate(bin, OperatingSystem.IsWindows()),
            [new LogLevelModule()], robust, new SimpleHttpFactory(), TimeProvider.System);
        return new Rig(grid, paths, settings, robust);
    }

    private static string LogTail(LauncherPaths paths)
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(paths.Logs)) return "";
        foreach (var f in Directory.GetFiles(paths.Logs))
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                var lines = r.ReadToEnd().Split('\n');
                sb.AppendLine("==== " + Path.GetFileName(f));
                foreach (var l in lines.TakeLast(40)) sb.AppendLine(l.TrimEnd());
            }
            catch (IOException) { }
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Fresh_grid_boots_owner_and_region()
    {
        var (grid, paths, s, robust) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            var status = grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Database.State);
            Assert.Equal(ProcessState.Running, status.Robust.State);
            Assert.Null(status.Region);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);

            var account = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            Assert.Equal(200, account.UserLevel);
            Assert.True(await robust.AuthenticateAsync(account.PrincipalId, "pass-word1", ct));
            Assert.False(await robust.AuthenticateAsync(account.PrincipalId, "wrong", ct));

            // Task 11's /dreamgrid/regionready endpoint does not exist yet: stand in for it on the web port.
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            Assert.True(grid.GetStatus().LoginsEnabled);

            status = grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Region!.State);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);
            Assert.True(new SettingsStore(paths.SettingsFile).LoadOrCreate().Setup.RegionCreated);

            var dbPid = status.Database.Pid;
            Assert.NotNull(dbPid);
            await grid.StopAllAsync(ct);

            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")), "an OpenSim.dll process remains");
            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "Robust.dll")), "a Robust.dll process remains");
            Assert.False(ProcessImage.IsAlive(dbPid.Value), "MariaDB is still running");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
        }
    }

    [Fact]
    public async Task Port_conflict_on_Robust_public_port_is_reported_before_anything_starts()
    {
        var (grid, paths, s, _) = Make();
        var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        holder.Start();
        try
        {
            var e = await Assert.ThrowsAsync<PortConflictException>(() => grid.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(s.RobustPublicPort.ToString(), e.Message);
            Assert.Null(PidFile.Read(Path.Combine(paths.Pids, "mariadb.pid")));
            Assert.False(AnyProcessMatches(paths.MariaDbData), "MariaDB was started");
        }
        finally
        {
            holder.Stop();
            await grid.DetachAsync();
        }
    }

    private sealed class SimpleHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>
    /// A tiny HTTP/1.1 server on 127.0.0.1:<c>port</c> that hands every POSTed RegionReady alert to
    /// <c>onAlert</c> and answers 200. Raw sockets, so it needs no URL reservation on Windows.
    /// </summary>
    private sealed class AlertListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public AlertListener(int port, Action<RegionAlert> onAlert)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(onAlert));
        }

        private async Task AcceptLoop(Action<RegionAlert> onAlert)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var body = await ReadBody(client.GetStream());
                            if (RegionAlert.Parse(body) is { } alert) onAlert(alert);
                            var ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                            await client.GetStream().WriteAsync(ok);
                        }
                        catch (Exception) { /* a broken request is not the test's concern */ }
                    }
                });
            }
        }

        private static async Task<string> ReadBody(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
            {
                var n = await stream.ReadAsync(chunk);
                if (n == 0) return "";
                buffer.AddRange(chunk.AsSpan(0, n));
            }
            var headers = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
            var length = 0;
            foreach (var line in headers.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line["Content-Length:".Length..].Trim());
            var bodyStart = headerEnd + 4;
            while (buffer.Count - bodyStart < length)
            {
                var n = await stream.ReadAsync(chunk);
                if (n == 0) break;
                buffer.AddRange(chunk.AsSpan(0, n));
            }
            return Encoding.UTF8.GetString(buffer.ToArray(), bodyStart, Math.Min(length, buffer.Count - bodyStart));
        }

        private static int IndexOfHeaderEnd(List<byte> b)
        {
            for (var i = 0; i + 3 < b.Count; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
