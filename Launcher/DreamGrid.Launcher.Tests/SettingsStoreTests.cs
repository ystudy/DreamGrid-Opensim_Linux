using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
    }

    [Fact]
    public void Settings_tmp_left_by_a_crash_is_ignored_and_removed()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var good = new SettingsStore(path).LoadOrCreate();
        File.WriteAllText(path + ".tmp", "{\"webPort\": 80");

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(good.Db.RobustPassword, loaded.Db.RobustPassword);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_removes_tmp_when_replace_fails()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var settings = new SettingsStore(Path.Combine(NewDir(), "x.json")).LoadOrCreate();
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => new SettingsStore(path).Save(settings));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_does_not_retry_non_sharing_errors()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var settings = new SettingsStore(Path.Combine(NewDir(), "x.json")).LoadOrCreate();
        Directory.CreateDirectory(path);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => new SettingsStore(path).Save(settings));
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Load_rejects_null_for_required_member()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["db"] = null;
        File.WriteAllText(path, node.ToJsonString());

        Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).LoadOrCreate());
    }

    [Fact]
    public void LoadOrCreate_creates_file_with_unique_random_secrets()
    {
        var a = new SettingsStore(Path.Combine(NewDir(), "settings.json")).LoadOrCreate();
        var b = new SettingsStore(Path.Combine(NewDir(), "settings.json")).LoadOrCreate();

        Assert.NotEqual(a.Db.RobustPassword, b.Db.RobustPassword);
        Assert.Equal(32, a.Db.RobustPassword.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9]{32}$"), a.Db.RobustPassword);
        Assert.NotNull(a.SetupToken);
    }

    [Fact]
    public void LoadOrCreate_returns_existing_values_unchanged()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        var created = store.LoadOrCreate();
        store.Save(created with { GridName = "Avalon" });

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal("Avalon", loaded.GridName);
        Assert.Equal(created.Db, loaded.Db);
        Assert.Equal(created.Console, loaded.Console);
        Assert.Equal(created.MachineId, loaded.MachineId);
        Assert.Equal(created.SetupToken, loaded.SetupToken);
    }

    [Fact]
    public void Corrupt_file_throws_and_is_left_untouched()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        File.WriteAllText(path, "{\"GridName\": ");
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).LoadOrCreate());

        Assert.Contains(path, ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Concurrent_reads_never_make_a_save_fail()
    {
        var store = new SettingsStore(Path.Combine(NewDir(), "settings.json"));
        var s = store.LoadOrCreate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested) { store.LoadOrCreate(); reads++; }
            return reads;
        });
        var saves = 0;
        while (!stop.IsCancellationRequested) { store.Save(s with { GridName = "G" + saves }); saves++; }

        Assert.True(await reader > 0);
        Assert.True(saves > 0);
    }

    [Fact]
    public void Save_is_atomic_leaves_no_tmp_file()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with { GridName = "X" });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Unix_file_mode_is_owner_only()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Save_makes_the_settings_directory_owner_only_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var dir = Path.Combine(NewDir(), "data");
        new SettingsStore(Path.Combine(dir, "settings.json")).LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }
}
