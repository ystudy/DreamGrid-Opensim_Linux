using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DreamGrid.Launcher.Core.Iar;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Reading and rewriting inventory archives (IAR plan task 3).</summary>
public sealed class IarArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-iar-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static List<string> Names(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } e) names.Add(e.Name);
        return names;
    }

    [Fact]
    public void LooksLikeIar_true_for_iar_false_for_oar_text_and_empty()
    {
        var b = new IarBuilder();
        b.Notecard("Notes", "hello", "hi");
        Assert.True(IarArchive.LooksLikeIar(b.Save(P("a.iar"))));

        File.WriteAllText(P("t.iar"), "not an archive");
        Assert.False(IarArchive.LooksLikeIar(P("t.iar")));
        File.WriteAllBytes(P("e.iar"), []);
        Assert.False(IarArchive.LooksLikeIar(P("e.iar")));
        Assert.False(IarArchive.LooksLikeIar(P("missing.iar")));

        // An OAR also starts with archive.xml; it passes this check, but it holds no inventory.
        var oar = new IarBuilder();
        oar.Raw("objects/x.xml", [1]);
        var contents = IarArchive.Inspect(oar.Save(P("o.iar")), 1 << 20, 100);
        Assert.Equal((0, 0), (contents.Items.Count, contents.Folders));
    }

    [Fact]
    public void Inspect_reads_items_folders_and_permissions()
    {
        var b = new IarBuilder();
        var tex = b.Texture("Clothes/Shoes", "boot", IarBuilder.Copy | IarBuilder.Modify);
        b.Notecard("Notes", "hello", "hi");
        var c = IarArchive.Inspect(b.Save(P("a.iar")), 1 << 20, 100);

        Assert.Equal(2, c.Items.Count);
        Assert.Equal(3, c.Folders);
        var boot = c.Items.Single(i => i.Name == "boot");
        Assert.Equal(("Clothes/Shoes", tex, 0, 0, IarBuilder.Copy | IarBuilder.Modify), (boot.FolderPath, boot.AssetId, boot.AssetType, boot.InvType, boot.CurrentPermissions));
        Assert.StartsWith("inventory/Clothes__", boot.EntryPath, StringComparison.Ordinal);
        Assert.True(c.AssetEntries.ContainsKey(tex));
        Assert.True(c.UnpackedBytes > 0);
    }

    [Fact]
    public void Inspect_stops_at_unpacked_limit()
    {
        var b = new IarBuilder();
        b.Asset(Guid.NewGuid(), "texture.jp2", new byte[1 << 20]);
        var path = b.Save(P("bomb.iar"));
        Assert.True(new FileInfo(path).Length < 64 * 1024); // small on disk

        var e = Assert.Throws<IarTooBigException>(() => IarArchive.Inspect(path, 512 * 1024, 100));
        Assert.Equal("unpacked", e.Limit);
        Assert.True(e.ReadBytes <= 512 * 1024 + 1024);
    }

    [Fact]
    public void Inspect_stops_at_item_limit()
    {
        var b = new IarBuilder();
        for (var i = 0; i < 5; i++) b.Notecard("Notes", "n" + i, "x");
        Assert.Equal("items", Assert.Throws<IarTooBigException>(() => IarArchive.Inspect(b.Save(P("a.iar")), 1 << 20, 4)).Limit);
    }

    [Fact]
    public void Rewrite_drops_item_and_its_only_asset()
    {
        var b = new IarBuilder();
        var keep = b.Texture("T", "keep");
        var drop = b.Texture("T", "drop");
        var source = b.Save(P("a.iar"));
        var dropEntry = IarArchive.Inspect(source, 1 << 20, 100).Items.Single(i => i.Name == "drop").EntryPath;

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string> { dropEntry });

        var c = IarArchive.Inspect(P("b.iar"), 1 << 20, 100);
        Assert.Equal(["keep"], c.Items.Select(i => i.Name));
        Assert.True(c.AssetEntries.ContainsKey(keep));
        Assert.False(c.AssetEntries.ContainsKey(drop));
        Assert.Equal("archive.xml", Names(P("b.iar"))[0]);
    }

    [Fact]
    public void Rewrite_keeps_asset_still_used_by_a_kept_item()
    {
        var b = new IarBuilder();
        var shared = b.Texture("T", "one");
        b.Item(b.Folder("T"), "two", shared, 0, 0);
        var source = b.Save(P("a.iar"));
        var two = IarArchive.Inspect(source, 1 << 20, 100).Items.Single(i => i.Name == "two").EntryPath;

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string> { two });

        Assert.True(IarArchive.Inspect(P("b.iar"), 1 << 20, 100).AssetEntries.ContainsKey(shared));
    }

    [Fact]
    public void Rewrite_drops_texture_reached_only_through_dropped_object()
    {
        var b = new IarBuilder();
        var face = Guid.NewGuid();
        b.Asset(face, "texture.jp2", [1, 2, 3]);
        b.Object("Objects", "chair", IarBuilder.Group(IarBuilder.Part(IarBuilder.Full).Replace("<TextureEntry></TextureEntry>", $"<TextureEntry>{face}</TextureEntry>")));
        b.Notecard("Notes", "n", "nothing here");
        var source = b.Save(P("a.iar"));
        var chair = IarArchive.Inspect(source, 1 << 20, 100).Items.Single(i => i.Name == "chair").EntryPath;

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string> { chair });

        var c = IarArchive.Inspect(P("b.iar"), 1 << 20, 100);
        Assert.False(c.AssetEntries.ContainsKey(face));
        Assert.Single(c.AssetEntries);
    }

    [Fact]
    public void Rewrite_keeps_texture_reached_through_kept_notecard()
    {
        var b = new IarBuilder();
        var pic = Guid.NewGuid();
        b.Asset(pic, "texture.jp2", [1, 2, 3]);
        b.Notecard("Notes", "with picture", $"Linden text version 2 {{ ... {pic.ToString().ToUpperInvariant()} ... }}");
        var source = b.Save(P("a.iar"));

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string>());

        Assert.True(IarArchive.Inspect(P("b.iar"), 1 << 20, 100).AssetEntries.ContainsKey(pic));
    }

    [Fact]
    public void Rewrite_keeps_folders_and_drops_entries_opensim_doesnt_load()
    {
        var b = new IarBuilder();
        b.Folder("Empty/Inner");
        b.Raw("userprofiles/x.xml", Encoding.UTF8.GetBytes("<x/>"));
        var source = b.Save(P("a.iar"));

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string>());

        var names = Names(P("b.iar"));
        Assert.DoesNotContain("userprofiles/x.xml", names);
        Assert.Equal(2, names.Count(n => n.StartsWith("inventory/", StringComparison.Ordinal) && n.EndsWith('/')));
    }

    [Fact]
    public void Rewrite_round_trips_long_names()
    {
        var b = new IarBuilder();
        var longName = new string('f', 150);
        b.Notecard(longName, "n", "x");
        var source = b.Save(P("a.iar"));

        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string>());

        var item = Assert.Single(IarArchive.Inspect(P("b.iar"), 1 << 20, 100).Items);
        Assert.Equal(longName, item.FolderPath);
        Assert.Contains(Names(P("b.iar")), n => n.Length > 150); // .NET reads the launcher's long names too
    }
}
