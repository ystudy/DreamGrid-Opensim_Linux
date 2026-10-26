using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher;

/// <summary>
/// The verbs the installers call (<c>--version</c>, <c>admin-link</c>, <c>stop-grid</c>) and the <c>--web-port</c>
/// startup option. Exit codes: 0 done, 2 bad arguments or unreadable settings, 3 another launcher holds the data
/// directory, 4 the settings did not appear in time.
/// </summary>
public static class CommandLine
{
    /// <summary>The build version without the "+source revision" suffix; the site footer shows the same text.</summary>
    public static string Version()
    {
        var v = typeof(CommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return "dev";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    /// <summary>The verb's exit code, or null when <paramref name="args"/> names no verb (the host runs).</summary>
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
            default:
                return null;
        }
    }

    /// <summary>The <c>--web-port</c> value, or null when the flag is absent; a missing or non-numeric value throws.</summary>
    public static int? ParseWebPort(string[] args)
    {
        var i = Array.IndexOf(args, "--web-port");
        if (i < 0) return null;
        if (i == args.Length - 1 || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException("--web-port needs a whole number.");
        return port;
    }

    /// <summary>Saves <paramref name="port"/> as the web port when it differs; returns the validator's message instead when it is not allowed.</summary>
    public static string? ApplyWebPort(SettingsStore store, int port)
    {
        var s = store.LoadOrCreate();
        if (s.WebPort == port) return null;
        var next = s with { WebPort = port };
        var result = SettingsValidator.Network(next.Hostname, next.WebPort, next.RobustPublicPort, next.RobustPrivatePort,
            next.RegionPort, next.MariaDbPort, next.RobustConsolePort, next.RegionConsolePort);
        if (result.Errors.TryGetValue("WebPort", out var message)) return message;
        store.Save(next);
        return null;
    }

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

        var services = new ServiceCollection().AddGrid(paths, store);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<GridController>().StopAllAsync(ct);
        output.WriteLine("The grid is stopped.");
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }
}
