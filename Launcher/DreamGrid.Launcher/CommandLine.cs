using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher;

/// <summary>
/// The verbs the installers and the owner call (<c>--version</c>, <c>admin-link</c>, <c>stop-grid</c>, <c>set-web-port</c>, <c>reset-two-factor</c>) and the
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
    /// <param name="foreignDataOwner">Who owns a data folder when it isn't this account (<see cref="ForeignDataOwner"/>; tests replace it).</param>
    public static async Task<int?> RunVerbAsync(string[] args, IDictionary<string, string?> env, TextWriter output, CancellationToken ct,
        Func<string, string?>? foreignDataOwner = null)
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
            case "reset-two-factor":
                return await Guarded(output, () => Task.FromResult(ResetTwoFactor(args, env, output, foreignDataOwner ?? ForeignDataOwner)));
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

    /// <summary>
    /// <c>reset-two-factor [--data &lt;dir&gt;]</c>: turns two-factor sign-in off for the owner (<c>Setup.Owner</c>), the way back in for
    /// an owner who lost their phone. <c>reset-two-factor --all-damaged</c>: sets a damaged <c>twofactor.json</c> aside (everyone
    /// then sets two-factor up again). It edits <c>&lt;data&gt;/security/twofactor.json</c> directly, with a store that needs no data
    /// protection key (taking someone out needs no decryption), so it works with the launcher running or stopped: the launcher
    /// reads that file on every check. On Unix it runs only as the account that owns the data folder: a file root wrote would be
    /// root's alone, the service (user <c>dreamgrid</c>) could no longer read it, and every staff sign-in would be refused.
    /// Exit 0 done, 2 with a one-line reason.
    /// </summary>
    private static int ResetTwoFactor(string[] args, IDictionary<string, string?> env, TextWriter output, Func<string, string?> foreignDataOwner)
    {
        var dataDir = LauncherPaths.ResolveData(args, env, OperatingSystem.IsWindows());
        if (!Directory.Exists(dataDir))
        {
            output.WriteLine($"The data folder {dataDir} does not exist. Pass --data <folder> if the grid keeps its data elsewhere.");
            return 2;
        }
        string? account;
        try
        {
            account = foreignDataOwner(dataDir);
        }
        catch (DataOwnerUnknownException)
        {
            output.WriteLine($"The owner of {dataDir} couldn't be read, so this command won't run as root; run the command as the account that owns the data folder:");
            output.WriteLine($"sudo runuser -u dreamgrid -- {SameCommand(args)}");
            return 2;
        }
        if (account is not null)
        {
            output.WriteLine($"Run this as {account}, the account that owns {dataDir}, so the grid can still read what it changes:");
            output.WriteLine($"sudo runuser -u {account} -- {SameCommand(args)}");
            return 2;
        }
        try
        {
            return ResetTwoFactorIn(dataDir, args.Contains("--all-damaged"), output);
        }
        catch (UnauthorizedAccessException)
        {
            output.WriteLine(OperatingSystem.IsWindows()
                ? "This account can't change the two-factor file. Run this from an Administrator command prompt."
                : "This account can't change twofactor.json; run the command as the account that owns the data folder.");
            return 2;
        }
    }

    private static int ResetTwoFactorIn(string dataDir, bool allDamaged, TextWriter output)
    {
        var file = Path.Combine(dataDir, "security", "twofactor.json");
        // A file this account may not open is not damaged: saying so would send the owner to --all-damaged for nothing.
        if (!CanOpen(file))
        {
            output.WriteLine(OperatingSystem.IsWindows()
                ? "This account can't read twofactor.json. Run this from an Administrator command prompt."
                : "This account can't read twofactor.json; run the command as the account that owns the data folder.");
            return 2;
        }
        var store = new TwoFactorStore(file, new EphemeralDataProtectionProvider(), TimeProvider.System);

        if (allDamaged)
        {
            var existed = File.Exists(file);
            TwoFactorStore.MoveAsideIfDamaged(file);
            if (existed && !File.Exists(file))
            {
                var aside = Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "twofactor.json.damaged-*").Order().LastOrDefault();
                output.WriteLine($"The damaged two-factor file was set aside as {Path.GetFileName(aside)}. Everyone with two-factor sign-in sets it up again.");
                return 0;
            }
            if (existed && store.StateOf(Guid.Empty) == TwoFactorState.Unknown)
            {
                output.WriteLine("The two-factor file could not be read just now, but it does not look damaged. Try again.");
                return 2;
            }
            output.WriteLine(existed
                ? "The two-factor file is not damaged. Nothing was changed."
                : "There is no two-factor file, so it is not damaged. Nothing was changed.");
            return 0;
        }

        var settings = new SettingsStore(Path.Combine(dataDir, "settings.json")).TryLoad();
        if (settings is null)
        {
            output.WriteLine($"No settings found in {dataDir}. Is the DreamGrid service installed?");
            return 2;
        }
        if (settings.Setup.Owner is not { } owner)
        {
            output.WriteLine("There is no owner yet, so there is nobody to reset.");
            return 2;
        }

        const string Damaged = "The two-factor file is damaged. Run reset-two-factor --all-damaged to set it aside; everyone then sets two-factor up again.";
        if (store.StateOf(owner.PrincipalId) == TwoFactorState.Unknown)
        {
            output.WriteLine(Damaged);
            return 2;
        }
        try
        {
            store.Disable(owner.PrincipalId);
        }
        catch (InvalidOperationException)
        {
            output.WriteLine(Damaged);
            return 2;
        }
        // A write by the running launcher at the same moment could have put the old file back.
        if (store.StateOf(owner.PrincipalId) != TwoFactorState.Off)
        {
            output.WriteLine("Two-factor sign-in could not be turned off; try again.");
            return 2;
        }
        output.WriteLine($"Two-factor sign-in is off for {owner.FirstName} {owner.LastName}.");
        return 0;
    }

    /// <summary>False only when <paramref name="file"/> is there but this account may not open it; a missing or busy file is the store's to answer.</summary>
    private static bool CanOpen(string file)
    {
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; }
    }

    /// <summary>
    /// The name of the account that owns <paramref name="dataDir"/> (the folder a symlink points to) when this process runs as
    /// another one (on Unix: its effective user, root under sudo, say, is not the folder's owner); null when it is the same
    /// account, or on Windows. When the owner can't be found out, root gets <see cref="DataOwnerUnknownException"/> (root never
    /// owns the data folder, so it must not write there on a guess) and any other account null.
    /// </summary>
    public static string? ForeignDataOwner(string dataDir) =>
        OperatingSystem.IsWindows() ? null : ForeignDataOwner(dataDir, FolderOwner, EffectiveUid());

    /// <summary>
    /// <see cref="ForeignDataOwner(string)"/> with the folder's owner (<paramref name="folderOwner"/>: uid and name, or null when
    /// it can't be read) and this process's effective uid given (null when unknown: then the account names are compared).
    /// </summary>
    public static string? ForeignDataOwner(string dataDir, Func<string, (uint Uid, string Name)?> folderOwner, uint? effectiveUid)
    {
        if (folderOwner(dataDir) is not { } owner) return effectiveUid == 0 ? throw new DataOwnerUnknownException(dataDir) : null;
        if (effectiveUid is { } me ? owner.Uid == me : owner.Name == Environment.UserName) return null;
        return owner.Name is "" or "UNKNOWN" ? owner.Uid.ToString(CultureInfo.InvariantCulture) : owner.Name; // an account with no name: its number
    }

    /// <summary>
    /// The uid and name of the account that owns <paramref name="dir"/>, following a symlink (<c>stat -L</c>), or null when that
    /// can't be read within 5 seconds.
    /// </summary>
    private static (uint Uid, string Name)? FolderOwner(string dir)
    {
        try
        {
            using var stat = Process.Start(new ProcessStartInfo("stat")
            {
                ArgumentList = { "-L", "-c", "%u %U", "--", dir },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (stat is null) return null;
            // Both pipes are read at once, so a full one can't stall stat, and nothing waits past the timeout.
            var stdout = stat.StandardOutput.ReadToEndAsync();
            var stderr = stat.StandardError.ReadToEndAsync();
            if (!stat.WaitForExit(5000) || !Task.WhenAll(stdout, stderr).Wait(1000))
            {
                try { stat.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            if (stat.ExitCode != 0) return null;
            var parts = stdout.Result.Trim().Split(' ', 2);
            if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)) return null;
            return (uid, parts[1]);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>This process's effective uid; without libc's geteuid, 0 for root and otherwise null.</summary>
    private static uint? EffectiveUid()
    {
        try { return GetEffectiveUid(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Environment.IsPrivilegedProcess ? 0 : null;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    /// <summary>This process's own command line with <paramref name="args"/>: the dotnet host and the launcher's DLL, or the launcher's own program.</summary>
    private static string SameCommand(string[] args)
    {
        var host = Environment.ProcessPath ?? "dotnet";
        string[] program = Path.GetFileNameWithoutExtension(host) == "dotnet" ? [host, typeof(CommandLine).Assembly.Location] : [host];
        return string.Join(' ', program.Concat(args).Select(ShellQuoted));
    }

    private static string ShellQuoted(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':' or '=' or '+' or ',')
            ? word
            : "'" + word.Replace("'", "'\\''") + "'";

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

/// <summary>
/// The account that owns the data folder couldn't be found out while this process runs as root, which is never the account
/// that owns it: a verb that writes in the folder refuses rather than leave root's files behind.
/// </summary>
public sealed class DataOwnerUnknownException(string dataDir)
    : Exception($"The owner of {dataDir} couldn't be read.")
{
    public string DataDir { get; } = dataDir;
}
