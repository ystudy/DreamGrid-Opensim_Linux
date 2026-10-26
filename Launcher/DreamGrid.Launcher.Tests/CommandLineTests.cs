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
        Assert.DoesNotContain('+', output);
        Assert.NotEmpty(output);
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
}
