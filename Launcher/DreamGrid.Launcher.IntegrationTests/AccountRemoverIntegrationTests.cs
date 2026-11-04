using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Deleting a resident and everything tied to them, and an empty estate, against the real tables.</summary>
[Collection(GridCollection.Name)]
public sealed class AccountRemoverIntegrationTests : IDisposable
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
    public async Task Delete_everything_leaves_no_row_for_the_avatar_and_removes_only_empty_estates()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        _s = SeedSettings(store);
        var robust = NewRobustClient(_http, _s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        await grid.StartAsync(ct);
        // The owner first, as on a real grid: its create waits for Robust to answer, and the strict name check needs that.
        await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
        var sue = await grid.CreateResidentAsync("Sue", "Gone", null, -1, ct);
        var ann = await grid.CreateResidentAsync("Ann", "Stays", null, 0, ct);
        var id = sue.PrincipalId.ToString();
        var other = ann.PrincipalId.ToString();

        // A friend both ways, a group membership, a profile and pick, an offline message.
        await Exec("robust", "INSERT INTO Friends (PrincipalID, Friend, Flags, Offered) VALUES (@a, @b, '1', '0'), (@b, @a, '1', '0')", ct, ("@a", id), ("@b", other));
        var group = Guid.NewGuid().ToString();
        await Exec("robust", "INSERT INTO os_groups_membership (GroupID, PrincipalID, SelectedRoleID, AccessToken) VALUES (@g, @p, @z, @t)", ct,
            ("@g", group), ("@p", id), ("@z", Guid.Empty.ToString()), ("@t", Guid.NewGuid().ToString()));
        await Exec("robust", "INSERT INTO os_groups_rolemembership (GroupID, RoleID, PrincipalID) VALUES (@g, @z, @p)", ct,
            ("@g", group), ("@z", Guid.Empty.ToString()), ("@p", id));
        await Exec("robust", "INSERT INTO userprofile (useruuid, profilePartner, profileImage, profileURL, profileFirstImage, profileAllowPublish, " +
                             "profileMaturePublish, profileWantToMask, profileWantToText, profileSkillsMask, profileSkillsText, profileLanguages, " +
                             "profileAboutText, profileFirstText) VALUES (@p, @z, @z, '', @z, 0, 0, 0, '', 0, '', '', 'hi', '')", ct,
            ("@p", id), ("@z", Guid.Empty.ToString()));
        await Exec("robust", "INSERT INTO im_offline (PrincipalID, FromID, Message) VALUES (@p, @f, 'hello')", ct, ("@p", id), ("@f", other));

        var tables = await new AccountRemover(store.LoadOrCreate).DeleteEverythingAsync(sue.PrincipalId, ct);

        Assert.Contains("UserAccounts", tables);
        Assert.Contains("Friends", tables);
        foreach (var (table, column) in new[] { ("UserAccounts", "PrincipalID"), ("auth", "UUID"), ("inventoryfolders", "agentID"),
                     ("inventoryitems", "avatarID"), ("Friends", "PrincipalID"), ("Friends", "Friend"), ("os_groups_membership", "PrincipalID"),
                     ("os_groups_rolemembership", "PrincipalID"), ("userprofile", "useruuid"), ("im_offline", "PrincipalID") })
            Assert.Equal(0L, await Count("robust", $"`{table}` WHERE `{column}` = @id", ct, ("@id", id)));
        Assert.Equal(1L, await Count("robust", "UserAccounts WHERE PrincipalID = @id", ct, ("@id", other)));

        // An estate with a region linked stays; an empty one goes with its managers and bans.
        var estates = new EstateRemover(store.LoadOrCreate);
        // The estate tables come from a region's first start; this test starts none, so make them as OpenSim's migrations do.
        await Exec("opensim", EstateTables, ct);
        await Exec("opensim", "INSERT INTO estate_settings (EstateName, AbuseEmailToEstateOwner, DenyAnonymous, ResetHomeOnTeleport, FixedSun, " +
                              "DenyTransacted, BlockDwell, DenyIdentified, AllowVoice, UseGlobalTime, PricePerMeter, TaxFree, AllowDirectTeleport, " +
                              "RedirectGridX, RedirectGridY, ParentEstateID, SunPosition, EstateSkipScripts, BillableFactor, PublicAccess, " +
                              "AbuseEmail, EstateOwner, DenyMinors) VALUES ('Empty Estate', 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 1, 0, 0, 1, 0, 0, 0, 1, '', @o, 0)", ct,
            ("@o", id));
        var empty = Convert.ToInt32(await Scalar("opensim", "SELECT MAX(EstateID) FROM estate_settings", ct));
        await Exec("opensim", "INSERT INTO estate_managers (EstateID, uuid) VALUES (@e, @u)", ct, ("@e", empty), ("@u", other));
        await Exec("opensim", "INSERT INTO estateban (EstateID, bannedUUID, bannedIp, bannedIpHostMask, bannedNameMask) VALUES (@e, @u, '', '', '')", ct,
            ("@e", empty), ("@u", other));
        await Exec("opensim", "INSERT INTO estate_map (RegionID, EstateID) VALUES (@r, @e)", ct, ("@r", Guid.NewGuid().ToString()), ("@e", empty));
        Assert.False(await estates.RemoveIfEmptyAsync(empty, ct));
        Assert.Equal(1L, await Count("opensim", "estate_settings WHERE EstateID = @e", ct, ("@e", empty)));
        await Exec("opensim", "DELETE FROM estate_map WHERE EstateID = @e", ct, ("@e", empty));
        Assert.True(await estates.RemoveIfEmptyAsync(empty, ct));
        foreach (var table in new[] { "estate_settings", "estate_managers", "estateban" })
            Assert.Equal(0L, await Count("opensim", $"{table} WHERE EstateID = @e", ct, ("@e", empty)));
    }

    private const string EstateTables =
        "CREATE TABLE IF NOT EXISTS estate_settings (EstateID int(10) unsigned NOT NULL AUTO_INCREMENT, EstateName varchar(64) DEFAULT NULL, " +
        "AbuseEmailToEstateOwner tinyint(4) NOT NULL, DenyAnonymous tinyint(4) NOT NULL, ResetHomeOnTeleport tinyint(4) NOT NULL, FixedSun tinyint(4) NOT NULL, " +
        "DenyTransacted tinyint(4) NOT NULL, BlockDwell tinyint(4) NOT NULL, DenyIdentified tinyint(4) NOT NULL, AllowVoice tinyint(4) NOT NULL, " +
        "UseGlobalTime tinyint(4) NOT NULL, PricePerMeter int(11) NOT NULL, TaxFree tinyint(4) NOT NULL, AllowDirectTeleport tinyint(4) NOT NULL, " +
        "RedirectGridX int(11) NOT NULL, RedirectGridY int(11) NOT NULL, ParentEstateID int(10) unsigned NOT NULL, SunPosition double NOT NULL, " +
        "EstateSkipScripts tinyint(4) NOT NULL, BillableFactor float NOT NULL, PublicAccess tinyint(4) NOT NULL, AbuseEmail varchar(255) NOT NULL, " +
        "EstateOwner varchar(36) NOT NULL, DenyMinors tinyint(4) NOT NULL, PRIMARY KEY (EstateID)) ENGINE=InnoDB AUTO_INCREMENT=101; " +
        "CREATE TABLE IF NOT EXISTS estate_users (EstateID int(10) unsigned NOT NULL, uuid char(36) NOT NULL, KEY EstateID (EstateID)) ENGINE=InnoDB; " +
        "CREATE TABLE IF NOT EXISTS estate_managers (EstateID int(10) unsigned NOT NULL, uuid char(36) NOT NULL, KEY EstateID (EstateID)) ENGINE=InnoDB; " +
        "CREATE TABLE IF NOT EXISTS estate_groups (EstateID int(10) unsigned NOT NULL, uuid char(36) NOT NULL, KEY EstateID (EstateID)) ENGINE=InnoDB; " +
        "CREATE TABLE IF NOT EXISTS estateban (EstateID int(10) unsigned NOT NULL, bannedUUID varchar(36) NOT NULL, bannedIp varchar(16) NOT NULL, " +
        "bannedIpHostMask varchar(16) NOT NULL, bannedNameMask varchar(64) DEFAULT NULL, KEY estateban_EstateID (EstateID)) ENGINE=InnoDB; " +
        "CREATE TABLE IF NOT EXISTS estate_map (RegionID char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000', EstateID int(11) NOT NULL, " +
        "PRIMARY KEY (RegionID), KEY EstateID (EstateID)) ENGINE=InnoDB;";

    private MySqlConnection Open(string database) => new(new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)_s.MariaDbPort, Database = database,
        UserID = "root", Password = _s.Db.RootPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
    }.ConnectionString);

    private async Task Exec(string db, string sql, CancellationToken ct, params (string Name, object Value)[] p)
    {
        await using var conn = Open(db);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> Scalar(string db, string sql, CancellationToken ct)
    {
        await using var conn = Open(db);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private async Task<long> Count(string db, string from, CancellationToken ct, params (string Name, object Value)[] p)
    {
        await using var conn = Open(db);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM " + from, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
