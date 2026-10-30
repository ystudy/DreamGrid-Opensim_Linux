using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>AddRegionAsync refuses a bad request before it changes the settings or starts anything.</summary>
public sealed class AddRegionValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-add-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly SettingsStore _store;
    private readonly GridController _grid;

    public AddRegionValidationTests()
    {
        _paths = new LauncherPaths(_root, Path.Combine(_root, "program"), null);
        _store = new SettingsStore(_paths.SettingsFile);
        _store.Save(_store.LoadOrCreate() with
        {
            Programs = [new ProgramSettings("Home", 8004, 8011)],
            Regions = [new RegionSettings("Home", "Home", Guid.NewGuid(), 1000, 1000, UdpPort: 8004)],
        });
        _grid = new GridController(_paths, _store, new MariaDbBinaries("mariadbd", "install-db", null),
            [new LogLevelModule()], new NoRobust(), new FakeGridJoin(), new NoHttp(), TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private async Task Refused(NewRegion request, string message)
    {
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => _grid.AddRegionAsync(request, TestContext.Current.CancellationToken));

        Assert.StartsWith(message, e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public Task Duplicate_name_ignoring_case_is_refused() =>
        Refused(new NewRegion("HOME", 1005, 1005, 1, "island", null), "There is already a region called HOME.");

    [Fact]
    public Task Overlapping_spot_is_refused() =>
        Refused(new NewRegion("Beach", 999, 999, 2, "island", null), "That spot is taken by Home.");

    [Fact]
    public Task Size_17_is_refused() =>
        Refused(new NewRegion("Beach", 1005, 1005, 17, "island", null), "A region is 1 to 16 squares on each side.");

    [Fact]
    public Task Unknown_share_target_is_refused() =>
        Refused(new NewRegion("Beach", 1001, 1000, 1, "island", "Nowhere"), "That group of regions no longer exists.");

    [Fact]
    public Task Invalid_name_is_refused() =>
        Refused(new NewRegion("../evil", 1005, 1005, 1, "island", null), "A region name is 2-32 letters");

    [Fact]
    public Task Unknown_content_is_refused() =>
        Refused(new NewRegion("Beach", 1005, 1005, 1, "lava", null), "Content must be starter, a built-in type (island, beach, forest, meadow) or one of your region types.");

    [Fact]
    public async Task Add_accepts_built_in_type_content()
    {
        // The rig cannot start anything, so the request gets past the content check and fails later, not as a refusal.
        var e = await Record.ExceptionAsync(() =>
            _grid.AddRegionAsync(new NewRegion("Beach", 1005, 1005, 1, "beach", null), TestContext.Current.CancellationToken));

        Assert.NotNull(e);
        Assert.IsNotType<ArgumentException>(e);
    }

    private sealed class NoRobust : IRobustClient
    {
        public Task<bool> IsReadyAsync(CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<bool> AuthenticateAsync(Guid principalId, string password, CancellationToken ct) => throw new InvalidOperationException("not expected");
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MapRegion>>([]);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("not expected");
    }
}
