using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>
/// Reading, taking down and putting back inventory items against the real tables (spec 2026-11-04-inventory-takedown-design).
/// One grid start covers every case, because each start costs about a minute.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class InventoryStoreIntegrationTests : IDisposable
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
    public async Task Inventory_store_against_the_real_tables()
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
        var ann = (await grid.CreateResidentAsync("Ann", "Holder", null, 0, ct)).PrincipalId;
        var bob = (await grid.CreateResidentAsync("Bob", "Copier", null, 0, ct)).PrincipalId;
        var inv = new InventoryStore(store.LoadOrCreate);

        await Remove_deletes_only_the_named_items_and_raises_their_folder_versions(inv, ann, ct);
        await Put_back_restores_every_column_into_its_old_folder(inv, ann, ct);
        await Put_back_without_its_folder_goes_to_lost_and_found(inv, ann, ct);
        await Put_back_without_lost_and_found_goes_to_my_inventory(inv, ann, ct);
        await Copies_finds_the_same_asset_in_other_inventories_and_skips_links(inv, ann, bob, ct);
        await Search_escapes_like_wildcards(inv, ann, ct);
        await Hypergrid_creator_shows_its_name_and_host(inv, ann, ct);
        Assert.Null(await inv.AssetHashAsync(Guid.NewGuid().ToString(), ct)); // Asset_hash_is_null_for_an_unknown_asset
        await Create_folder_goes_under_my_inventory_and_raises_its_version(inv, bob, ct);
        await Stored_appearance_reads_the_default_outfit(new AppearanceStore(store.LoadOrCreate), inv, bob, ct);
    }

    /// <summary>A new account gets the default outfit (CreateDefaultAvatarEntries): every worn item is in its own inventory.</summary>
    private static async Task Stored_appearance_reads_the_default_outfit(AppearanceStore looks, InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var worn = await looks.WornAsync(owner, ct);
        Assert.NotNull(worn);
        Assert.Contains(worn, w => w.Slot == "Wearable 0:0");
        var items = await inv.ItemsAsync(owner, [.. worn.Select(w => w.ItemId)], ct);
        // Final review C1: the default outfit a new account gets never passes for a starter avatar.
        Assert.Equal(items.Count, worn.Count);
        Assert.False(DreamGrid.Launcher.Core.Starter.StarterCheck.Model(worn, [.. items.Select(i => i.Row)]).Passed);
        Assert.Null(await looks.WornAsync(Guid.NewGuid(), ct));
    }

    private async Task Remove_deletes_only_the_named_items_and_raises_their_folder_versions(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var folder = await NewFolderAsync(owner, "Takedown A", 0, ct);
        var other = await NewFolderAsync(owner, "Takedown B", 0, ct);
        var a = await NewItemAsync(owner, folder, "First", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        var b = await NewItemAsync(owner, folder, "Second", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        var c = await NewItemAsync(owner, other, "Third", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        var before = await VersionAsync(folder, ct);

        await inv.RemoveAsync(owner, [a], ct);

        Assert.Equal(0L, await Count("inventoryitems WHERE inventoryID = @i", ct, ("@i", a.ToString())));
        Assert.Equal(1L, await Count("inventoryitems WHERE inventoryID = @i", ct, ("@i", b.ToString())));
        Assert.Equal(1L, await Count("inventoryitems WHERE inventoryID = @i", ct, ("@i", c.ToString())));
        Assert.Equal(before + 1, await VersionAsync(folder, ct));
    }

    private async Task Put_back_restores_every_column_into_its_old_folder(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var folder = await NewFolderAsync(owner, "Put back", 0, ct);
        var id = await NewItemAsync(owner, folder, "Hat", Guid.NewGuid().ToString(), 6, owner.ToString(), ct);
        var saved = Assert.Single(await inv.ItemsAsync(owner, [id], ct)).Row;

        await inv.RemoveAsync(owner, [id], ct);
        Assert.Empty(await inv.ItemsAsync(owner, [id], ct));
        var into = await inv.PutBackAsync(saved, ct);

        Assert.Equal(folder, into);
        Assert.Equal(saved, Assert.Single(await inv.ItemsAsync(owner, [id], ct)).Row);
    }

    private async Task Put_back_without_its_folder_goes_to_lost_and_found(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var folder = await NewFolderAsync(owner, "Gone soon", 0, ct);
        var id = await NewItemAsync(owner, folder, "Lost", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        var saved = Assert.Single(await inv.ItemsAsync(owner, [id], ct)).Row;
        await inv.RemoveAsync(owner, [id], ct);
        await Exec("DELETE FROM inventoryfolders WHERE folderID = @f", ct, ("@f", folder.ToString()));

        var into = await inv.PutBackAsync(saved, ct);

        Assert.Equal(await FolderOfTypeAsync(owner, 16, ct), into);
    }

    private async Task Put_back_without_lost_and_found_goes_to_my_inventory(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var folder = await NewFolderAsync(owner, "Gone too", 0, ct);
        var id = await NewItemAsync(owner, folder, "Lost again", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        var saved = Assert.Single(await inv.ItemsAsync(owner, [id], ct)).Row;
        await inv.RemoveAsync(owner, [id], ct);
        await Exec("DELETE FROM inventoryfolders WHERE folderID = @f", ct, ("@f", folder.ToString()));
        await Exec("UPDATE inventoryitems SET parentFolderID = @r WHERE parentFolderID = @l", ct,
            ("@r", (await FolderOfTypeAsync(owner, 8, ct)).ToString()), ("@l", (await FolderOfTypeAsync(owner, 16, ct)).ToString()));
        await Exec("DELETE FROM inventoryfolders WHERE agentID = @a AND type = 16", ct, ("@a", owner.ToString()));

        var into = await inv.PutBackAsync(saved, ct);

        Assert.Equal(await FolderOfTypeAsync(owner, 8, ct), into);
    }

    private async Task Copies_finds_the_same_asset_in_other_inventories_and_skips_links(InventoryStore inv, Guid ann, Guid bob, CancellationToken ct)
    {
        var asset = Guid.NewGuid().ToString();
        await NewItemAsync(ann, await FolderOfTypeAsync(ann, 8, ct), "Stolen", asset, 0, ann.ToString(), ct);
        await NewItemAsync(bob, await FolderOfTypeAsync(bob, 8, ct), "Bought", asset, 0, ann.ToString(), ct);
        await NewItemAsync(bob, await FolderOfTypeAsync(bob, 8, ct), "Link to it", asset, 24, ann.ToString(), ct);

        var copies = await inv.CopiesAsync(asset, 200, ct);

        Assert.Equal(2, copies.Count);
        Assert.Contains(copies, c => c.Owner == ann && c.OwnerName == "Ann Holder");
        Assert.Contains(copies, c => c.Owner == bob && c.OwnerName == "Bob Copier" && c.Item.Row.Name == "Bought");
        Assert.DoesNotContain(copies, c => c.Item.Row.AssetType == 24);
    }

    private async Task Search_escapes_like_wildcards(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var root = await FolderOfTypeAsync(owner, 8, ct);
        await NewItemAsync(owner, root, "100% Silk", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);
        await NewItemAsync(owner, root, "100 Silk", Guid.NewGuid().ToString(), 0, owner.ToString(), ct);

        var found = await inv.SearchAsync(owner, "100%", 0, 50, ct);

        Assert.Equal("100% Silk", Assert.Single(found).Row.Name);
    }

    private async Task Hypergrid_creator_shows_its_name_and_host(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var root = await FolderOfTypeAsync(owner, 8, ct);
        var id = await NewItemAsync(owner, root, "Visitor gift", Guid.NewGuid().ToString(), 0,
            $"{Guid.NewGuid()};http://other.grid:8002/;Hal Gee", ct);

        var item = Assert.Single(await inv.ItemsAsync(owner, [id], ct));

        Assert.Equal("Hal Gee (other.grid)", item.CreatorName);
        Assert.StartsWith("My Inventory", item.FolderPath);
    }

    private async Task Create_folder_goes_under_my_inventory_and_raises_its_version(InventoryStore inv, Guid owner, CancellationToken ct)
    {
        var root = await FolderOfTypeAsync(owner, 8, ct);
        var before = await VersionAsync(root, ct);
        var made = await inv.CreateFolderAsync(owner, "Imported 2026-11-11", ct);
        var folder = Assert.Single(await inv.FoldersAsync(owner, ct), f => f.Id == made);
        Assert.Equal((root, "Imported 2026-11-11", -1), (folder.Parent!.Value, folder.Name, folder.Type));
        Assert.Equal(before + 1, await VersionAsync(root, ct));
        Assert.Equal(1, await VersionAsync(made, ct));
        await Assert.ThrowsAsync<NoInventoryException>(() => inv.CreateFolderAsync(Guid.NewGuid(), "x", ct));
    }

    private async Task<Guid> NewFolderAsync(Guid owner, string name, int type, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await Exec("INSERT INTO inventoryfolders (folderName, type, version, folderID, agentID, parentFolderID) VALUES (@n, @t, 1, @f, @a, @p)", ct,
            ("@n", name), ("@t", type), ("@f", id.ToString()), ("@a", owner.ToString()), ("@p", (await FolderOfTypeAsync(owner, 8, ct)).ToString()));
        return id;
    }

    private async Task<Guid> NewItemAsync(Guid owner, Guid folder, string name, string asset, int assetType, string creator, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await Exec("INSERT INTO inventoryitems (assetID, assetType, inventoryName, inventoryDescription, inventoryNextPermissions, " +
                   "inventoryCurrentPermissions, invType, creatorID, inventoryBasePermissions, inventoryEveryOnePermissions, salePrice, saleType, " +
                   "creationDate, groupID, groupOwned, flags, inventoryID, avatarID, parentFolderID, inventoryGroupPermissions) VALUES " +
                   "(@as, @at, @n, 'a description', 581632, 2147483647, @at, @c, 2147483647, 0, 10, 0, 1762214400, @z, 0, 0, @i, @o, @f, 0)", ct,
            ("@as", asset), ("@at", assetType), ("@n", name), ("@c", creator), ("@z", Guid.Empty.ToString()), ("@i", id.ToString()),
            ("@o", owner.ToString()), ("@f", folder.ToString()));
        return id;
    }

    private async Task<Guid> FolderOfTypeAsync(Guid owner, int type, CancellationToken ct)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT folderID FROM inventoryfolders WHERE agentID = @a AND type = @t LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@a", owner.ToString());
        cmd.Parameters.AddWithValue("@t", type);
        return InventoryStore.IdOf(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<int> VersionAsync(Guid folder, CancellationToken ct)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT version FROM inventoryfolders WHERE folderID = @f", conn);
        cmd.Parameters.AddWithValue("@f", folder.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private MySqlConnection Open() => new(new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)_s.MariaDbPort, Database = "robust",
        UserID = "root", Password = _s.Db.RootPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
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
