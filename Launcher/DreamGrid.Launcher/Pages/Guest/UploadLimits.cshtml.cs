using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.ModelImport;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>One limit as the Limits page lists it.</summary>
public sealed record LimitLine(string Name, string Value, string Why, string Tip);

/// <summary>
/// Every limit an uploaded model is checked against, with this grid's value (<see cref="ImportLimitsRules.For"/>), why it exists and
/// a Blender tip. On a Grid Asset Server the values are this computer's own regions only, and the page says so.
/// </summary>
[UploadPagesOnly]
public sealed class UploadLimitsModel(Func<LauncherSettings> settings) : PageModel
{
    /// <summary>The converter's own fixed limits (Task 2's face splitter).</summary>
    private const int FacesPerPart = 8;
    private const int PointsPerFace = 65_535;

    public IReadOnlyList<LimitLine> Limits { get; private set; } = [];
    public bool JoinedRegionsUnchecked { get; private set; }

    public void OnGet()
    {
        var s = settings();
        var l = ImportLimitsRules.For(s);
        JoinedRegionsUnchecked = s.Plan == OwnerPlan.GridAsset;
        Limits =
        [
            new("Largest part", Metres(l.MaxPartSize) + " on a side",
                "Regions refuse parts larger than this.",
                "In Blender, check the size in the N panel; scale down (S) and apply the scale (Ctrl+A)."),
            new("Largest physical part", Metres(l.MaxPhysicalPartSize) + " on a side",
                "Parts that move with physics must be smaller, so the region can work out collisions.",
                "In Blender, split a large model into smaller objects if it should be physical."),
            new("Smallest part", Metres(l.MinPartSize) + " on a side",
                "Smaller parts can't be made, so they are left out.",
                "In Blender, join tiny pieces to a larger part (Ctrl+J)."),
            new("Parts in one item", l.MaxLinksetParts == 0 ? "no limit" : Whole(l.MaxLinksetParts) + " parts",
                "An item is a set of linked parts; regions can limit how many.",
                "In Blender, join objects (Ctrl+J) to use fewer parts."),
            new("Materials in one part", Whole(FacesPerPart) + " faces",
                "Each part has at most 8 faces, one material each. More materials are split into extra linked parts for you.",
                "In Blender, join materials that share a picture, or bake them into one."),
            new("Points in one face", Whole(PointsPerFace),
                "Larger faces are split for you.",
                "In Blender, a Decimate modifier lowers the number of points."),
            new("Picture size", Whole(l.MaxTextureSide) + " px on a side",
                "Pictures are resized to a power of two no larger than this, to keep the grid quick to load.",
                "In Blender, save pictures at 1024 × 1024 or smaller."),
            new("Triangles before it gets heavy", Thousands(l.HeavyTriangles),
                "Above this, the simpler versions shown from far away may look lumpy or have holes.",
                "In Blender, add a Decimate modifier."),
            new("Triangles in one upload", Thousands(l.MaxTriangles),
                "The most this grid can take in one item.",
                "In Blender, add a Decimate modifier."),
            new("Objects in one file", Whole(l.MaxNodes),
                "The most this grid can take in one file.",
                "In Blender, join objects (Ctrl+J)."),
            new("File size", Whole(l.MaxUploadBytes / (1024 * 1024)) + " MB",
                "The largest file this grid takes.",
                "In Blender, use smaller pictures; export as glTF Binary (.glb)."),
            new("Picture inside the file", Whole(l.MaxImageSide) + " px on a side",
                "Larger pictures aren't read at all.",
                "In Blender, scale pictures down before exporting."),
        ];
    }

    private static string Whole(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Thousands(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string Metres(double m) => m.ToString("0.###", CultureInfo.InvariantCulture) + " m";
}
