using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.IntegrationTests.GridTestSupport;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Renaming an avatar in Robust's real UserAccounts table, as the People page's edit does.</summary>
[Collection(GridCollection.Name)]
public sealed class RobustNamesIntegrationTests : IDisposable
{
    private readonly string _root = NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        DeleteDataDir(_root);
    }

    [Fact]
    public async Task Rename_writes_UserAccounts_and_refuses_a_taken_name_in_any_case()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);
        var robust = NewRobustClient(_http, s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var names = new RobustNames(store.LoadOrCreate);
        await grid.StartAsync(ct);
        // The owner first, as on a real grid: its create waits for Robust to answer, and the strict name check needs that.
        await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
        var ann = await grid.CreateResidentAsync("Ann", "Baker", null, 0, ct);
        await grid.CreateResidentAsync("Bob", "Jones", null, 0, ct);

        await names.SetAsync(ann.PrincipalId, "Annie", "Baker", ct);
        Assert.Equal(ann.PrincipalId, (await robust.FindAccountAsync("Annie", "Baker", ct))?.PrincipalId);
        Assert.Null(await robust.FindAccountAsync("Ann", "Baker", ct));

        await Assert.ThrowsAsync<NameTakenException>(() => names.SetAsync(ann.PrincipalId, "bob", "JONES", ct));
        Assert.Equal(ann.PrincipalId, (await robust.FindAccountAsync("Annie", "Baker", ct))?.PrincipalId);
        await Assert.ThrowsAsync<RobustNamesUnavailableException>(() => names.SetAsync(Guid.NewGuid(), "No", "One", ct));
    }
}
