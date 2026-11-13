using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GridGate.Launcher.Core.Iar;

/// <summary>Why an item stayed on the grid when a resident backed up their inventory.</summary>
public enum LeftOutReason { NoCopy, NoModify, NoTransfer, ContentsNotFullRights, PartsNotFullRights }

/// <summary>One item left out of a resident's backup: its name, its folder and why.</summary>
public sealed record LeftOutItem(string Name, string FolderPath, LeftOutReason Reason);

/// <summary>
/// The full-rights rule for residents' downloads (spec 2026-11-11-iar-backups-design §6.3): an item leaves the grid only when its
/// owner can copy, modify and transfer it. An object must also have every part (root, children, and every object of a coalesced
/// one) full rights for its owner, and everything in its contents full rights, objects in its contents checked the same way, as
/// deep as they go. An object whose data is missing or can't be read is left out: the rule fails closed.
/// </summary>
public static class IarPermissionFilter
{
    private const uint Copy = 0x8000, Modify = 0x4000, Transfer = 0x2000, Full = Copy | Modify | Transfer;
    private const int ObjectType = 6;

    /// <summary>Rewrites <paramref name="source"/> into <paramref name="target"/> without every item that isn't full rights.</summary>
    public static IReadOnlyList<LeftOutItem> Apply(string source, string target)
    {
        var contents = IarArchive.Inspect(source, long.MaxValue, int.MaxValue);
        var objectAssets = contents.AssetEntries
            .Where(a => a.Value.EndsWith("_object.xml", StringComparison.OrdinalIgnoreCase)).Select(a => a.Key).ToHashSet();
        var facts = new Dictionary<Guid, ObjectFacts?>();
        IarArchive.ReadAssets(source, objectAssets, (id, _, data) => facts[id] = Read(data));

        var left = new List<LeftOutItem>();
        var drop = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in contents.Items)
        {
            var reason = OwnReason(item.CurrentPermissions);
            if (reason is null && item.AssetType == ObjectType)
                reason = facts.GetValueOrDefault(item.AssetId) is { } f ? ObjectReason(f, facts, [item.AssetId]) : LeftOutReason.PartsNotFullRights;
            if (reason is { } r)
            {
                left.Add(new LeftOutItem(item.Name, item.FolderPath, r));
                drop.Add(item.EntryPath);
            }
        }
        IarArchive.Rewrite(source, target, drop);
        return left;
    }

    private static LeftOutReason? OwnReason(uint perms) =>
        (perms & Copy) == 0 ? LeftOutReason.NoCopy
        : (perms & Modify) == 0 ? LeftOutReason.NoModify
        : (perms & Transfer) == 0 ? LeftOutReason.NoTransfer
        : null;

    private static LeftOutReason? ObjectReason(ObjectFacts f, Dictionary<Guid, ObjectFacts?> all, HashSet<Guid> visiting)
    {
        if (!f.PartsFull) return LeftOutReason.PartsNotFullRights;
        if (!f.ContentsFull) return LeftOutReason.ContentsNotFullRights;
        foreach (var inner in f.ObjectsInside)
        {
            if (!visiting.Add(inner)) continue;
            // An object in the contents that isn't in the archive brings nothing with it; one that is must pass too.
            if (!all.TryGetValue(inner, out var innerFacts)) continue;
            if (innerFacts is null || ObjectReason(innerFacts, all, visiting) is not null) return LeftOutReason.ContentsNotFullRights;
        }
        return null;
    }

    private sealed record ObjectFacts(bool PartsFull, bool ContentsFull, IReadOnlyList<Guid> ObjectsInside);

    /// <summary>The object's facts, or null when its data isn't an object OpenSim wrote.</summary>
    private static ObjectFacts? Read(byte[] data)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(data).TrimStart('﻿')), settings);
            var root = XElement.Load(reader);
            var parts = root.DescendantsAndSelf("SceneObjectPart").ToList();
            if (parts.Count == 0) return null;
            var partsFull = parts.All(p => FullIn(p.Element("OwnerMask")));
            var items = parts.SelectMany(p => p.Elements("TaskInventory").Elements("TaskInventoryItem")).ToList();
            var contentsFull = items.All(i => FullIn(i.Element("CurrentPermissions")));
            var inside = items
                .Where(i => (string?)i.Element("Type") is "6" or null || (string?)i.Element("InvType") is "6")
                .Select(i => Guid.TryParse(i.Element("AssetID")?.Value.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty).ToList();
            return new ObjectFacts(partsFull, contentsFull, inside);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static bool FullIn(XElement? e) => e is not null && uint.TryParse(e.Value.Trim(), out var v) && (v & Full) == Full;
}
