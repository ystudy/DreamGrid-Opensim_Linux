using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Input validation that must happen before the controller touches settings or a console.</summary>
public sealed class GridControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-grid-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly SettingsStore _store;
    private readonly GridController _grid;

    public GridControllerTests()
    {
        _paths = new LauncherPaths(_root, Path.Combine(_root, "program"), null);
        _store = new SettingsStore(_paths.SettingsFile);
        _grid = new GridController(_paths, _store, new MariaDbBinaries("mariadbd", "install-db", null),
            [new LogLevelModule()], new NoRobust(), new FakeGridJoin(), new FakeOtherGrid(), new NoHttp(), TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("A")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("name.ini")]
    [InlineData("Thirty-three characters long name")]
    [InlineData(" Welcome")]
    [InlineData("Welcome ")]
    public async Task CreateRegion_rejects_bad_names_before_touching_settings(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.CreateRegionAsync(name, "island", CancellationToken.None));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    [Theory]
    [InlineData("Welcome")]
    [InlineData("Dréam Isle")]
    [InlineData("O'Neil_Bay-2")]
    public void Region_name_rule_accepts_letters_digits_spaces_dash_underscore_apostrophe(string name)
    {
        Assert.True(GridController.IsValidRegionName(name));
    }

    [Fact]
    public void Instance_folder_name_is_filesystem_safe()
    {
        Assert.Equal("Welcome", GridController.SafeFolderName("Welcome"));
        Assert.Equal("O_Neil_Bay-2", GridController.SafeFolderName("O'Neil Bay-2"));
        Assert.Equal("Dr_am", GridController.SafeFolderName("Dréam"));
    }

    [Theory]
    [InlineData("pass word")]
    [InlineData("pass\"word")]
    [InlineData("--pass")]
    [InlineData("")]
    public async Task CreateOwner_rejects_passwords_the_console_would_split(string password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _grid.CreateOwnerAsync("Test", "Owner", password, null, CancellationToken.None));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    [Theory]
    [InlineData("Te st", "Owner")]
    [InlineData("Test", "")]
    public async Task CreateOwner_rejects_names_with_spaces_or_empty(string first, string last)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _grid.CreateOwnerAsync(first, last, "pass-word1", null, CancellationToken.None));
    }

    [Fact]
    public void Region_alert_sets_and_clears_logins_enabled()
    {
        var id = Guid.NewGuid();
        _store.Save(_store.LoadOrCreate() with
        {
            Programs = [new ProgramSettings("Welcome", 8004, 8011)],
            Regions = [new RegionSettings("Welcome", "Welcome", id, UdpPort: 8004)],
        });
        _grid.OnRegionAlert(new RegionAlert("region_ready", "enabled", "Welcome", id, null));
        Assert.True(_grid.LoginsEnabled);
        _grid.OnRegionAlert(new RegionAlert("regionpid", "regionpid", "Welcome", id, 4242));
        Assert.True(_grid.LoginsEnabled);
        Assert.Equal(4242, _grid.RegionAlertPid);
        _grid.OnRegionAlert(new RegionAlert("region_ready", "disabled", "Welcome", id, null));
        Assert.False(_grid.LoginsEnabled);
    }

    private sealed class NoRobust : IRobustClient
    {
        public Task<bool> IsReadyAsync(CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<RobustAccount?> FindAccountAsync(string first, string last, CancellationToken ct) => GetAccountAsync(first, last, ct);

        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MapRegion>>([]);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct) => GetMapRegionsAsync(ct);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("not expected");
    }
}
