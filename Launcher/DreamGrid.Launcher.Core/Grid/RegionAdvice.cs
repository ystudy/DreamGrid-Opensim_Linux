using System.Globalization;
using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// The add-region form's advice for a region run on its own. <see cref="MoreRegions"/> is how many more of this
/// size the free memory holds (null when free memory is unknown); <see cref="Short"/> is true below 1.5 times the estimate.
/// </summary>
public sealed record OwnAdvice(long EstimateBytes, long? FreeBytes, int? MoreRegions, bool Short, string Text);

/// <summary>Plain-language advice for the add-region form: memory, size, sharing and placement. "MB" and "GB" are 2^20 and 2^30 bytes.</summary>
public static class RegionAdvice
{
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;
    private const long BaseMb = 400;
    private const long PerExtraSquareMb = 150;

    /// <summary>
    /// 400 MB for a 1×1 plus 150 MB per extra square. Once a running program reports its memory, the mean of those
    /// scaled the same way instead.
    /// </summary>
    public static OwnAdvice OnItsOwn(GridStatus status, long? freeBytes, int size)
    {
        var squares = (long)size * size;
        var weightMb = BaseMb + PerExtraSquareMb * (squares - 1);
        var measured = status.Programs
            .Where(p => p.Process.State == ProcessState.Running && p.Process.MemoryBytes is > 0)
            .Select(p => p.Process.MemoryBytes!.Value)
            .ToList();
        var estimate = measured.Count > 0
            ? (long)(measured.Average() * weightMb / BaseMb)
            : weightMb * MB;

        int? more = freeBytes is { } free ? (int)Math.Min(int.MaxValue, free / estimate) : null;
        var isShort = freeBytes is { } f && f < 1.5 * estimate;

        var text = $"Uses about {Size(estimate)} more memory.";
        if (freeBytes is { } have)
            text += isShort
                ? $" This computer has {Size(have)} free. This computer may not have enough free memory for another region run on its own. Sharing saves memory."
                : $" This computer has {Size(have)} free, enough for about {more} more {(more == 1 ? "region" : "regions")} run this way.";
        if (!isShort) text += " If it crashes, only this region goes offline.";
        return new OwnAdvice(estimate, freeBytes, more, isShort, text);
    }

    public static string Share(IReadOnlyList<string> groupRegions, string newName) =>
        $"Saves memory, but these regions start, stop, restart and crash together. Restarting {newName} also restarts {Join(groupRegions)}, and drops everyone in them.";

    /// <summary>A warning for large regions; null for a 1×1.</summary>
    public static string? SizeRisk(int size) => size switch
    {
        <= 1 => null,
        2 => "A 2×2 region is 4 squares of land. It uses more memory than a 1×1.",
        3 => $"A 3×3 region is 9 squares of land. It uses about {TimesA1x1(3)} times the memory of a 1×1 and can feel slower to visitors.",
        4 => "A 4×4 region is 16 squares of land in one region. It uses several times the memory of a 1×1 and can feel slower to visitors. Many viewers handle very large regions poorly.",
        _ => $"A {size}×{size} region is {size * size} squares of land in one region. It uses about {TimesA1x1(size)} times the memory of a 1×1, needs a powerful computer, and can feel slow to visitors. Many viewers handle very large regions poorly.",
    };

    /// <summary>The size's memory estimate as a whole number of 1×1 regions (rounded down).</summary>
    private static long TimesA1x1(int size) => (BaseMb + PerExtraSquareMb * ((long)size * size - 1)) / BaseMb;

    public static string Placement(PlacementCheck check)
    {
        if (!check.Ok) return $"That spot is taken by {check.OverlapsWith}.";
        if (check.Neighbours.Count == 0) return "This will be a separate island; visitors reach it by teleport.";
        return "This region will join " + string.Join(" and ", check.Neighbours.Select(n => $"{n.Name} on its {n.Side} side")) + ".";
    }

    /// <summary>"Home" for one region, "Home, Beach (2 regions)" for several.</summary>
    public static string GroupLabel(IReadOnlyList<string> regionNames) =>
        regionNames.Count == 1 ? regionNames[0] : $"{string.Join(", ", regionNames)} ({regionNames.Count} regions)";

    /// <summary>"Home", "Home and Beach", "Home, Beach and Forest".</summary>
    public static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>A file's size: whole KB under 1 MB, MB with one decimal under 1 GB, else GB with one decimal (1024-based).</summary>
    public static string FileSize(long bytes) =>
        bytes >= GB ? $"{(bytes / (double)GB).ToString("0.0", CultureInfo.InvariantCulture)} GB"
        : bytes >= MB ? $"{(bytes / (double)MB).ToString("0.0", CultureInfo.InvariantCulture)} MB"
        : $"{Math.Round(bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture)} KB";

    /// <summary>GB with one decimal from 1 GB up, else MB rounded to 10 ("MB" and "GB" are 2^20 and 2^30 bytes).</summary>
    public static string Size(long bytes) => bytes >= GB
        ? $"{(bytes / (double)GB).ToString("0.0", CultureInfo.InvariantCulture)} GB"
        : $"{(long)Math.Round(bytes / (double)MB / 10) * 10} MB";
}
