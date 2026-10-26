using System.Diagnostics;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Who's online against the real Robust tables (created by Robust's own migrations).</summary>
[Collection(GridCollection.Name)]
public sealed class OnlineUsersTests : IDisposable
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
    public async Task OnlineUsers_lists_presence_rows()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);
        var robust = NewRobustClient(_http, s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var online = new OnlineUsers(store.LoadOrCreate);
        try
        {
            await grid.StartAsync(ct); // MariaDB, then Robust, which creates its tables
            var owner = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            var nobody = await online.ListAsync(ct);
            Assert.True(nobody.Available);
            Assert.Empty(nobody.Avatars);

            var region = Guid.NewGuid();
            await using (var conn = new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)s.MariaDbPort, Database = "robust",
                UserID = "robustuser", Password = s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
            }.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new MySqlCommand(
                    "INSERT INTO Presence (UserID, RegionID, SessionID, SecureSessionID) VALUES (@u, @r, @s, @s2)", conn);
                cmd.Parameters.AddWithValue("@u", owner.PrincipalId.ToString());
                cmd.Parameters.AddWithValue("@r", region.ToString());
                cmd.Parameters.AddWithValue("@s", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@s2", Guid.NewGuid().ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var list = await online.ListAsync(ct);
            Assert.True(list.Available);
            Assert.Equal([new OnlineAvatar("Test Owner", region)], list.Avatars);

            // A database that is down: no exception, no long wait.
            await grid.StopAllAsync(ct);
            var watch = Stopwatch.StartNew();
            var down = await online.ListAsync(ct);
            Assert.False(down.Available);
            Assert.Empty(down.Avatars);
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
