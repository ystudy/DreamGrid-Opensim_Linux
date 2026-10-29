using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher;

/// <summary>
/// The verbs the installers call (<c>--version</c>, <c>admin-link</c>, <c>stop-grid</c>, <c>set-web-port</c>) and the
/// <c>--web-port</c> host option. Exit codes: 0 done, 2 bad arguments, a refused port or unreadable settings, 3 another
/// launcher holds the data directory, 4 the settings did not appear in time, 5 the grid's programs were still running
/// a minute after the stop.
/// </summary>
/// <remarks>
/// The installers set the web port with <c>set-web-port</c>, once, while the service is stopped. The host option
/// <c>--web-port &lt;n&gt;</c> (for development) applies the port again on every start, so a port changed later in
/// Advanced Settings → Network would be undone at the next start: it never goes into a service's command line.
/// </remarks>
public static class CommandLine
{
    private static readonly TimeSpan StopGridExitWait = TimeSpan.FromSeconds(60);

    /// <summary>The assembly's InformationalVersion as built (e.g. 0.0.0-dev+abc1234); the site footer shows the same text.</summary>
    public static string Version() => LauncherVersion.Current;

    /// <summary>The verb's exit code, or null when <paramref name="args"/> names no verb (the host runs). A verb is recognised only as the first argument.</summary>
    public static async Task<int?> RunVerbAsync(string[] args, IDictionary<string, string?> env, TextWriter output, CancellationToken ct)
    {
        switch (args.FirstOrDefault())
        {
            case "--version":
                output.WriteLine(Version());
                return 0;
            case "admin-link":
                return await Guarded(output, () => AdminLinkAsync(args, env, output, ct));
            case "stop-grid":
                return await Guarded(output, () => StopGridAsync(args, env, output, ct));
            case "set-web-port":
                return await Guarded(output, () => Task.FromResult(SetWebPort(args, env, output)));
            default:
                return null;
        }
    }

    /// <summary>The <c>--web-port</c> host option's value, or null when the flag is absent; a missing or non-numeric value throws.</summary>
    public static int? ParseWebPort(string[] args)
    {
        var i = Array.IndexOf(args, "--web-port");
        if (i < 0) return null;
        if (i == args.Length - 1 || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException("--web-port needs a whole number.");
        return port;
    }

    /// <summary>
    /// Saves <paramref name="port"/> as the web port, creating the settings when there are none yet; returns the
    /// validator's message instead when the port is not allowed (then nothing is created or changed).
    /// </summary>
    public static string? ApplyWebPort(SettingsStore store, int port)
    {
        var saved = store.TryLoad();
        var ports = saved ?? PortDefaults;
        var result = SettingsValidator.Network(ports.Hostname, port, ports.RobustPublicPort, ports.RobustPrivatePort,
            ports.RegionPort, ports.MariaDbPort, ports.RobustConsolePort, ports.RegionConsolePort);
        if (result.Errors.TryGetValue("WebPort", out var message)) return message;
        var s = saved ?? store.LoadOrCreate();
        if (s.WebPort != port) store.Save(s with { WebPort = port });
        return null;
    }

    /// <summary>Settings with the default ports, for checking a port before any settings exist (only the ports are read).</summary>
    private static readonly LauncherSettings PortDefaults = new()
    {
        Db = new DatabaseSecrets("", "", ""), Console = new ConsoleSecrets("", ""), MachineId = "",
    };

    private static async Task<int> Guarded(TextWriter output, Func<Task<int>> verb)
    {
        try
        {
            return await verb();
        }
        catch (Exception e) when (e is ArgumentException or SettingsCorruptException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine(e.Message);
            return 2;
        }
    }

    private static async Task<int> AdminLinkAsync(string[] args, IDictionary<string, string?> env, TextWriter output, CancellationToken ct)
    {
        var dataDir = LauncherPaths.ResolveData(args, env, OperatingSystem.IsWindows());
        var wait = Value(args, "--wait") is { } w
            ? int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds
                : throw new ArgumentException("--wait needs a number of seconds.")
            : 0;
        var store = new SettingsStore(Path.Combine(dataDir, "settings.json"));

        // The installer may call this while the service is still creating the settings: wait for the file, never make it.
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        LauncherSettings? s;
        while ((s = store.TryLoad()) is null)
        {
            if (DateTime.UtcNow >= deadline)
            {
                output.WriteLine($"No settings found in {dataDir}. Is the DreamGrid service running?");
                return 4;
            }
            await Task.Delay(250, ct);
        }

        var path = !s.Setup.OwnerCreated && s.SetupToken is { } token ? $"/setup?token={token}" : "/";
        output.WriteLine($"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}{path}");
        if (args.Contains("--open"))
        {
            try
            {
                Process.Start(new ProcessStartInfo($"http://localhost:{s.WebPort}{path}") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                output.WriteLine($"The browser could not be opened: {e.Message}");
            }
        }
        return 0;
    }

    private static async Task<int> StopGridAsync(string[] args, IDictionary<string, string?> env, TextWriter output, CancellationToken ct)
    {
        var paths = LauncherPaths.Resolve(args, env, OperatingSystem.IsWindows());
        using var instanceLock = InstanceLock.TryAcquire(paths.Data);
        if (instanceLock is null)
        {
            output.WriteLine($"Another DreamGrid launcher is already running with data directory {paths.Data}. Stop the service first.");
            return 3;
        }

        var store = new SettingsStore(paths.SettingsFile);
        if (store.TryLoad() is null)
        {
            output.WriteLine("There are no settings yet, so there is no grid to stop.");
            return 0;
        }

        // A killed process may take a moment to exit and release its files, and the uninstaller deletes the runtime
        // folders next: wait for every child recorded before the stop.
        var children = LiveChildPids(paths.Pids);
        var services = new ServiceCollection().AddGrid(paths, store);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<GridController>().StopAllAsync(ct);
        if (!await WaitUntilGoneAsync(children, StopGridExitWait, ct))
        {
            output.WriteLine($"Some of the grid's programs were still running {StopGridExitWait.TotalSeconds:0} seconds after the stop.");
            return 5;
        }
        output.WriteLine("The grid is stopped.");
        return 0;
    }

    /// <summary>
    /// <c>set-web-port &lt;n&gt; --data &lt;dir&gt;</c>: saves the web port while no launcher runs (the installers stop the
    /// service first). This is the one moment the settings may be created outside the launcher's host.
    /// </summary>
    private static int SetWebPort(string[] args, IDictionary<string, string?> env, TextWriter output)
    {
        if (args.Length < 2 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException("set-web-port needs a port number: set-web-port <port> --data <dir>.");
        var dataDir = LauncherPaths.ResolveData(args, env, OperatingSystem.IsWindows());
        using var instanceLock = InstanceLock.TryAcquire(dataDir);
        if (instanceLock is null)
        {
            output.WriteLine($"Another DreamGrid launcher is already running with data directory {dataDir}. Stop the service first.");
            return 3;
        }

        if (ApplyWebPort(new SettingsStore(Path.Combine(dataDir, "settings.json")), port) is { } error)
        {
            output.WriteLine(error);
            return 2;
        }
        output.WriteLine($"The admin website will use port {port}.");
        return 0;
    }

    /// <summary>The running processes named by the PID files in <paramref name="pidsDir"/> (none when it does not exist); one this account may not inspect counts as running.</summary>
    public static IReadOnlyList<int> LiveChildPids(string pidsDir)
    {
        if (!Directory.Exists(pidsDir)) return [];
        return Directory.EnumerateFiles(pidsDir, "*.pid")
            .Select(PidFile.Read)
            .OfType<int>()
            .Where(MayBeRunning)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Waits until every process in <paramref name="pids"/> has exited; false when one still runs after
    /// <paramref name="timeout"/>. A process this account may not inspect counts as still running.
    /// </summary>
    public static async Task<bool> WaitUntilGoneAsync(IReadOnlyCollection<int> pids, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (pids.Any(MayBeRunning))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(200, ct);
        }
        return true;
    }

    private static bool MayBeRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // HasExited misses a zombie on Linux; IsAlive also checks /proc.
            return !process.HasExited && ProcessImage.IsAlive(pid);
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }  // it exited while being looked at
        catch (Win32Exception) { return true; }              // access denied: it exists
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }
}
