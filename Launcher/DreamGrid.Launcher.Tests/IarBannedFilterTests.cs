using System.Security.Cryptography;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Taken-down content can't come back in an upload (IAR plan task 5).</summary>
public sealed class IarBannedFilterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-iarban-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Stolen = [9, 8, 7, 6, 5, 4];

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private TakedownStore Store() => new(Path.Combine(_dir, "takedowns"), _time, NullLogger<TakedownStore>.Instance);

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A case that took down one item with asset <paramref name="asset"/>; closed or put back as asked.</summary>
    private TakedownCase Case(TakedownStore store, Guid asset, bool open = true, bool putBack = false)
    {
        var holder = Guid.NewGuid();
        var row = new InventoryItemRow(asset.ToString(), 0, "Logo", "", 1, 2, 0, holder.ToString(), 3, 4, 5, 0, 6, Guid.Empty.ToString(), 0, 7,
            Guid.NewGuid(), holder, Guid.NewGuid(), 8);
        return store.Open(new TakedownNotice(TakedownReason.Copyright, "Rita", null, null, null, new DateOnly(2026, 11, 1), "ours"), true,
            n => TakedownStoreTests.Build(n, holder) with
            {
                Open = open,
                Items = [new CaseItem(row.ItemId, holder, "Logo", null, 0, holder.ToString(), "Ann", "My Inventory", asset.ToString(), row, null, null, null,
                    _time.GetUtcNow(), putBack ? _time.GetUtcNow() : null)],
                Events = open ? [] : [new CaseEvent(_time.GetUtcNow(), "Test Admin", null, "closed", "done")],
            });
    }

    private string Archive(Action<IarBuilder> fill)
    {
        var b = new IarBuilder();
        fill(b);
        return b.Save(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".iar"));
    }

    private static FakeInventoryStore Inventory(params (Guid Asset, byte[] Data)[] hashes)
    {
        var inv = new FakeInventoryStore(new FakeRobustAccounts());
        foreach (var (asset, data) in hashes) inv.Hashes[asset.ToString()] = Hash(data);
        return inv;
    }

    [Fact]
    public async Task Match_by_asset_id()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        var c = Case(store, asset);
        var iar = Archive(b => { b.Texture("Pics", "logo copy", asset: asset); b.Texture("Pics", "mine"); });

        var banned = await store.BannedAssetsAsync(Inventory(), Ct);
        var matches = IarBannedFilter.Find(iar, banned, store.Unreadable);

        Assert.Equal([("logo copy", c.Number)], matches.Select(m => (m.ItemName, m.CaseNumber)));
    }

    [Fact]
    public async Task Match_by_content_hash_with_a_new_id()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        var c = Case(store, asset);
        var iar = Archive(b =>
        {
            var renamed = Guid.NewGuid();
            b.Asset(renamed, "texture.jp2", Stolen);
            b.Item(b.Folder("Pics"), "same bytes", renamed, 0, 0);
        });

        var matches = IarBannedFilter.Find(iar, await store.BannedAssetsAsync(Inventory((asset, Stolen)), Ct), store.Unreadable);

        Assert.Equal([("same bytes", c.Number)], matches.Select(m => (m.ItemName, m.CaseNumber)));
    }

    [Fact]
    public async Task Object_holding_a_banned_texture_matches()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        Case(store, asset);
        var iar = Archive(b =>
        {
            b.Asset(asset, "texture.jp2", Stolen);
            b.Object("Objects", "sign", IarBuilder.Group(IarBuilder.Part(IarBuilder.Full).Replace("<TextureEntry></TextureEntry>", $"<TextureEntry>{asset}</TextureEntry>")));
        });

        var match = Assert.Single(IarBannedFilter.Find(iar, await store.BannedAssetsAsync(Inventory(), Ct), store.Unreadable));
        Assert.Equal("sign", match.ItemName);
    }

    [Fact]
    public async Task Put_back_item_is_not_banned()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        Case(store, asset, putBack: true);
        var iar = Archive(b => b.Texture("Pics", "logo", asset: asset));

        Assert.Empty(IarBannedFilter.Find(iar, await store.BannedAssetsAsync(Inventory(), Ct), store.Unreadable));
    }

    [Fact]
    public async Task Closed_case_items_are_banned()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        var c = Case(store, asset, open: false);
        var iar = Archive(b => b.Texture("Pics", "logo", asset: asset));

        Assert.Equal(c.Number, Assert.Single(IarBannedFilter.Find(iar, await store.BannedAssetsAsync(Inventory(), Ct), store.Unreadable)).CaseNumber);
    }

    [Fact]
    public async Task Clean_leaves_out_matches_and_keeps_the_rest()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        Case(store, asset);
        var iar = Archive(b => { b.Texture("Pics", "logo", asset: asset); b.Notecard("Notes", "mine", "x"); });
        var banned = await store.BannedAssetsAsync(Inventory(), Ct);
        var matches = IarBannedFilter.Find(iar, banned, store.Unreadable);

        var clean = Path.Combine(_dir, "clean.iar");
        IarBannedFilter.Clean(iar, clean, matches, banned);

        var kept = IarArchive.Inspect(clean, 1 << 20, 100);
        Assert.Equal(["mine"], kept.Items.Select(i => i.Name));
        Assert.False(kept.AssetEntries.ContainsKey(asset));
    }

    [Fact]
    public async Task Banned_hashes_are_read_once_per_asset()
    {
        var store = Store();
        var asset = Guid.NewGuid();
        Case(store, asset);
        var inv = Inventory((asset, Stolen));
        Assert.Equal(Hash(Stolen), Assert.Single(await store.BannedAssetsAsync(inv, Ct)).Hash);
        inv.Hashes.Clear();
        Assert.Equal(Hash(Stolen), Assert.Single(await store.BannedAssetsAsync(inv, Ct)).Hash);
    }

    [Fact]
    public void Unreadable_takedowns_refuse_upload_check()
    {
        var iar = Archive(b => b.Notecard("Notes", "n", "x"));
        Assert.Throws<TakedownsUnreadableException>(() => IarBannedFilter.Find(iar, [], unreadable: true));
    }
}
