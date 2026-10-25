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
    public void Save_is_atomic_leaves_no_tmp_file()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with { GridName = "X" });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Unix_file_mode_is_owner_only()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
