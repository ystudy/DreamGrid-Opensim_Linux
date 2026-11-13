using System.Diagnostics;
using GridGate.Launcher.Core.Groups;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static GridGate.Launcher.IntegrationTests.GridTestSupport;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The launcher's changes to OpenSim's core group tables, against the real tables Robust's own migrations make. One grid is
/// started and each behaviour runs on its own groups, in order.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class GridGroupsTests : IDisposable
{
    private const string Zero = "00000000-0000-0000-0000-000000000000";
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
    public async Task Group_tables_are_read_and_changed_the_way_OpenSim_writes_them()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        _s = SeedSettings(store);
        var robust = NewRobustClient(_http, _s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var groups = new GridGroups(store.LoadOrCreate);
        try
        {
            await grid.StartAsync(ct); // MariaDB, then Robust, which creates its tables
            var owner = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            var ann = await grid.CreateResidentAsync("Ann", "Smith", null, 0, ct);
            var bob = await grid.CreateResidentAsync("Bob", "Jones", null, 0, ct);
            var pending = await grid.CreateResidentAsync("Pat", "Waiting", null, -1, ct);
            const string visitor = "6a0f3c2e-1d55-4c1b-9b9e-0d8a4f3b2c11;https://grid.example:8002/;Hope Visitor";

            // List and get: founder, members, roles, open to join.
            var builders = await SeedGroup("Builders", owner.PrincipalId, open: true, ct);
            await Exec("INSERT INTO os_groups_membership (GroupID, PrincipalID, SelectedRoleID, AccessToken) VALUES (@g, @p, @z, @t)", ct,
                ("@g", builders.Id), ("@p", visitor), ("@z", Zero), ("@t", Guid.NewGuid().ToString()));
            await Exec("INSERT INTO os_groups_rolemembership (GroupID, RoleID, PrincipalID) VALUES (@g, @z, @p)", ct,
                ("@g", builders.Id), ("@z", Zero), ("@p", visitor));
            var list = await groups.ListAsync(ct);
            var row = Assert.Single(list, g => g.Name == "Builders");
            Assert.Equal("Test Owner", row.Founder);
            Assert.Equal(2, row.Members);
            Assert.True(row.OpenToJoin);
            var detail = await groups.GetAsync(Guid.Parse(builders.Id), ct);
            Assert.NotNull(detail);
            var founder = Assert.Single(detail.Members, m => m.Local);
            Assert.Equal("Test Owner", founder.Name);
            Assert.True(founder.Owner);
            Assert.Contains("Owners", founder.Roles);
            Assert.Contains("Everyone", founder.Roles);
            var hg = Assert.Single(detail.Members, m => !m.Local);
            Assert.Equal(visitor, hg.PrincipalId);
            Assert.Equal("Hope Visitor (visitor)", hg.Name);
            Assert.False(hg.Owner);
            Assert.Null(await groups.GetAsync(Guid.NewGuid(), ct));

            // Update: rename and charter; a name taken in another case; a missing group.
            var gid = Guid.Parse(builders.Id);
            Assert.Equal(RenameResult.Done, await groups.UpdateAsync(gid, "Master Builders", "We build.", ct));
            detail = await groups.GetAsync(gid, ct);
            Assert.Equal("Master Builders", detail!.Name);
            Assert.Equal("We build.", detail.Charter);
            var other = await SeedGroup("Gardeners", owner.PrincipalId, open: false, ct);
            Assert.Equal(RenameResult.NameTaken, await groups.UpdateAsync(Guid.Parse(other.Id), "master builders", "", ct));
            Assert.Equal("Gardeners", (await groups.GetAsync(Guid.Parse(other.Id), ct))!.Name);
            Assert.Equal(RenameResult.NoGroup, await groups.UpdateAsync(Guid.NewGuid(), "Nobody", "", ct));

            // Add: membership with the Everyone role, the invite gone, the active group untouched; twice is AlreadyMember.
            await Exec("INSERT INTO os_groups_invites (InviteID, GroupID, RoleID, PrincipalID) VALUES (@i, @g, @z, @p)", ct,
                ("@i", Guid.NewGuid().ToString()), ("@g", builders.Id), ("@z", Zero), ("@p", ann.PrincipalId.ToString()));
            Assert.Equal(AddResult.Added, await groups.AddMemberAsync(gid, ann.PrincipalId, ct));
            Assert.Equal(1L, await Count("os_groups_membership WHERE GroupID=@g AND PrincipalID=@p AND SelectedRoleID=@z AND ListInProfile=1 AND AcceptNotices=1", ct,
                ("@g", builders.Id), ("@p", ann.PrincipalId.ToString()), ("@z", Zero)));
            Assert.Equal(1L, await Count("os_groups_rolemembership WHERE GroupID=@g AND PrincipalID=@p AND RoleID=@z", ct,
                ("@g", builders.Id), ("@p", ann.PrincipalId.ToString()), ("@z", Zero)));
            Assert.Equal(0L, await Count("os_groups_invites WHERE GroupID=@g AND PrincipalID=@p", ct,
                ("@g", builders.Id), ("@p", ann.PrincipalId.ToString())));
            Assert.Equal(0L, await Count("os_groups_principals WHERE PrincipalID=@p", ct, ("@p", ann.PrincipalId.ToString())));
            Assert.Equal(AddResult.AlreadyMember, await groups.AddMemberAsync(gid, ann.PrincipalId, ct));
            Assert.Equal(AddResult.NoGroup, await groups.AddMemberAsync(Guid.NewGuid(), ann.PrincipalId, ct));
            Assert.Contains(ann.PrincipalId.ToString(), await groups.MemberIdsAsync(gid, ct), StringComparer.OrdinalIgnoreCase);

            // Remove: roles gone and the active group cleared.
            await Exec("INSERT INTO os_groups_principals (PrincipalID, ActiveGroupID) VALUES (@p, @g)", ct,
                ("@p", ann.PrincipalId.ToString()), ("@g", builders.Id));
            Assert.True(await groups.RemoveMemberAsync(gid, ann.PrincipalId.ToString(), ct));
            Assert.Equal(0L, await Count("os_groups_membership WHERE GroupID=@g AND PrincipalID=@p", ct, ("@g", builders.Id), ("@p", ann.PrincipalId.ToString())));
            Assert.Equal(0L, await Count("os_groups_rolemembership WHERE GroupID=@g AND PrincipalID=@p", ct, ("@g", builders.Id), ("@p", ann.PrincipalId.ToString())));
            Assert.Equal(1L, await Count("os_groups_principals WHERE PrincipalID=@p AND ActiveGroupID=@z", ct, ("@p", ann.PrincipalId.ToString()), ("@z", Zero)));
            Assert.False(await groups.RemoveMemberAsync(gid, ann.PrincipalId.ToString(), ct));

            // Remove a hypergrid member by its full ID.
            Assert.True(await groups.RemoveMemberAsync(gid, visitor, ct));
            Assert.Equal(0L, await Count("os_groups_membership WHERE GroupID=@g AND PrincipalID=@p", ct, ("@g", builders.Id), ("@p", visitor)));

            // Delete: every row of the group gone, active groups cleared.
            await groups.AddMemberAsync(gid, bob.PrincipalId, ct);
            await Exec("INSERT INTO os_groups_principals (PrincipalID, ActiveGroupID) VALUES (@p, @g)", ct,
                ("@p", bob.PrincipalId.ToString()), ("@g", builders.Id));
            await Exec("INSERT INTO os_groups_notices (GroupID, NoticeID, Message) VALUES (@g, @n, 'hi')", ct,
                ("@g", builders.Id), ("@n", Guid.NewGuid().ToString()));
            await Exec("INSERT INTO os_groups_invites (InviteID, GroupID, RoleID, PrincipalID) VALUES (@i, @g, @z, @p)", ct,
                ("@i", Guid.NewGuid().ToString()), ("@g", builders.Id), ("@z", Zero), ("@p", pending.PrincipalId.ToString()));
            Assert.True(await groups.DeleteAsync(gid, ct));
            foreach (var table in new[] { "os_groups_groups", "os_groups_membership", "os_groups_roles", "os_groups_rolemembership", "os_groups_invites", "os_groups_notices" })
                Assert.Equal(0L, await Count(table + " WHERE GroupID=@g", ct, ("@g", builders.Id)));
            Assert.Equal(0L, await Count("os_groups_principals WHERE ActiveGroupID=@g", ct, ("@g", builders.Id)));
            Assert.False(await groups.DeleteAsync(gid, ct));
            Assert.NotNull(await groups.GetAsync(Guid.Parse(other.Id), ct)); // the other group is untouched

            // Active residents: level 0 and above only.
            var active = await groups.ActiveResidentsAsync(ct);
            Assert.Contains(ann.PrincipalId, active);
            Assert.Contains(owner.PrincipalId, active);
            Assert.DoesNotContain(pending.PrincipalId, active);

            // A database that is down: GroupsUnavailableException, quickly.
            await grid.StopAllAsync(ct);
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<GroupsUnavailableException>(() => groups.ListAsync(ct));
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

    private sealed record Seeded(string Id, string OwnerRole);

    /// <summary>A group as OpenSim's GroupsService.CreateGroup makes one: the row, Everyone and Owners roles, the founder in both.</summary>
    private async Task<Seeded> SeedGroup(string name, Guid founder, bool open, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString(), ownerRole = Guid.NewGuid().ToString(), f = founder.ToString();
        await Exec("INSERT INTO os_groups_groups (GroupID, Name, Charter, InsigniaID, FounderID, OpenEnrollment, ShowInList, OwnerRoleID) " +
                   "VALUES (@g, @n, '', @z, @f, @o, 1, @r)", ct,
            ("@g", id), ("@n", name), ("@z", Zero), ("@f", f), ("@o", open ? "1" : "0"), ("@r", ownerRole));
        await Exec("INSERT INTO os_groups_roles (GroupID, RoleID, Name, Title, Powers) VALUES (@g, @z, 'Everyone', 'Member', 1), (@g, @r, 'Owners', 'Owner', 99)", ct,
            ("@g", id), ("@z", Zero), ("@r", ownerRole));
        await Exec("INSERT INTO os_groups_membership (GroupID, PrincipalID, SelectedRoleID, AccessToken) VALUES (@g, @f, @r, @t)", ct,
            ("@g", id), ("@f", f), ("@r", ownerRole), ("@t", Guid.NewGuid().ToString()));
        await Exec("INSERT INTO os_groups_rolemembership (GroupID, RoleID, PrincipalID) VALUES (@g, @z, @f), (@g, @r, @f)", ct,
            ("@g", id), ("@z", Zero), ("@r", ownerRole), ("@f", f));
        return new Seeded(id, ownerRole);
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

    private async Task<long> Count(string from, CancellationToken ct, params (string Name, object Value)[] p)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM " + from, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
