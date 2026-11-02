using System.Diagnostics;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>The whole grid's regions against the real Robust tables (created by Robust's own migrations).</summary>
[Collection(GridCollection.Name)]
public sealed class GridRegionsTests : IDisposable
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
    public async Task GridRegions_lists_registered_regions()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);
        var robust = NewRobustClient(_http, s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var regions = new GridRegions(store.LoadOrCreate);
        try
        {
            await grid.StartAsync(ct); // MariaDB, then Robust, which creates its tables
            var before = await regions.ListAsync(ct);
            Assert.True(before.Available);

            // A region another machine registered: only Robust's table knows it.
            var elsewhere = Guid.NewGuid();
            await using (var conn = new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)s.MariaDbPort, Database = "robust",
                UserID = "robustuser", Password = s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
            }.ConnectionString))
            {
                await conn.OpenAsync(ct);
                // uuid, regionHandle and Token are the columns without a default.
                await using var cmd = new MySqlCommand("INSERT INTO regions (uuid, regionHandle, regionName, Token) VALUES (@u, 1, 'Far Away', '')", conn);
                cmd.Parameters.AddWithValue("@u", elsewhere.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var list = await regions.ListAsync(ct);
            Assert.True(list.Available);
            Assert.Contains(new GridRegion(elsewhere, "Far Away"), list.Regions);
            Assert.Equal(before.Regions.Count + 1, list.Regions.Count);

            // A database that is down: no exception, no long wait.
            await grid.StopAllAsync(ct);
            var watch = Stopwatch.StartNew();
            var down = await regions.ListAsync(ct);
            Assert.False(down.Available);
            Assert.Empty(down.Regions);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
        }
    }
}
