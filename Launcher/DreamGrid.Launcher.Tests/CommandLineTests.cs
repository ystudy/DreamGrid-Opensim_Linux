using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class CommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-cli-" + Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_root, "data");
    private string SettingsFile => Path.Combine(DataDir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static async Task<(int? Code, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var code = await CommandLine.RunVerbAsync(args, new Dictionary<string, string?>(), output, CancellationToken.None);
        return (code, output.ToString().Trim());
    }

    private SettingsStore NewStore(Func<LauncherSettings, LauncherSettings>? change = null)
    {
        var store = new SettingsStore(SettingsFile);
        var s = store.LoadOrCreate();
        if (change is not null) store.Save(change(s));
        return store;
    }

    [Fact]
    public async Task Version_prints_informational_version()
    {
        var (code, output) = await RunAsync("--version");

        Assert.Equal(0, code);
        Assert.Equal(CommandLine.Version(), output);
        // The version is the entry assembly's: the launcher when it runs, the test host here.
        Assert.Equal(LauncherVersion.Current, output);
        Assert.Equal(Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, output);
    }

    [Fact]
    public async Task Admin_link_prints_setup_url_before_owner()
    {
        var s = NewStore(x => x with { Hostname = "grid.example.com", WebPort = 8123 }).LoadOrCreate();

        var (code, output) = await RunAsync("admin-link", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Equal($"http://grid.example.com:8123/setup?token={s.SetupToken}", output);
    }

    [Fact]
    public async Task Admin_link_prints_dashboard_url_after_owner()
    {
        NewStore(x => x with { Hostname = "grid.example.com", Setup = x.Setup with { OwnerCreated = true } });

        var (code, output) = await RunAsync("admin-link", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Equal("http://grid.example.com:8001/", output);
    }

    [Fact]
    public async Task Admin_link_does_not_create_settings()
    {
        Directory.CreateDirectory(DataDir);

        var (code, _) = await RunAsync("admin-link", "--data", DataDir, "--wait", "0");

        Assert.Equal(4, code);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task No_verb_returns_null()
    {
        Assert.Null((await RunAsync()).Code);
        Assert.Null((await RunAsync("--data", DataDir, "--web-port", "8101")).Code);
    }

    [Fact]
    public async Task Stop_grid_refuses_while_lock_is_held()
    {
        using var held = InstanceLock.TryAcquire(DataDir);
        Assert.NotNull(held);

        var (code, output) = await RunAsync("stop-grid", "--data", DataDir, "--opensim", Path.Combine(_root, "program"));

        Assert.Equal(3, code);
        Assert.Contains("already running", output);
    }

    [Fact]
    public async Task Stop_grid_with_nothing_running_succeeds_and_leaves_settings()
    {
        NewStore();
        var before = File.ReadAllText(SettingsFile);

        var (code, _) = await RunAsync("stop-grid", "--data", DataDir, "--opensim", Path.Combine(_root, "program"));

        Assert.Equal(0, code);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Web_port_flag_is_saved_and_validated()
    {
        var store = NewStore();

        Assert.Null(CommandLine.ApplyWebPort(store, 8101));
        Assert.Equal(8101, store.LoadOrCreate().WebPort);

        var before = File.ReadAllText(SettingsFile);
        var error = CommandLine.ApplyWebPort(store, 8002); // the Robust public port
        Assert.NotNull(error);
        Assert.Contains("8002", error);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
        Assert.Equal(8101, store.LoadOrCreate().WebPort);
    }

    [Fact]
    public void Web_port_argument_is_parsed()
    {
        Assert.Null(CommandLine.ParseWebPort(["--data", "x"]));
        Assert.Equal(8101, CommandLine.ParseWebPort(["--web-port", "8101"]));
        Assert.Throws<ArgumentException>(() => CommandLine.ParseWebPort(["--web-port", "abc"]));
        Assert.Throws<ArgumentException>(() => CommandLine.ParseWebPort(["--web-port"]));
    }
    [Fact]
    public async Task Set_web_port_refuses_while_lock_is_held()
    {
        NewStore();
        var before = File.ReadAllText(SettingsFile);
        using var held = InstanceLock.TryAcquire(DataDir);
        Assert.NotNull(held);

        var (code, output) = await RunAsync("set-web-port", "8101", "--data", DataDir);

        Assert.Equal(3, code);
        Assert.Contains("Stop the service first", output);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task Set_web_port_rejects_a_port_the_launcher_rejects_and_keeps_the_settings()
    {
        NewStore();
        var before = File.ReadAllText(SettingsFile);

        var (code, output) = await RunAsync("set-web-port", "8002", "--data", DataDir); // the Robust public port

        Assert.Equal(2, code);
        Assert.Contains("8002", output);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
    }

    [Theory]
    [InlineData("set-web-port")]
    [InlineData("set-web-port", "abc")]
    [InlineData("set-web-port", "--data")]
    public async Task Set_web_port_needs_a_number(params string[] args)
    {
        var (code, output) = await RunAsync([.. args, "--data", DataDir]);

        Assert.Equal(2, code);
        Assert.Contains("set-web-port", output);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Set_web_port_saves_the_port()
    {
        var token = NewStore().LoadOrCreate().SetupToken;

        var (code, _) = await RunAsync("set-web-port", "8101", "--data", DataDir);

        Assert.Equal(0, code);
        var s = new SettingsStore(SettingsFile).LoadOrCreate();
        Assert.Equal(8101, s.WebPort);
        Assert.Equal(token, s.SetupToken);
    }

    [Fact]
    public async Task Set_web_port_creates_the_settings_when_there_are_none()
    {
        var (code, _) = await RunAsync("set-web-port", "8101", "--data", DataDir);

        Assert.Equal(0, code);
        var s = new SettingsStore(SettingsFile).TryLoad();
        Assert.NotNull(s);
        Assert.Equal(8101, s.WebPort);
        Assert.NotNull(s.SetupToken);
    }

    [Fact]
    public async Task Set_web_port_refused_creates_no_settings()
    {
        var (code, _) = await RunAsync("set-web-port", "80", "--data", DataDir);

        Assert.Equal(2, code);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Live_child_pids_are_read_from_the_pid_files()
    {
        var pids = Path.Combine(DataDir, "state", "pids");
        Directory.CreateDirectory(pids);
        File.WriteAllText(Path.Combine(pids, "robust.pid"), Environment.ProcessId.ToString());
        File.WriteAllText(Path.Combine(pids, "gone.pid"), int.MaxValue.ToString());
        File.WriteAllText(Path.Combine(pids, "junk.pid"), "x");

        Assert.Equal([Environment.ProcessId], CommandLine.LiveChildPids(pids));
        Assert.Empty(CommandLine.LiveChildPids(Path.Combine(_root, "no-such-dir")));
    }

    [Fact]
    public async Task Wait_until_gone_waits_for_a_running_child_to_exit()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = typeof(CommandLineTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "DreamGrid.FakeChild.exe" : "DreamGrid.FakeChild");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var child = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--serve", port.ToString() } })!;
        try
        {
            Assert.False(await CommandLine.WaitUntilGoneAsync([child.Id], TimeSpan.FromMilliseconds(500), ct));

            var waiting = CommandLine.WaitUntilGoneAsync([child.Id], TimeSpan.FromSeconds(30), ct);
            child.Kill();
            Assert.True(await waiting);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public async Task Wait_until_gone_with_no_children_returns_at_once() =>
        Assert.True(await CommandLine.WaitUntilGoneAsync([], TimeSpan.Zero, TestContext.Current.CancellationToken));

    private static readonly Guid Owner = Guid.Parse("0e000000-0000-0000-0000-00000000000e");
    private static readonly Guid Staff = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private string TwoFactorFile => Path.Combine(DataDir, "security", "twofactor.json");

    private SettingsStore NewStoreWithOwner() => NewStore(s => s with
    {
        Setup = s.Setup with { OwnerCreated = true, Owner = new OwnerInfo("Olive", "Owner", Owner) },
    });

    /// <summary>Puts <paramref name="who"/> in the two-factor file, as the running launcher would (its own key ring).</summary>
    private void Enrol(Guid who, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TwoFactorFile)!);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new DreamGrid.Launcher.Security.TwoFactorStore(TwoFactorFile,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), time);
        var (secret, _) = store.BeginEnrol(who, name, "DreamGrid");
        Assert.NotNull(store.ConfirmEnrol(who, DreamGrid.Launcher.Core.Security.Totp.Code(secret,
            DreamGrid.Launcher.Core.Security.Totp.StepAt(time.GetUtcNow())), secret));
    }

    private bool IsEnrolled(Guid who)
    {
        // A new store, as the next sign-in would read the file (no decryption is needed to see who is on).
        var store = new DreamGrid.Launcher.Security.TwoFactorStore(TwoFactorFile,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System);
        return store.Enrolled().Any(e => e.Principal == who);
    }

    [Fact]
    public async Task Reset_two_factor_turns_off_owner()
    {
        NewStoreWithOwner();
        Enrol(Owner, "Olive Owner");
        Enrol(Staff, "Test Admin");

        var (code, output) = await RunAsync("reset-two-factor", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Equal("Two-factor sign-in is off for Olive Owner.", output);
        Assert.False(IsEnrolled(Owner));
        Assert.True(IsEnrolled(Staff)); // only the owner's
    }

    [Fact]
    public async Task Reset_two_factor_when_owner_has_none_says_so()
    {
        NewStoreWithOwner();

        var (code, output) = await RunAsync("reset-two-factor", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Equal("Two-factor sign-in is off for Olive Owner.", output);
        Assert.False(File.Exists(TwoFactorFile));
    }

    [Fact]
    public async Task Reset_two_factor_moves_damaged_file_aside()
    {
        NewStoreWithOwner();
        Directory.CreateDirectory(Path.GetDirectoryName(TwoFactorFile)!);
        File.WriteAllText(TwoFactorFile, "{ this is not json");

        // Without the flag nothing is touched, and the reason names the flag.
        var (refused, message) = await RunAsync("reset-two-factor", "--data", DataDir);
        Assert.Equal(2, refused);
        Assert.Contains("reset-two-factor --all-damaged", message);
        Assert.Equal("{ this is not json", File.ReadAllText(TwoFactorFile));

        var (code, output) = await RunAsync("reset-two-factor", "--all-damaged", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Contains("set aside", output);
        Assert.False(File.Exists(TwoFactorFile));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(TwoFactorFile)!, "twofactor.json.damaged-*"));

        // Run again: nothing to move.
        var (again, text) = await RunAsync("reset-two-factor", "--all-damaged", "--data", DataDir);
        Assert.Equal(0, again);
        Assert.Contains("not damaged", text);
    }

    [Fact]
    public async Task Reset_two_factor_all_damaged_leaves_a_good_file()
    {
        NewStoreWithOwner();
        Enrol(Staff, "Test Admin");
        var before = File.ReadAllText(TwoFactorFile);

        var (code, output) = await RunAsync("reset-two-factor", "--all-damaged", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Contains("not damaged", output);
        Assert.Equal(before, File.ReadAllText(TwoFactorFile));
    }

    [Fact]
    public async Task Reset_two_factor_without_owner_exits_2()
    {
        NewStore(); // settings exist, no owner yet

        var (code, output) = await RunAsync("reset-two-factor", "--data", DataDir);

        Assert.Equal(2, code);
        Assert.Contains("no owner", output);
        Assert.False(File.Exists(TwoFactorFile));
    }

    [Fact]
    public async Task Reset_two_factor_without_data_folder_exits_2()
    {
        var (code, output) = await RunAsync("reset-two-factor", "--data", Path.Combine(_root, "nowhere"));

        Assert.Equal(2, code);
        Assert.Contains("nowhere", output);
        Assert.False(Directory.Exists(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public async Task Security_levels_that_are_not_allowed_are_refused_like_corrupt_settings()
    {
        NewStore(s => s with { Security = s.Security with { SupervisorLevel = 0 } });

        var (code, output) = await RunAsync("admin-link", "--data", DataDir);

        Assert.Equal(2, code);
        Assert.Contains("security.supervisorLevel: Use a supervisor level of 1 or more.", output);
    }

    private static async Task<(int? Code, string Output)> RunAsync(Func<string, string?> foreignOwner, params string[] args)
    {
        var output = new StringWriter();
        var code = await CommandLine.RunVerbAsync(args, new Dictionary<string, string?>(), output, CancellationToken.None, foreignOwner);
        return (code, output.ToString().Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_two_factor_refuses_to_run_as_another_account_than_the_data_folders_owner(bool allDamaged)
    {
        NewStoreWithOwner();
        Enrol(Owner, "Olive Owner");
        var before = File.ReadAllBytes(TwoFactorFile);
        string? asked = null;
        string[] args = allDamaged ? ["reset-two-factor", "--all-damaged", "--data", DataDir] : ["reset-two-factor", "--data", DataDir];

        var (code, output) = await RunAsync(dir => { asked = dir; return "dreamgrid"; }, args);

        Assert.Equal(2, code);
        Assert.Equal(DataDir, asked);
        Assert.Contains("sudo runuser -u dreamgrid -- ", output);
        Assert.Contains(allDamaged ? "reset-two-factor --all-damaged" : "reset-two-factor", output);
        Assert.Equal(before, File.ReadAllBytes(TwoFactorFile)); // nothing written, so nothing changes hands
        Assert.True(IsEnrolled(Owner));
    }

    [Fact]
    public async Task Reset_two_factor_runs_as_the_data_folders_owner()
    {
        NewStoreWithOwner();
        Enrol(Owner, "Olive Owner");

        var (code, output) = await RunAsync(_ => null, "reset-two-factor", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.Equal("Two-factor sign-in is off for Olive Owner.", output);
    }

    [Fact]
    public async Task Other_verbs_do_not_ask_who_owns_the_data_folder()
    {
        NewStore();
        var asked = false;

        var (code, _) = await RunAsync(_ => { asked = true; return "dreamgrid"; }, "admin-link", "--data", DataDir);

        Assert.Equal(0, code);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_two_factor_says_when_this_account_cannot_read_the_file(bool allDamaged)
    {
        NewStoreWithOwner();
        Enrol(Owner, "Olive Owner");
        string[] args = allDamaged ? ["reset-two-factor", "--all-damaged", "--data", DataDir] : ["reset-two-factor", "--data", DataDir];

        using (NoAccess.Read(TwoFactorFile))
        {
            var (code, output) = await RunAsync(_ => null, args);

            Assert.Equal(2, code);
            Assert.Contains("This account can't read twofactor.json", output);
            Assert.Contains(OperatingSystem.IsWindows() ? "Run this from an Administrator command prompt."
                : "run the command as the account that owns the data folder.", output);
            Assert.DoesNotContain("damaged", output.Replace("--all-damaged", ""));
            Assert.DoesNotContain("Try again", output);
        }
        Assert.True(File.Exists(TwoFactorFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(TwoFactorFile)!, "twofactor.json.damaged-*"));
        Assert.True(IsEnrolled(Owner));
    }

    [Fact]
    public async Task Reset_two_factor_says_how_to_run_it_when_the_file_cannot_be_written()
    {
        NewStoreWithOwner();
        Enrol(Owner, "Olive Owner");

        using (NoAccess.Write(Path.GetDirectoryName(TwoFactorFile)!, TwoFactorFile))
        {
            var (code, output) = await RunAsync(_ => null, "reset-two-factor", "--data", DataDir);

            Assert.Equal(2, code);
            Assert.Contains(OperatingSystem.IsWindows() ? "Run this from an Administrator command prompt."
                : "run the command as the account that owns the data folder.", output);
        }
        Assert.True(IsEnrolled(Owner));
    }

    [Fact]
    public void The_owner_check_finds_no_other_owner_for_this_accounts_own_folder()
    {
        Directory.CreateDirectory(DataDir);
        Assert.Null(CommandLine.ForeignDataOwner(DataDir));
    }

    /// <summary>Takes this account's read (or write) access away from a file or folder until disposed: an ACL deny rule on Windows, the mode on Unix.</summary>
    private sealed class NoAccess : IDisposable
    {
        private readonly Action _undo;
        private NoAccess(Action undo) => _undo = undo;
        public void Dispose() => _undo();

        public static NoAccess Read(string file)
        {
            if (OperatingSystem.IsWindows()) return Deny(file, System.Security.AccessControl.FileSystemRights.ReadData);
            SkipIfRoot();
            var mode = File.GetUnixFileMode(file);
            File.SetUnixFileMode(file, UnixFileMode.None);
            return new NoAccess(() => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, mode); });
        }

        /// <summary>No new files in <paramref name="dir"/> and no writing <paramref name="file"/>.</summary>
        public static NoAccess Write(string dir, string file)
        {
            if (OperatingSystem.IsWindows())
            {
                var a = Deny(dir, System.Security.AccessControl.FileSystemRights.CreateFiles);
                var b = Deny(file, System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.Delete);
                return new NoAccess(() => { b.Dispose(); a.Dispose(); });
            }
            SkipIfRoot();
            var mode = File.GetUnixFileMode(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new NoAccess(() => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, mode); });
        }

        private static void SkipIfRoot()
        {
            if (!OperatingSystem.IsWindows()) Assert.SkipWhen(Environment.UserName == "root", "root reads and writes everything");
        }

        [SupportedOSPlatform("windows")]
        private static NoAccess Deny(string path, System.Security.AccessControl.FileSystemRights rights)
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var rule = new System.Security.AccessControl.FileSystemAccessRule(me, rights, System.Security.AccessControl.AccessControlType.Deny);
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var acl = info.GetAccessControl();
                acl.AddAccessRule(rule);
                info.SetAccessControl(acl);
                return new NoAccess(() => { var undo = info.GetAccessControl(); undo.RemoveAccessRule(rule); info.SetAccessControl(undo); });
            }
            var f = new FileInfo(path);
            var facl = f.GetAccessControl();
            facl.AddAccessRule(rule);
            f.SetAccessControl(facl);
            return new NoAccess(() => { var undo = f.GetAccessControl(); undo.RemoveAccessRule(rule); f.SetAccessControl(undo); });
        }
    }
}
