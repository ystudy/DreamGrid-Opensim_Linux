using System.Security.Cryptography;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>A taken-down item's asset: its id, FSAssets' hash of its data (null when the grid no longer has it) and its case.</summary>
public sealed record BannedAsset(Guid AssetId, string? Hash, string CaseNumber);

/// <summary>An item in an upload that matches taken-down content, the case it matches, and its entry in the archive.</summary>
public sealed record BannedMatch(string ItemName, string CaseNumber, string EntryPath);

/// <summary>The takedown records couldn't be read, so an upload can't be checked against them; it is refused.</summary>
public sealed class TakedownsUnreadableException() : InvalidOperationException("The takedown records can't be read.");

/// <summary>
/// Finds and removes taken-down content in an uploaded archive (spec 2026-11-11-iar-backups-design §6.4). An asset is banned by
/// id, or by the hash of its data in the archive. An item matches when its own asset is banned, or when anything it reaches
/// (a face texture in an object's binary fields, an object in its contents, an id in a notecard) is. Cleaning also drops every
/// banned asset entry, even one no item points at, since OpenSim loads every asset in an archive.
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
        var byId = ById(banned);
        var (bannedHere, names) = Scan(iar, banned);
        var contents = IarArchive.Inspect(iar, long.MaxValue, int.MaxValue);
        var matches = new List<BannedMatch>();
        foreach (var item in contents.Items)
            if (Reaches(item.AssetId) is { } caseNumber) matches.Add(new BannedMatch(item.Name, caseNumber, item.EntryPath));
        return matches;

        string? Reaches(Guid start)
        {
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

    /// <summary>
    /// Rewrites <paramref name="source"/> into <paramref name="target"/> without the matched items and without every asset entry
    /// that is banned by id or by hash. The target is always a fresh file written the way OpenSim reads archives: an upload is never
    /// loaded as it came. Returns how many items and asset entries were left out.
    /// </summary>
    public static int Clean(string source, string target, IReadOnlyList<BannedMatch> matches, IReadOnlyList<BannedAsset> banned)
    {
        var drop = banned.Count == 0 ? [] : Scan(source, banned).BannedHere.Keys.ToHashSet();
        IarArchive.Rewrite(source, target, matches.Select(m => m.EntryPath).ToHashSet(StringComparer.Ordinal), drop);
        return matches.Count + drop.Count;
    }

    private static Dictionary<Guid, string> ById(IReadOnlyList<BannedAsset> banned) =>
        banned.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => g.First().CaseNumber);

    /// <summary>Which asset entries are banned (by id, or by hashing their data as a stream), and what each linking asset names.</summary>
    private static (Dictionary<Guid, string> BannedHere, Dictionary<Guid, HashSet<Guid>> Names) Scan(string iar, IReadOnlyList<BannedAsset> banned)
    {
        var byId = ById(banned);
        var byHash = banned.Where(b => b.Hash is not null).GroupBy(b => b.Hash!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().CaseNumber);
        var known = IarArchive.AssetIds(iar);
        known.UnionWith(byId.Keys);
        var bannedHere = new Dictionary<Guid, string>();
        var names = new Dictionary<Guid, HashSet<Guid>>();
        using var tar = new IarTarReader(iar);
        while (tar.Next() is { } e)
        {
            if (IarArchive.KindOf(e) != IarArchive.EntryKind.Asset) continue;
            var id = IarArchive.AssetIdOf(e.Name)!.Value;
            string hash;
            if (IarArchive.IsLinking(e.Name))
            {
                var data = tar.ReadData(IarArchive.MaxLinkingBytes);
                hash = Convert.ToHexStringLower(SHA256.HashData(data));
                names[id] = IarArchive.References(e.Name, data, known);
            }
            else
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                tar.CopyData(new HashingStream(sha));
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (byId.TryGetValue(id, out var n) || byHash.TryGetValue(hash, out n)) bannedHere[id] = n;
        }
        return (bannedHere, names);
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    }
}
