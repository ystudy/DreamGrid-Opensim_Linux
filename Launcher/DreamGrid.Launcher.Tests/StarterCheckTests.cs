using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Starter;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Checking a model's stored outfit and a new resident's copy of it (spec 2026-11-11-starter-avatars-design §5.5, §6.3).</summary>
public sealed class StarterCheckTests
{
    private const uint Full = 0x8000 | 0x4000 | 0x2000 | 0x80000;

    internal static InventoryItemRow Row(Guid item, Guid owner, string name, Guid asset, uint next = Full) =>
        new(asset.ToString(), 13, name, "", next, Full, 18, owner.ToString(), Full, 0, 0, 0, 0, Guid.Empty.ToString(), 0, 0, item, owner,
            Guid.NewGuid(), 0);

    private static readonly Guid Model = Guid.NewGuid();
    private static readonly Guid Resident = Guid.NewGuid();

    private static (List<WornItem> Worn, List<InventoryItemRow> Items) Outfit(params (string Slot, string Name)[] slots)
    {
        var worn = new List<WornItem>();
        var items = new List<InventoryItemRow>();
        foreach (var (slot, name) in slots)
        {
            var id = Guid.NewGuid();
            worn.Add(new WornItem(slot, id));
            items.Add(Row(id, Model, name, Guid.NewGuid()));
        }
        return (worn, items);
    }

    /// <summary>The resident's copy as Robust makes it: new item ids, the same assets, worn in the same slots.</summary>
    private static (List<WornItem> Worn, List<InventoryItemRow> Items) CopyOf(List<WornItem> worn, List<InventoryItemRow> items)
    {
        var w = new List<WornItem>();
        var r = new List<InventoryItemRow>();
        foreach (var x in worn)
        {
            var src = items.Single(i => i.ItemId == x.ItemId);
            var id = Guid.NewGuid();
            w.Add(x with { ItemId = id });
            r.Add(src with { ItemId = id, Owner = Resident });
        }
        return (w, r);
    }

    [Fact]
    public void Model_without_appearance_fails()
    {
        var r = StarterCheck.Model(null, []);
        Assert.False(r.Passed);
        Assert.Contains("Replace Outfit", Assert.Single(r.Missing));
        Assert.False(StarterCheck.Model([], []).Passed);
    }

    [Fact]
    public void Model_missing_a_worn_item_names_its_slot()
    {
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("Wearable 5:0", "Jacket"), ("_ap_2", "Hair"));
        var r = StarterCheck.Model(worn, items.Where(i => i.Name != "Jacket").ToList());
        Assert.False(r.Passed);
        Assert.Equal(["Wearable 5:0"], r.Missing);
        Assert.True(StarterCheck.Model(worn, items).Passed);
    }

    [Fact]
    public void Model_no_copy_hair_warns_but_passes()
    {
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("_ap_2", "Hair"));
        items[1] = items[1] with { NextPermissions = 0x4000 | 0x2000 };
        items[0] = items[0] with { NextPermissions = 0x8000 | 0x2000 };
        var r = StarterCheck.Model(worn, items);
        Assert.True(r.Passed);
        Assert.Equal(["Shape: residents can't modify", "Hair: residents can't copy"], r.PermissionWarnings);
    }

    [Fact]
    public void Model_wearing_items_outside_its_outfit_folder_fails()
    {
        // Final review C1: a new model already wears the default outfit Robust made, from its own inventory.
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("_ap_2", "Hair"));
        var outfit = Guid.NewGuid();
        var inside = items.Select(i => i with { Folder = outfit }).ToList();
        Assert.True(StarterCheck.Model(worn, inside, new HashSet<Guid> { outfit }).Passed);
        var r = StarterCheck.Model(worn, items, new HashSet<Guid> { outfit });
        Assert.False(r.Passed);
        Assert.Equal([StarterCheck.NotTheOutfit], r.Missing);
    }

    [Fact]
    public void Model_wearing_only_the_default_outfit_fails()
    {
        var worn = new List<WornItem>();
        var items = new List<InventoryItemRow>();
        foreach (var (slot, asset) in new[] { ("Wearable 0:0", "66c41e39-38f9-f75a-024e-585989bfab73"), ("Wearable 1:0", "77c41e39-38f9-f75a-024e-585989bbabbb"),
                     ("Wearable 4:0", "00000000-38f9-1111-024e-222222111110") })
        {
            var id = Guid.NewGuid();
            worn.Add(new WornItem(slot, id));
            items.Add(Row(id, Model, slot, Guid.Parse(asset)));
        }
        var r = StarterCheck.Model(worn, items);
        Assert.False(r.Passed);
        Assert.Equal([StarterCheck.NotTheOutfit], r.Missing);
    }

    [Fact]
    public void Model_with_two_attachments_on_one_point_fails()
    {
        // Final review I1: Robust's copy keeps only the last attachment on each point.
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("_ap_2", "Hair"), ("_ap_2", "Head"));
        var r = StarterCheck.Model(worn, items);
        Assert.False(r.Passed);
        Assert.Equal(["_ap_2: Hair, Head on one point; only one is copied to residents, so move the others to other points"], r.Missing);
    }

    [Fact]
    public void Copy_matches_slot_by_asset()
    {
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("Wearable 4:0", "Shirt"), ("_ap_2", "Hair"), ("_ap_9", "Hat"));
        var (rw, ri) = CopyOf(worn, items);
        Assert.True(StarterCheck.Copy(worn, items, rw, ri).Passed);
    }

    [Fact]
    public void Copy_with_an_empty_slot_fails()
    {
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"), ("Wearable 4:0", "Shirt"), ("_ap_2", "Hair"));
        var (rw, ri) = CopyOf(worn, items);
        rw.RemoveAll(w => w.Slot == "_ap_2");
        var r = StarterCheck.Copy(worn, items, rw, ri);
        Assert.False(r.Passed);
        Assert.Equal(["_ap_2"], r.Missing);

        // Worn in the slot, but a different asset (Ruth's shirt): still missing.
        var (rw2, ri2) = CopyOf(worn, items);
        ri2[1] = ri2[1] with { AssetId = Guid.NewGuid().ToString() };
        Assert.Equal(["Wearable 4:0"], StarterCheck.Copy(worn, items, rw2, ri2).Missing);
    }

    [Fact]
    public void Copy_with_no_appearance_fails()
    {
        var (worn, items) = Outfit(("Wearable 0:0", "Shape"));
        Assert.False(StarterCheck.Copy(worn, items, null, []).Passed);
    }

    [Fact]
    public void Avatars_rows_parse_wearables_and_attachments()
    {
        var shape = Guid.NewGuid();
        var hair = Guid.NewGuid();
        var hat = Guid.NewGuid();
        var worn = AppearanceStore.Parse([
            ("AvatarType", "1"),
            ("Serial", "0"),
            ("Wearable 0:0", $"{shape}:{Guid.NewGuid()}"),
            ("Wearable 5:0", $"{Guid.Empty}:{Guid.Empty}"),
            ("_ap_2", $"{hair},{hat}"),
            ("VisualParams", "1,2,3"),
        ]);
        Assert.Equal([new WornItem("Wearable 0:0", shape), new WornItem("_ap_2", hair), new WornItem("_ap_2", hat)], worn);
    }
}
