using System.Security.Cryptography;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>A taken-down item's asset: its id, FSAssets' hash of its data (null when the grid no longer has it) and its case.</summary>
public sealed record BannedAsset(Guid AssetId, string? Hash, string CaseNumber);

/// <summary>An item in an upload that matches taken-down content, the case it matches, and its entry in the archive.</summary>
public sealed record BannedMatch(string ItemName, string CaseNumber, string EntryPath);

/// <summary>The takedown records couldn't be read, so an upload can't be checked against them; it is refused.</summary>
public sealed class TakedownsUnreadableException() : InvalidOperationException("The takedown records can't be read.");

/// <summary>
/// Finds and removes taken-down content in an uploaded archive (spec 2026-11-11-iar-backups-design §6.4). An item matches when its
/// own asset is banned, by id or by the hash of its data in the archive, or when anything it reaches through a linking asset (a
/// texture on an object, an object in its contents) is banned.
/// </summary>
public static class IarBannedFilter
{
    /// <summary>
    /// The items in <paramref name="iar"/> that match <paramref name="banned"/>. Throws <see cref="TakedownsUnreadableException"/>
    /// when <paramref name="unreadable"/> is true: the check fails closed.
    /// </summary>
    public static IReadOnlyList<BannedMatch> Find(string iar, IReadOnlyList<BannedAsset> banned, bool unreadable)
    {
        if (unreadable) throw new TakedownsUnreadableException();
        if (banned.Count == 0) return [];
        var byId = banned.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => g.First().CaseNumber);
        var byHash = banned.Where(b => b.Hash is not null).GroupBy(b => b.Hash!).ToDictionary(g => g.Key, g => g.First().CaseNumber);

        var contents = IarArchive.Inspect(iar, long.MaxValue, int.MaxValue);
        // Which asset in the archive is banned, and what each linking asset names.
        var bannedHere = new Dictionary<Guid, string>();
        var names = new Dictionary<Guid, HashSet<Guid>>();
        IarArchive.ReadAssets(iar, contents.AssetEntries.Keys.ToHashSet(), (id, entry, data) =>
        {
            if (byId.TryGetValue(id, out var n) || byHash.TryGetValue(Convert.ToHexStringLower(SHA256.HashData(data)), out n))
                bannedHere[id] = n;
            if (IarArchive.IsLinking(entry)) names[id] = IarArchive.IdsIn(data);
        });

        var matches = new List<BannedMatch>();
        foreach (var item in contents.Items)
        {
            if (Reaches(item.AssetId) is { } caseNumber) matches.Add(new BannedMatch(item.Name, caseNumber, item.EntryPath));
        }
        return matches;

        string? Reaches(Guid start)
        {
            if (byId.TryGetValue(start, out var direct)) return direct;
            var seen = new HashSet<Guid>();
            var queue = new Queue<Guid>([start]);
            while (queue.TryDequeue(out var id))
            {
                if (!seen.Add(id)) continue;
                if (bannedHere.TryGetValue(id, out var n) || byId.TryGetValue(id, out n)) return n;
                if (names.TryGetValue(id, out var more))
                    foreach (var next in more) queue.Enqueue(next);
            }
            return null;
        }
    }

    /// <summary>Rewrites <paramref name="source"/> into <paramref name="target"/> without the matched items (and their assets).</summary>
    public static void Remove(string source, string target, IReadOnlyList<BannedMatch> matches)
    {
        IarArchive.Rewrite(source, target, matches.Select(m => m.EntryPath).ToHashSet(StringComparer.Ordinal));
    }
}
