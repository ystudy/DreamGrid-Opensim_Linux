using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>The People list's search against the real tables Robust's migrations make. One grid, every behaviour in order.</summary>
[Collection(GridCollection.Name)]
public sealed class PeopleDirectoryIntegrationTests : IDisposable
{
    private readonly string _root = NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };
    private LauncherSettings _s = null!;

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        DeleteDataDir(_root);
    }

    [Fact]
    public async Task People_are_searched_paged_and_filtered_on_the_real_tables()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        _s = SeedSettings(store);
        var robust = NewRobustClient(_http, _s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var people = new PeopleDirectory(store.LoadOrCreate);
        await grid.StartAsync(ct);
        await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
        for (var i = 0; i < 30; i++) await grid.CreateResidentAsync("Res" + i.ToString("00"), "Zed", null, 0, ct);
        var annB = await grid.CreateResidentAsync("Ann_B", "Test", null, 0, ct);
        var annie = await grid.CreateResidentAsync("Annie", "Rose", null, -1, ct);
        var sam = await grid.CreateResidentAsync("Sam", "Staff", null, 100, ct);
        var nobody = Array.Empty<Guid>();

        // Part of a name finds both Ann accounts; the underscore is literal.
        var ann = await people.SearchAsync("ann", PeopleFilter.Everyone, nobody, 100, 1, ct);
        Assert.Equal(["Ann_B Test", "Annie Rose"], ann.Rows.Select(r => $"{r.Account.FirstName} {r.Account.LastName}").Order());
        var underscore = await people.SearchAsync("n_B", PeopleFilter.Everyone, nobody, 100, 1, ct);
        Assert.Equal(annB.PrincipalId, Assert.Single(underscore.Rows).Account.PrincipalId);
        Assert.Empty((await people.SearchAsync("n%B", PeopleFilter.Everyone, nobody, 100, 1, ct)).Rows);

        // Pages of 25, the total counts every match, sorted by last then first name.
        var zed1 = await people.SearchAsync("Zed", PeopleFilter.Everyone, nobody, 100, 1, ct);
        var zed2 = await people.SearchAsync("Zed", PeopleFilter.Everyone, nobody, 100, 2, ct);
        Assert.Equal(30, zed1.Total);
        Assert.Equal(25, zed1.Rows.Count);
        Assert.Equal(5, zed2.Rows.Count);
        Assert.Equal("Res00", zed1.Rows[0].Account.FirstName);
        Assert.Equal("Res29", zed2.Rows[^1].Account.FirstName);

        // Staff: at or above the staff level.
        var staff = await people.SearchAsync(null, PeopleFilter.Staff, nobody, 100, 1, ct);
        Assert.Contains(staff.Rows, r => r.Account.PrincipalId == sam.PrincipalId);
        Assert.DoesNotContain(staff.Rows, r => r.Account.UserLevel < 100);

        // Suspended leaves out a waiting sign-up, which Waiting shows; Waiting with nobody waiting is empty.
        Assert.Contains((await people.SearchAsync(null, PeopleFilter.Suspended, nobody, 100, 1, ct)).Rows, r => r.Account.PrincipalId == annie.PrincipalId);
        Assert.Empty((await people.SearchAsync(null, PeopleFilter.Suspended, [annie.PrincipalId], 100, 1, ct)).Rows);
        Assert.Equal(annie.PrincipalId, Assert.Single((await people.SearchAsync(null, PeopleFilter.Waiting, [annie.PrincipalId], 100, 1, ct)).Rows).Account.PrincipalId);
        Assert.Equal(0, (await people.SearchAsync(null, PeopleFilter.Waiting, nobody, 100, 1, ct)).Total);

        // Last login and logout from GridUser, the online region from Presence (rows as OpenSim writes them).
        var region = Guid.NewGuid();
        var login = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        await Exec("INSERT INTO GridUser (UserID, HomeRegionID, HomePosition, HomeLookAt, LastRegionID, LastPosition, LastLookAt, Online, Login, Logout) " +
                   "VALUES (@u, @z, '<0,0,0>', '<0,0,0>', @r, '<0,0,0>', '<0,0,0>', 'True', @in, '0')", ct,
            ("@u", annB.PrincipalId.ToString()), ("@z", Guid.Empty.ToString()), ("@r", region.ToString()), ("@in", login.ToString()));
        await Exec("INSERT INTO Presence (UserID, RegionID, SessionID, SecureSessionID) VALUES (@u, @r, @s, @ss)", ct,
            ("@u", annB.PrincipalId.ToString()), ("@r", region.ToString()), ("@s", Guid.NewGuid().ToString()), ("@ss", Guid.NewGuid().ToString()));
        var row = await people.GetAsync(annB.PrincipalId, ct);
        Assert.NotNull(row);
        Assert.Equal(login, row.LastLogin!.Value.ToUnixTimeSeconds());
        Assert.Null(row.LastLogout);
        Assert.Equal(region, row.OnlineRegion);
        var never = await people.GetAsync(sam.PrincipalId, ct);
        Assert.Null(never!.LastLogin);
        Assert.Null(never.OnlineRegion);
        Assert.Null(await people.GetAsync(Guid.NewGuid(), ct));
    }

    private MySqlConnection Open() => new(new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)_s.MariaDbPort, Database = "robust",
        UserID = "robustuser", Password = _s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
    }.ConnectionString);

    private async Task Exec(string sql, CancellationToken ct, params (string Name, object Value)[] p)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
