using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.ModelImport;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>One limit as the Limits page lists it.</summary>
public sealed record LimitLine(string Name, string Value, string Why, string Tip);

/// <summary>
/// Every limit an uploaded model is checked against, with this grid's value (<see cref="ImportLimitsRules.For"/>), why it exists and
/// a Blender tip. On a Grid Asset Server the values are this computer's own regions only, and the page says so.
/// </summary>
[UploadPagesOnly]
public sealed class UploadLimitsModel(Func<LauncherSettings> settings, IStringLocalizer<ResidentText> t) : PageModel
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
            Line("LargestPart", t["UploadLimits.MetresSide", Metres(l.MaxPartSize)]),
            Line("LargestPhysical", t["UploadLimits.MetresSide", Metres(l.MaxPhysicalPartSize)]),
            Line("SmallestPart", t["UploadLimits.MetresSide", Metres(l.MinPartSize)]),
            Line("Parts", l.MaxLinksetParts == 0 ? t["UploadLimits.NoLimit"] : t.Count("UploadLimits.PartsCount", l.MaxLinksetParts)),
            Line("Materials", t.Count("UploadLimits.FacesCount", FacesPerPart)),
            Line("Points", Whole(PointsPerFace)),
            Line("Picture", t["UploadLimits.PixelsSide", Whole(l.MaxTextureSide)]),
            Line("Heavy", Thousands(l.HeavyTriangles)),
            Line("Triangles", Thousands(l.MaxTriangles)),
            Line("Objects", Whole(l.MaxNodes)),
            Line("FileSize", t["Upload.Megabytes", Whole(l.MaxUploadBytes / (1024 * 1024))]),
            Line("PictureInFile", t["UploadLimits.PixelsSide", Whole(l.MaxImageSide)]),
        ];
    }

    /// <summary>A row: its name, why and tip are the keys <c>UploadLimits.&lt;row&gt;.Name</c>, <c>.Why</c> and <c>.Tip</c>.</summary>
    private LimitLine Line(string row, string value) =>
        new(t[$"UploadLimits.{row}.Name"], value, t[$"UploadLimits.{row}.Why"], t[$"UploadLimits.{row}.Tip"]);

    // Numbers are written in the page's language: 2,000,000 in English, 2 000 000 in French.
    private static string Whole(long n) => n.ToString(CultureInfo.CurrentCulture);
    private static string Thousands(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
    private static string Metres(double m) => m.ToString("0.###", CultureInfo.CurrentCulture);
}
