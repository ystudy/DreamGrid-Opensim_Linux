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

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Helpers shared by the tests that boot a real grid (in process or through the launcher).</summary>
internal static class GridTestSupport
{
    /// <summary>A fresh data directory with a space and a non-ASCII letter in its name.</summary>
    public static string NewDataDir() => Path.Combine(Path.GetTempPath(), "Dréam Grid " + Guid.NewGuid().ToString("N"));

    /// <summary>Paths for <paramref name="root"/>; skips (or fails in CI) without MariaDB or a built bin/.</summary>
    public static LauncherPaths RequirePaths(string root)
    {
        var mariaBin = IntegrationEnvironment.RequireMariaDbBin();
        var opensim = IntegrationEnvironment.RequireOpenSimBin();
        var paths = new LauncherPaths(root, opensim, mariaBin);
        paths.EnsureCreated();
        return paths;
    }

    /// <summary>Writes settings.json through the real store: fresh secrets, loopback host, free ports.</summary>
    public static LauncherSettings SeedSettings(SettingsStore store)
    {
        var p = FreePorts(7);
        var settings = store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            GridName = "Test Grid",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        };
        store.Save(settings);
        return settings;
    }

    public static RobustClient NewRobustClient(HttpClient http, LauncherSettings s) =>
        new(http, new Uri($"http://127.0.0.1:{s.RobustPrivatePort}"), new Uri($"http://127.0.0.1:{s.RobustPublicPort}"));

    /// <summary>A controller wired the way the host wires it (Program.cs).</summary>
    public static GridController NewController(LauncherPaths paths, SettingsStore store, IRobustClient robust) =>
        new(paths, store, MariaDbBinaries.Locate(paths.MariaDbBin, OperatingSystem.IsWindows()),
            [new LogLevelModule()], robust, new SimpleHttpFactory(), TimeProvider.System);

    /// <summary>Kills every process whose image or command line mentions the test's data directory.</summary>
    public static void KillEverythingUnder(string root)
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

    /// <summary>How many processes (other than this one) have <paramref name="image"/> in their image or command line.</summary>
    public static int CountProcessesMatching(string image) =>
        Process.GetProcesses().Count(p => { using (p) return p.Id != Environment.ProcessId && ProcessImage.Matches(p.Id, image); });

    public static bool AnyProcessMatches(string image) => CountProcessesMatching(image) > 0;

    /// <summary>The PID files under <see cref="LauncherPaths.Pids"/> that still name a live process of this data directory.</summary>
    public static IReadOnlyList<string> LivePidFiles(LauncherPaths paths)
    {
        if (!Directory.Exists(paths.Pids)) return [];
        return Directory.GetFiles(paths.Pids, "*.pid")
            .Where(f => PidFile.Read(f) is { } pid && ProcessImage.Matches(pid, paths.Data))
            .Select(f => $"{Path.GetFileName(f)} ({PidFile.Read(f)})")
            .ToList();
    }

    /// <summary>Ports that are free for both TCP and UDP (the region needs both on its port).</summary>
    public static int[] FreePorts(int count)
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

    /// <summary>The last 40 lines of every log under <c>state/logs</c>, for failure messages.</summary>
    public static string LogTail(LauncherPaths paths)
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

    /// <summary>Deletes the data directory unless DREAMGRID_TEST_KEEP=1 (logs and generated INIs kept for diagnosis).</summary>
    public static void DeleteDataDir(string root)
    {
        if (Environment.GetEnvironmentVariable("DREAMGRID_TEST_KEEP") == "1") return;
        for (var i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }
}

internal sealed class SimpleHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(100) };
}

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1:<c>port</c> that hands every POSTed RegionReady alert to
/// <c>onAlert</c> and answers 200. It stands in for the host's /dreamgrid/regionready when a test drives
/// <see cref="GridController"/> in process. Raw sockets, so it needs no URL reservation on Windows.
/// </summary>
internal sealed class AlertListener : IDisposable
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
