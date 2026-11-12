using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>
/// One inventory item in an archive (an <c>inventory/**.xml</c> entry, OpenSim's InventoryItemBase XML). <see cref="FolderPath"/>
/// is the folder names from the entry path without their <c>__&lt;id&gt;</c> suffixes, joined with '/'.
/// </summary>
public sealed record IarItem(string EntryPath, Guid ItemId, string Name, string FolderPath, Guid AssetId, int AssetType, int InvType,
    uint CurrentPermissions);

/// <summary>What an archive holds: its items, how many folders, each asset's entry path, and the bytes unpacked to read it.</summary>
public sealed record IarContents(IReadOnlyList<IarItem> Items, int Folders, IReadOnlyDictionary<Guid, string> AssetEntries, long UnpackedBytes);

/// <summary>An archive passed a limit while it was read: <see cref="Limit"/> is <c>"unpacked"</c> or <c>"items"</c>.</summary>
public sealed class IarTooBigException(string limit, long readBytes)
    : IOException($"The archive is over its {limit} limit.")
{
    public string Limit { get; } = limit;
    public long ReadBytes { get; } = readBytes;
}

/// <summary>
/// Reads and rewrites inventory archives (spec 2026-11-11-iar-backups-design §6) without OpenSim code. Every read is strict: an
/// entry OpenSim would read differently, or load without this code having seen it, makes the archive unreadable
/// (<see cref="InvalidDataException"/>). A rewrite leaves out the given item and asset entries and keeps an asset only while it is
/// still reachable from a kept item, so a left-out item's asset (and a texture only a left-out object used) never stays in the file.
/// </summary>
public static partial class IarArchive
{
    private const string Inventory = "inventory/";
    private const string Assets = "assets/";

    /// <summary>The largest item entry read (OpenSim's are a few kilobytes).</summary>
    public const long MaxItemBytes = 1L << 20;

    /// <summary>The largest linking asset (object, notecard, wearable…) read into memory to find what it names.</summary>
    public const long MaxLinkingBytes = 128L << 20;

    /// <summary>
    /// The asset kinds whose data can name other assets (objects, notecards, wearables, gestures, scripts, materials, settings).
    /// Their data is searched for asset ids; other kinds (textures, sounds, meshes, animations) name none.
    /// </summary>
    private static readonly string[] Linking =
        ["_object.xml", "_notecard.txt", "_clothing.txt", "_bodypart.txt", "_gesture.txt", "_script.lsl", "_material.xml", "_settings.bin", "_prmat.dat"];

    /// <summary>The extensions OpenSim writes for assets (ArchiveConstants).</summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.Ordinal)
    {
        "animation.bvh", "bodypart.txt", "callingcard.txt", "clothing.txt", "folder.txt", "gesture.txt", "image.jpg", "image.tga", "landmark.txt",
        "bytecode.lso", "script.lsl", "mesh.llmesh", "notecard.txt", "object.xml", "simstate.bin", "sound.ogg", "sound.wav", "texture.jp2",
        "texture.tga", "material.xml", "settings.bin", "prmat.dat",
    };

    /// <summary>The object fields OpenSim writes as base64 binary that can hold asset ids (faces, particles, extra params, materials).</summary>
    private static readonly string[] Base64Fields = ["TextureEntry", "ParticleSystem", "ExtraParams", "MatOvrd"];

    /// <summary>A gzip tar whose first entry is <c>archive.xml</c>; false for anything else, a missing file included.</summary>
    public static bool LooksLikeIar(string path)
    {
        try
        {
            using var tar = new IarTarReader(path, 1 << 20);
            return tar.Next()?.Name == "archive.xml";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The archive's items, folders and assets. Throws <see cref="IarTooBigException"/> as soon as a limit is passed.</summary>
    public static IarContents Inspect(string path, long maxUnpackedBytes, int maxItems)
    {
        var items = new List<IarItem>();
        var assets = new Dictionary<Guid, string>();
        var folders = 0;
        using var tar = new IarTarReader(path, maxUnpackedBytes);
        while (tar.Next() is { } e)
        {
            switch (KindOf(e))
            {
                case EntryKind.Folder: folders++; break;
                case EntryKind.Item:
                    if (items.Count >= maxItems) throw new IarTooBigException("items", tar.Read);
                    items.Add(ParseItem(e.Name, tar.ReadData(MaxItemBytes)));
                    break;
                case EntryKind.Asset: assets[AssetIdOf(e.Name)!.Value] = e.Name; break;
            }
        }
        return new IarContents(items, folders, assets, tar.Read);
    }

    /// <summary>Hands each wanted asset's data to <paramref name="found"/>, in one pass over the archive (linking assets only).</summary>
    public static void ReadAssets(string path, IReadOnlySet<Guid> wanted, Action<Guid, string, byte[]> found)
    {
        using var tar = new IarTarReader(path);
        while (tar.Next() is { } e)
            if (KindOf(e) == EntryKind.Asset && AssetIdOf(e.Name) is { } id && wanted.Contains(id))
                found(id, e.Name, tar.ReadData(MaxLinkingBytes));
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="target"/> without the item entries in <paramref name="dropItemEntries"/>,
    /// without the assets in <paramref name="dropAssets"/>, and without every asset no kept item reaches (directly, or through the
    /// ids inside a kept object, notecard or other linking asset, however deep). <c>archive.xml</c> and folders are copied unchanged,
    /// in order, except the folder entries in <paramref name="dropFolderEntries"/>; anything else is left out. The target is
    /// written the way OpenSim writes archives.
    /// </summary>
    public static void Rewrite(string source, string target, ISet<string> dropItemEntries, ISet<Guid>? dropAssets = null,
        ISet<string>? dropFolderEntries = null)
    {
        var known = AssetIds(source);
        var roots = new HashSet<Guid>();
        var names = new Dictionary<Guid, HashSet<Guid>>();
        using (var tar = new IarTarReader(source))
        {
            while (tar.Next() is { } e)
            {
                var kind = KindOf(e);
                if (kind == EntryKind.Item)
                {
                    var data = tar.ReadData(MaxItemBytes);
                    if (!dropItemEntries.Contains(e.Name)) roots.Add(ParseItem(e.Name, data).AssetId);
                }
                else if (kind == EntryKind.Asset && IsLinking(e.Name))
                    names[AssetIdOf(e.Name)!.Value] = References(e.Name, tar.ReadData(MaxLinkingBytes), known);
            }
        }

        var reached = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.TryDequeue(out var id))
        {
            if (dropAssets?.Contains(id) == true || !reached.Add(id)) continue;
            if (names.TryGetValue(id, out var more))
                foreach (var next in more) queue.Enqueue(next);
        }

        var temp = target + ".part";
        try
        {
            using (var tar = new IarTarReader(source))
            using (var writer = new IarTarWriter(temp))
            {
                while (tar.Next() is { } e)
                {
                    var kind = KindOf(e);
                    if (kind == EntryKind.Item && dropItemEntries.Contains(e.Name)) continue;
                    if (kind == EntryKind.Folder && dropFolderEntries?.Contains(e.Name) == true) continue;
                    if (kind == EntryKind.Asset && !reached.Contains(AssetIdOf(e.Name)!.Value)) continue;
                    if (kind == EntryKind.Other) continue;
                    writer.Write(e.Name, e.Type, e.Size, tar.CopyData);
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Every folder entry's path in the archive, in order (headers only).</summary>
    public static IReadOnlyList<string> FolderEntries(string path)
    {
        var folders = new List<string>();
        using var tar = new IarTarReader(path);
        while (tar.Next() is { } e)
            if (KindOf(e) == EntryKind.Folder) folders.Add(e.Name);
        return folders;
    }

    /// <summary>Every asset entry's id in the archive (headers only).</summary>
    internal static HashSet<Guid> AssetIds(string path)
    {
        var ids = new HashSet<Guid>();
        using var tar = new IarTarReader(path);
        while (tar.Next() is { } e)
            if (KindOf(e) == EntryKind.Asset) ids.Add(AssetIdOf(e.Name)!.Value);
        return ids;
    }

    /// <summary>
    /// The asset ids a linking asset names: every id written in its text, and, for an object, every id among
    /// <paramref name="known"/> inside its base64 binary fields (OpenSim writes face textures, particles, extra params and material
    /// overrides that way, with ids in OpenMetaverse's byte order).
    /// </summary>
    internal static HashSet<Guid> References(string entry, byte[] data, IReadOnlySet<Guid> known)
    {
        var ids = new HashSet<Guid>();
        var text = Encoding.Latin1.GetString(data);
        foreach (Match m in UuidPattern().Matches(text))
            ids.Add(Guid.Parse(m.Value));
        if (entry.EndsWith("_object.xml", StringComparison.OrdinalIgnoreCase) && known.Count > 0)
        {
            foreach (var field in Base64Fields)
            {
                foreach (Match m in Regex.Matches(text, $"<{field}>([A-Za-z0-9+/=\\s]+)</{field}>"))
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(m.Groups[1].Value); }
                    catch (FormatException) { continue; }
                    for (var i = 0; i + 16 <= bytes.Length; i++)
                    {
                        var g = new Guid(bytes.AsSpan(i, 16), bigEndian: true);
                        if (known.Contains(g)) ids.Add(g);
                    }
                }
            }
        }
        return ids;
    }

    /// <summary>Every asset id written in <paramref name="data"/> as text, in either case.</summary>
    internal static HashSet<Guid> IdsIn(byte[] data) => References("", data, new HashSet<Guid>());

    /// <summary>Whether an asset entry's data can name other assets (an object, notecard, wearable and so on).</summary>
    internal static bool IsLinking(string entry) => Linking.Any(x => entry.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    internal enum EntryKind { Archive, Folder, Item, Asset, Other }

    /// <summary>
    /// What an entry is, refusing what OpenSim would load without it being checked here: under <c>inventory/</c> only folders
    /// (type 5, ending in '/') and <c>.xml</c> item files; under <c>assets/</c> only OpenSim's <c>&lt;id&gt;_&lt;type&gt;.&lt;ext&gt;</c>.
    /// </summary>
    internal static EntryKind KindOf(IarTarEntry e)
    {
        var dir = e.Type == '5';
        if (dir != e.Name.EndsWith('/')) throw new InvalidDataException($"{e.Name} is a folder and a file at once.");
        if (e.Name.StartsWith(Inventory, StringComparison.Ordinal))
        {
            if (dir) return EntryKind.Folder;
            if (e.Name.EndsWith(".xml", StringComparison.Ordinal)) return EntryKind.Item;
            throw new InvalidDataException($"{e.Name} isn't an inventory item.");
        }
        if (e.Name.StartsWith(Assets, StringComparison.Ordinal))
        {
            if (dir && e.Name == Assets) return EntryKind.Other;
            if (dir || AssetIdOf(e.Name) is null || e.Name.Length < Assets.Length + 38 || e.Name[Assets.Length + 36] != '_'
                || !AssetExtensions.Contains(e.Name[(Assets.Length + 37)..]))
                throw new InvalidDataException($"{e.Name} isn't an asset as OpenSim writes them.");
            return EntryKind.Asset;
        }
        return e.Name == "archive.xml" ? EntryKind.Archive : EntryKind.Other;
    }

    internal static bool IsFolder(IarTarEntry e) => KindOf(e) == EntryKind.Folder;

    internal static bool IsItem(IarTarEntry e) => KindOf(e) == EntryKind.Item;

    /// <summary>The id at the start of an <c>assets/&lt;id&gt;_&lt;type&gt;.&lt;ext&gt;</c> entry name, or null for any other entry.</summary>
    internal static Guid? AssetIdOf(string name) =>
        name.StartsWith(Assets, StringComparison.Ordinal) && name.Length >= Assets.Length + 36
        && Guid.TryParseExact(name.AsSpan(Assets.Length, 36), "D", out var id) ? id : null;

    internal static IarItem ParseItem(string entry, byte[] data)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            // OpenSim writes "encoding=utf-16" in the declaration of UTF-8 bytes; read the text, not the declaration.
            using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(data).TrimStart('\uFEFF')), settings);
            var root = XElement.Load(reader);
            if (root.Name.LocalName != "InventoryItem") throw new InvalidDataException($"{entry} is not an inventory item.");
            foreach (var child in root.Elements())
                values.TryAdd(child.Name.LocalName, child.Value.Trim());
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"{entry} can't be read: {e.Message}");
        }
        return new IarItem(entry, GuidOf("ID"), values.GetValueOrDefault("Name", ""), FolderPathOf(entry), GuidOf("AssetID"),
            IntOf("AssetType"), IntOf("InvType"), (uint)LongOf("CurrentPermissions"));

        Guid GuidOf(string key) => Guid.TryParse(values.GetValueOrDefault(key), out var g) ? g : Guid.Empty;
        int IntOf(string key) => int.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        long LongOf(string key) => long.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;
    }

    /// <summary><c>inventory/Clothes__&lt;id&gt;/Shoes__&lt;id&gt;/boot__&lt;id&gt;.xml</c> → <c>Clothes/Shoes</c>.</summary>
    internal static string FolderPathOf(string entry)
    {
        var parts = entry[Inventory.Length..].Split('/');
        return string.Join('/', parts[..^1].Select(p => p.LastIndexOf("__", StringComparison.Ordinal) is var at and >= 0 ? p[..at] : p));
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidPattern();
}
