using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
}
