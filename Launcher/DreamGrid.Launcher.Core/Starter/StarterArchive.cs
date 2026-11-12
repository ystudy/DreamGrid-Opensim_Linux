using System.Text;
using DreamGrid.Launcher.Core.Iar;

namespace DreamGrid.Launcher.Core.Starter;

/// <summary>An avatar found in an archive: its folder (names joined with '/'), its name and how many items it holds.</summary>
public sealed record StarterCandidate(string FolderPath, string Name, int Items);

/// <summary>Reading starter avatars out of an archive such as the owner's FULLAVATARS file (spec 2026-11-11-starter-avatars-design §6.2).</summary>
public static class StarterArchive
{
    private const int BodyPart = 13;
    public const string FallbackFolder = "Starter avatar";

    /// <summary>
    /// The folders exactly two levels down (<c>A/B</c>) that hold, in them or below, at least one body part (shape, skin, eyes,
    /// hair): each is offered as an avatar, in the archive's order.
    /// </summary>
    public static IReadOnlyList<StarterCandidate> Candidates(string iar)
    {
        var items = IarArchive.Inspect(iar, long.MaxValue, int.MaxValue).Items;
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var withBody = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var parts = item.FolderPath.Split('/');
            if (parts.Length < 2) continue;
            var key = parts[0] + "/" + parts[1];
            if (!counts.ContainsKey(key)) order.Add(key);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (item.AssetType == BodyPart) withBody.Add(key);
        }
        return [.. order.Where(withBody.Contains).Select(k => new StarterCandidate(k, k[(k.IndexOf('/') + 1)..], counts[k]))];
    }

    /// <summary>
    /// Writes <paramref name="target"/>: <paramref name="iar"/> with only <paramref name="folderPath"/>'s items, the folders on the way
    /// to it and under it, and the assets those items reach. The other avatars' folders are left out, so no empty folders are loaded.
    /// </summary>
    public static void OneAvatar(string iar, string folderPath, string target)
    {
        bool Inside(string path) => path == folderPath || path.StartsWith(folderPath + "/", StringComparison.Ordinal);
        bool OnTheWay(string path) => folderPath.StartsWith(path + "/", StringComparison.Ordinal);

        var items = IarArchive.Inspect(iar, long.MaxValue, int.MaxValue).Items;
        var dropItems = items.Where(i => !Inside(i.FolderPath)).Select(i => i.EntryPath).ToHashSet(StringComparer.Ordinal);
        var dropFolders = IarArchive.FolderEntries(iar)
            .Where(e => IarArchive.FolderPathOf(e) is var p && !Inside(p) && !OnTheWay(p))
            .ToHashSet(StringComparer.Ordinal);
        IarArchive.Rewrite(iar, target, dropItems, dropFolderEntries: dropFolders);
    }

    /// <summary>
    /// A folder name the IAR job accepts (<see cref="IarFolderNames.IsSafe"/>): the avatar's name without the characters it
    /// refuses, spaces collapsed, no leading '-', at most 64 characters; <see cref="FallbackFolder"/> when nothing is left.
    /// </summary>
    public static string SafeFolder(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '(' or ')' or '-') sb.Append(c);
        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('-').Trim();
        if (s.Length > 64) s = s[..64].Trim();
        return s.Length > 0 && IarFolderNames.IsSafe(s) ? s : FallbackFolder;
    }
}
