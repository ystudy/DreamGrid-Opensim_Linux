using GridGate.Launcher.Core.Iar;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Only full-rights items leave the grid, object contents and parts included (IAR plan task 4).</summary>
public sealed class IarPermissionFilterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-iarperm-").FullName;
    private const uint Full = IarBuilder.Full;
    private const uint NoTransfer = IarBuilder.Copy | IarBuilder.Modify;
    private const uint NoCopy = IarBuilder.Modify | IarBuilder.Transfer;
    private const uint NoModify = IarBuilder.Copy | IarBuilder.Transfer;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (IReadOnlyList<LeftOutItem> LeftOut, IarContents Kept) Run(IarBuilder b)
    {
        var source = b.Save(Path.Combine(_dir, "in.iar"));
        var target = Path.Combine(_dir, "out.iar");
        var left = IarPermissionFilter.Apply(source, target);
        return (left, IarArchive.Inspect(target, 1 << 24, 1000));
    }

    [Fact]
    public void Full_rights_item_stays()
    {
        var b = new IarBuilder();
        b.Texture("Pics", "mine");
        var (left, kept) = Run(b);
        Assert.Empty(left);
        Assert.Equal(["mine"], kept.Items.Select(i => i.Name));
    }

    [Theory]
    [InlineData(NoTransfer, LeftOutReason.NoTransfer)]
    [InlineData(NoCopy, LeftOutReason.NoCopy)]
    [InlineData(NoModify, LeftOutReason.NoModify)]
    [InlineData(0u, LeftOutReason.NoCopy)]
    public void No_transfer_item_left_out_with_reason(uint perms, LeftOutReason reason)
    {
        var b = new IarBuilder();
        b.Texture("Shop/Dresses", "dress", perms);
        b.Notecard("Notes", "mine", "x");
        var (left, kept) = Run(b);
        Assert.Equal([new LeftOutItem("dress", "Shop/Dresses", reason)], left);
        Assert.Equal(["mine"], kept.Items.Select(i => i.Name));
    }

    [Fact]
    public void Full_rights_box_with_no_transfer_contents_left_out()
    {
        var b = new IarBuilder();
        var inside = Guid.NewGuid();
        b.Asset(inside, "texture.jp2", [1, 2, 3]);
        b.Object("Objects", "box", IarBuilder.Group(IarBuilder.Part(Full, ("shop texture", NoTransfer, inside))));
        var (left, kept) = Run(b);
        Assert.Equal([new LeftOutItem("box", "Objects", LeftOutReason.ContentsNotFullRights)], left);
        Assert.Empty(kept.Items);
        Assert.False(kept.AssetEntries.ContainsKey(inside));
    }

    [Fact]
    public void Full_rights_box_with_full_rights_contents_stays()
    {
        var b = new IarBuilder();
        var inside = Guid.NewGuid();
        b.Asset(inside, "texture.jp2", [1, 2, 3]);
        b.Object("Objects", "box", IarBuilder.Group(IarBuilder.Part(Full, ("my texture", Full, inside))));
        var (left, kept) = Run(b);
        Assert.Empty(left);
        Assert.True(kept.AssetEntries.ContainsKey(inside));
    }

    [Fact]
    public void Full_rights_linkset_with_no_copy_child_prim_left_out()
    {
        var b = new IarBuilder();
        b.Object("Objects", "house", IarBuilder.Group(IarBuilder.Part(Full), IarBuilder.Part(Full), IarBuilder.Part(NoCopy)));
        var (left, _) = Run(b);
        Assert.Equal([new LeftOutItem("house", "Objects", LeftOutReason.PartsNotFullRights)], left);
    }

    [Fact]
    public void Coalesced_object_with_one_restricted_object_left_out()
    {
        var b = new IarBuilder();
        b.Object("Objects", "taken together", IarBuilder.Coalesced(IarBuilder.Group(IarBuilder.Part(Full)), IarBuilder.Group(IarBuilder.Part(NoTransfer))));
        var (left, _) = Run(b);
        Assert.Equal(LeftOutReason.PartsNotFullRights, Assert.Single(left).Reason);
    }

    [Fact]
    public void Box_holding_a_full_rights_object_with_restricted_contents_left_out()
    {
        var b = new IarBuilder();
        var inner = Guid.NewGuid();
        var shop = Guid.NewGuid();
        b.Asset(shop, "texture.jp2", [1]);
        b.Asset(inner, "object.xml", System.Text.Encoding.UTF8.GetBytes(IarBuilder.Group(IarBuilder.Part(Full, ("shop texture", NoTransfer, shop)))));
        b.Object("Objects", "outer box", IarBuilder.Group(IarBuilder.Part(Full, ("inner box", Full, inner))));
        var (left, kept) = Run(b);
        Assert.Equal(LeftOutReason.ContentsNotFullRights, Assert.Single(left).Reason);
        Assert.Empty(kept.AssetEntries);
    }

    [Fact]
    public void Unparsable_object_left_out()
    {
        var b = new IarBuilder();
        b.Object("Objects", "broken", "<SceneObjectGroup><RootPart>");
        b.Object("Objects", "empty", "<SceneObjectGroup></SceneObjectGroup>");
        var (left, _) = Run(b);
        Assert.Equal(2, left.Count);
        Assert.All(left, l => Assert.Equal(LeftOutReason.PartsNotFullRights, l.Reason));
    }

    [Fact]
    public void Object_whose_asset_is_missing_is_left_out()
    {
        var b = new IarBuilder();
        b.Item(b.Folder("Objects"), "ghost", Guid.NewGuid(), 6, 6);
        var (left, _) = Run(b);
        Assert.Equal(LeftOutReason.PartsNotFullRights, Assert.Single(left).Reason);
    }

    [Fact]
    public void All_left_out_still_writes_a_valid_archive()
    {
        var b = new IarBuilder();
        b.Texture("Shop", "a", NoTransfer);
        b.Texture("Shop", "b", NoCopy);
        var (left, kept) = Run(b);
        Assert.Equal(2, left.Count);
        Assert.True(IarArchive.LooksLikeIar(Path.Combine(_dir, "out.iar")));
        Assert.Empty(kept.Items);
    }

    [Fact]
    public void Assets_of_left_out_items_are_not_in_the_file()
    {
        var b = new IarBuilder();
        var shop = b.Texture("Shop", "logo", NoTransfer);
        var mine = b.Texture("Mine", "photo");
        var (_, kept) = Run(b);
        Assert.False(kept.AssetEntries.ContainsKey(shop));
        Assert.True(kept.AssetEntries.ContainsKey(mine));
    }
}
