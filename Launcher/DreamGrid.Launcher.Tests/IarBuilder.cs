using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Makes inventory archives for tests the way OpenSim lays them out (archive.xml first, <c>inventory/&lt;name&gt;__&lt;id&gt;/</c>
/// folders, <c>&lt;name&gt;__&lt;id&gt;.xml</c> items, <c>assets/&lt;id&gt;_&lt;type&gt;.&lt;ext&gt;</c>), written with .NET's own tar writer so
/// the launcher's reader is checked against something it didn't write itself.
/// </summary>
internal sealed class IarBuilder
{
    public const uint Full = 0x8000 | 0x4000 | 0x2000;
    public const uint Copy = 0x8000, Modify = 0x4000, Transfer = 0x2000;

    private readonly List<(string Name, byte[] Data)> _entries = [("archive.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><archive major_version=\"0\" minor_version=\"3\" />"))];
    private readonly Dictionary<string, string> _folders = [];

    /// <summary>The folder entry path for a slash-separated folder path (made on first use, with its parents).</summary>
    public string Folder(string path)
    {
        if (_folders.TryGetValue(path, out var known)) return known;
        var parent = path.Contains('/') ? Folder(path[..path.LastIndexOf('/')]) : "inventory/";
        var entry = parent + path.Split('/')[^1] + "__" + Guid.NewGuid() + "/";
        _folders[path] = entry;
        _entries.Add((entry, []));
        return entry;
    }

    public Guid Item(string folder, string name, Guid asset, int assetType, int invType, uint perms = Full)
    {
        var id = Guid.NewGuid();
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-16\"?><InventoryItem><Name>{name}</Name><ID>{id}</ID><InvType>{invType}</InvType>" +
                  $"<CreatorUUID>{Guid.NewGuid()}</CreatorUUID><CreationDate>1700000000</CreationDate><Owner>{Guid.NewGuid()}</Owner>" +
                  $"<Description></Description><AssetType>{assetType}</AssetType><AssetID>{asset}</AssetID><SaleType>0</SaleType>" +
                  $"<SalePrice>0</SalePrice><BasePermissions>{perms}</BasePermissions><CurrentPermissions>{perms}</CurrentPermissions>" +
                  $"<EveryOnePermissions>0</EveryOnePermissions><NextPermissions>{perms}</NextPermissions><Flags>0</Flags>" +
                  $"<GroupID>{Guid.Empty}</GroupID><GroupOwned>False</GroupOwned></InventoryItem>";
        _entries.Add((Folder(folder) + name + "__" + id + ".xml", Encoding.UTF8.GetBytes(xml)));
        return id;
    }

    public Guid Texture(string folder, string name, uint perms = Full, Guid? asset = null)
    {
        var id = asset ?? Guid.NewGuid();
        Asset(id, "texture.jp2", [0xff, 0x4f, 0xff, 0x51, 1, 2, 3]);
        Item(folder, name, id, 0, 0, perms);
        return id;
    }

    public Guid Notecard(string folder, string name, string text, uint perms = Full)
    {
        var id = Guid.NewGuid();
        Asset(id, "notecard.txt", Encoding.UTF8.GetBytes(text));
        Item(folder, name, id, 7, 7, perms);
        return id;
    }

    /// <summary>An object item whose asset is <paramref name="xml"/> (a SceneObjectGroup or CoalescedObject).</summary>
    public Guid Object(string folder, string name, string xml, uint perms = Full)
    {
        var id = Guid.NewGuid();
        Asset(id, "object.xml", Encoding.UTF8.GetBytes(xml));
        Item(folder, name, id, 6, 6, perms);
        return id;
    }

    public void Asset(Guid id, string ext, byte[] data) => _entries.Add(($"assets/{id}_{ext}", data));

    public void Raw(string name, byte[] data) => _entries.Add((name, data));

    public string Save(string path)
    {
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Gnu);
        foreach (var (name, data) in _entries)
        {
            var dir = name.EndsWith('/');
            var e = new GnuTarEntry(dir ? TarEntryType.Directory : TarEntryType.RegularFile, name);
            if (!dir) e.DataStream = new MemoryStream(data);
            tar.WriteEntry(e);
        }
        return path;
    }

    /// <summary>A part for an object's XML: its owner mask and, inside, task inventory items with these permissions.</summary>
    public static string Part(uint ownerMask, params (string Name, uint Perms, Guid Asset)[] contents)
    {
        var items = string.Concat(contents.Select(c =>
            $"<TaskInventoryItem><AssetID><UUID>{c.Asset}</UUID></AssetID><Name>{c.Name}</Name><BasePermissions>{c.Perms}</BasePermissions>" +
            $"<CurrentPermissions>{c.Perms}</CurrentPermissions><NextPermissions>{c.Perms}</NextPermissions></TaskInventoryItem>"));
        return $"<SceneObjectPart><UUID><UUID>{Guid.NewGuid()}</UUID></UUID><Name>part</Name><BaseMask>{ownerMask}</BaseMask>" +
               $"<OwnerMask>{ownerMask}</OwnerMask><NextOwnerMask>{ownerMask}</NextOwnerMask><TaskInventory>{items}</TaskInventory>" +
               $"<Shape><TextureEntry></TextureEntry></Shape></SceneObjectPart>";
    }

    /// <summary>An object of a root part and child parts, as OpenSim's SceneObjectSerializer writes one.</summary>
    public static string Group(string root, params string[] children) =>
        $"<SceneObjectGroup><RootPart>{root}</RootPart><OtherParts>{string.Concat(children.Select(c => $"<Part>{c}</Part>"))}</OtherParts></SceneObjectGroup>";

    /// <summary>Several objects taken at once.</summary>
    public static string Coalesced(params string[] groups) =>
        $"<CoalescedObject x=\"0\" y=\"0\" z=\"0\">{string.Concat(groups.Select(g => $"<SceneObjectGroup offsetx=\"0\" offsety=\"0\" offsetz=\"0\">{g[18..^19]}</SceneObjectGroup>"))}</CoalescedObject>";
}
