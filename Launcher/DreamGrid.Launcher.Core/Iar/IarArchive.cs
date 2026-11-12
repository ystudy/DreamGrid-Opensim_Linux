using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
/// Reads and rewrites inventory archives (spec 2026-11-11-iar-backups-design §6) without OpenSim code. A rewrite leaves out the
/// given item entries and keeps an asset only while it is still reachable from a kept item, so a left-out item's asset (and a
/// texture only a left-out object used) never stays in the file.
/// </summary>
public static partial class IarArchive
{
    private const string Inventory = "inventory/";
    private const string Assets = "assets/";

    /// <summary>
    /// The asset kinds whose data can name other assets (objects, notecards, wearables, gestures, scripts, materials, settings).
    /// Their data is searched for asset ids; other kinds (textures, sounds, meshes, animations) name none.
    /// </summary>
    private static readonly string[] Linking =
        ["_object.xml", "_notecard.txt", "_clothing.txt", "_bodypart.txt", "_gesture.txt", "_script.lsl", "_material.xml", "_settings.bin", "_prmat.dat"];

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
            if (IsFolder(e)) folders++;
            else if (IsItem(e))
            {
                if (items.Count >= maxItems) throw new IarTooBigException("items", tar.Read);
                items.Add(ParseItem(e.Name, tar.ReadData()));
            }
            else if (AssetIdOf(e.Name) is { } id) assets[id] = e.Name;
        }
        return new IarContents(items, folders, assets, tar.Read);
    }

    /// <summary>Hands each wanted asset's data to <paramref name="found"/>, in one pass over the archive.</summary>
    public static void ReadAssets(string path, IReadOnlySet<Guid> wanted, Action<Guid, string, byte[]> found)
    {
        using var tar = new IarTarReader(path);
        while (tar.Next() is { } e)
            if (!IsFolder(e) && AssetIdOf(e.Name) is { } id && wanted.Contains(id))
                found(id, e.Name, tar.ReadData());
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="target"/> without the item entries in <paramref name="dropItemEntries"/>,
    /// and without every asset no kept item reaches (directly, or through the ids inside a kept object, notecard or other linking
    /// asset, however deep). <c>archive.xml</c>, folders and every other entry are copied unchanged, in order.
    /// </summary>
    public static void Rewrite(string source, string target, ISet<string> dropItemEntries)
    {
        // Pass 1: what each kept item points at, and what each linking asset names.
        var roots = new HashSet<Guid>();
        var names = new Dictionary<Guid, HashSet<Guid>>();
        using (var tar = new IarTarReader(source))
        {
            while (tar.Next() is { } e)
            {
                if (IsItem(e))
                {
                    var data = tar.ReadData();
                    if (!dropItemEntries.Contains(e.Name)) roots.Add(ParseItem(e.Name, data).AssetId);
                }
                else if (!IsFolder(e) && AssetIdOf(e.Name) is { } id && Linking.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    names[id] = IdsIn(tar.ReadData());
            }
        }

        var reached = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.TryDequeue(out var id))
        {
            if (!reached.Add(id)) continue;
            if (names.TryGetValue(id, out var more))
                foreach (var next in more) queue.Enqueue(next);
        }

        // Pass 2: copy what stays.
        var temp = target + ".part";
        try
        {
            using (var tar = new IarTarReader(source))
            using (var writer = new IarTarWriter(temp))
            {
                while (tar.Next() is { } e)
                {
                    if (IsItem(e) && dropItemEntries.Contains(e.Name)) continue;
                    if (!IsFolder(e) && AssetIdOf(e.Name) is { } id && !reached.Contains(id)) continue;
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

    /// <summary>Every asset id written in <paramref name="data"/>, in either case.</summary>
    internal static HashSet<Guid> IdsIn(byte[] data)
    {
        var ids = new HashSet<Guid>();
        foreach (Match m in UuidPattern().Matches(Encoding.Latin1.GetString(data)))
            ids.Add(Guid.Parse(m.Value));
        return ids;
    }

    /// <summary>Whether an asset entry's data can name other assets (an object, notecard, wearable and so on).</summary>
    internal static bool IsLinking(string entry) => Linking.Any(x => entry.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    internal static bool IsFolder(IarTarEntry e) => e.IsDirectory;

    internal static bool IsItem(IarTarEntry e) =>
        !e.IsDirectory && e.Name.StartsWith(Inventory, StringComparison.Ordinal) && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

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
            using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(data).TrimStart('﻿')), settings);
            var root = System.Xml.Linq.XElement.Load(reader);
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
