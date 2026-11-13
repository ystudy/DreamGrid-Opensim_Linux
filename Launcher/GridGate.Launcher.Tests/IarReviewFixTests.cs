using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Iar;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The final review's findings on the archive code, each pinned (IAR plan, final fix pass).</summary>
public sealed class IarReviewFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-iarfix-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A TextureEntry as OpenSim writes it: base64 of binary, the default texture's id first, in OpenMetaverse byte order.</summary>
    internal static string TextureEntry(Guid texture)
    {
        var bytes = texture.ToByteArray(bigEndian: true).Concat(new byte[] { 0, 0, 0, 0, 0x80, 0x3f, 0, 0 }).ToArray();
        return Convert.ToBase64String(bytes);
    }

    private static string TexturedObject(Guid texture) =>
        IarBuilder.Group(IarBuilder.Part(IarBuilder.Full).Replace("<TextureEntry></TextureEntry>", $"<TextureEntry>{TextureEntry(texture)}</TextureEntry>"));

    [Fact]
    public void Rewrite_keeps_texture_named_in_a_base64_texture_entry()
    {
        var b = new IarBuilder();
        var face = Guid.NewGuid();
        b.Asset(face, "texture.jp2", [1, 2, 3]);
        b.Object("Objects", "chair", TexturedObject(face));
        IarArchive.Rewrite(b.Save(P("a.iar")), P("b.iar"), new HashSet<string>());
        Assert.True(IarArchive.Inspect(P("b.iar"), 1 << 20, 10).AssetEntries.ContainsKey(face));
    }

    [Fact]
    public void Base64_face_texture_of_a_banned_asset_matches_the_object()
    {
        var b = new IarBuilder();
        var face = Guid.NewGuid();
        b.Asset(face, "texture.jp2", [1, 2, 3]);
        b.Object("Objects", "sign", TexturedObject(face));
        var matches = IarBannedFilter.Find(b.Save(P("a.iar")), [new BannedAsset(face, null, "TD-2026-001")], false);
        Assert.Equal("sign", Assert.Single(matches).ItemName);
    }

    [Fact]
    public void Clean_drops_a_loose_banned_asset_and_any_banned_hash()
    {
        var b = new IarBuilder();
        var loose = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        byte[] stolen = [9, 9, 9, 9];
        b.Asset(loose, "texture.jp2", [7, 7]);
        b.Asset(renamed, "texture.jp2", stolen);
        b.Notecard("Notes", "mine", "x");
        var source = b.Save(P("a.iar"));
        var banned = new List<BannedAsset>
        {
            new(loose, null, "TD-2026-001"),
            new(Guid.NewGuid(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stolen)), "TD-2026-002"),
        };

        var matches = IarBannedFilter.Find(source, banned, false);
        var dropped = IarBannedFilter.Clean(source, P("clean.iar"), matches, banned);

        var kept = IarArchive.Inspect(P("clean.iar"), 1 << 20, 10);
        Assert.Equal(["mine"], kept.Items.Select(i => i.Name));
        Assert.False(kept.AssetEntries.ContainsKey(loose));
        Assert.False(kept.AssetEntries.ContainsKey(renamed));
        Assert.Equal(2, dropped);
    }

    private string RawTar(Action<TarWriter> write, TarEntryFormat format = TarEntryFormat.Pax)
    {
        var path = P(Guid.NewGuid().ToString("N") + ".iar");
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, format);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "archive.xml") { DataStream = new MemoryStream("<archive/>"u8.ToArray()) });
        write(tar);
        return path;
    }

    [Fact]
    public void Reader_refuses_pax_headers_that_opensim_would_read_as_files()
    {
        var path = RawTar(t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "assets/x.txt", new Dictionary<string, string> { ["comment"] = "hidden" })
        { DataStream = new MemoryStream([1]) }));
        Assert.Contains("OpenSim doesn't read the same way", Assert.Throws<InvalidDataException>(() => IarArchive.Inspect(path, 1 << 20, 10)).Message);
    }

    [Fact]
    public void Reader_refuses_inventory_entries_opensim_would_load_but_we_would_not_check()
    {
        var b = new IarBuilder();
        b.Raw(b.Folder("Notes") + "sneaky.txt", Encoding.UTF8.GetBytes("<InventoryItem/>"));
        Assert.Throws<InvalidDataException>(() => IarArchive.Inspect(b.Save(P("a.iar")), 1 << 20, 10));

        var odd = new IarBuilder();
        odd.Raw($"assets/{Guid.NewGuid()}.notmine", [1]);
        Assert.Throws<InvalidDataException>(() => IarArchive.Inspect(odd.Save(P("b.iar")), 1 << 20, 10));
    }

    [Fact]
    public void Reader_refuses_an_oversized_item_entry()
    {
        var b = new IarBuilder();
        b.Raw(b.Folder("Notes") + "big__" + Guid.NewGuid() + ".xml", new byte[2 << 20]);
        Assert.Contains("too big to read", Assert.Throws<InvalidDataException>(() => IarArchive.Inspect(b.Save(P("a.iar")), 1L << 30, 10)).Message);
    }

    [Fact]
    public void Reader_still_reads_what_opensim_and_the_launcher_write()
    {
        var b = new IarBuilder();
        b.Notecard(new string('f', 150), "n", "x");
        var source = b.Save(P("a.iar"));
        IarArchive.Rewrite(source, P("b.iar"), new HashSet<string>());
        Assert.Single(IarArchive.Inspect(P("b.iar"), 1 << 20, 10).Items);
    }

    [Fact]
    public void Folder_names_starting_with_a_dash_are_refused() => Assert.False(IarFolderNames.IsSafe("-m"));

    [Fact]
    public void One_download_stream_per_resident()
    {
        var gate = new IarDownloadGate();
        var ann = Guid.NewGuid();
        using (var first = gate.TryEnter(ann))
        {
            Assert.NotNull(first);
            Assert.Null(gate.TryEnter(ann));
            using var other = gate.TryEnter(Guid.NewGuid());
            Assert.NotNull(other);
        }
        using var again = gate.TryEnter(ann);
        Assert.NotNull(again);
    }
}
