using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamGrid.Launcher.Core.ModelImport;

// The model converter's file contract, copied from OpenSim/Tools/GridGate.ModelImport/Contracts.cs with identical JSON names.
// The launcher references no converter code; only these JSON files cross between them (tests/fixtures/model-import).

/// <summary>The region's limits, as the launcher passes them to the converter. <see cref="MaxLinksetParts"/> 0 means none.</summary>
public sealed record ImportLimits(double MaxPartSize, double MinPartSize, double MaxPhysicalPartSize, int MaxLinksetParts,
    long MaxUploadBytes, int MaxImageSide, int MaxTextureSide, int MaxTriangles, int MaxNodes, int HeavyTriangles);

public sealed record NextOwner(bool Copy, bool Modify, bool Transfer);

public sealed record ImportOptions(string ItemName, string FileName, int ScalePercent, NextOwner NextOwner, Guid UploaderId, string UploaderName);

public enum FindingKind { Safe, Risky, Blocking }

public sealed record Finding(string Code, FindingKind Kind, IReadOnlyDictionary<string, string> Values);

public sealed record ReportSummary(int Parts, int Faces, int Triangles, int Textures, double[] SizeMetres, int? LargestFitPercent);

public sealed record ImportReport(int Version, bool Blocked, ReportSummary Summary, IReadOnlyList<Finding> Findings);

/// <summary>Type is 0 texture, 49 mesh, 6 object; File is "assets/&lt;id&gt;.bin".</summary>
public sealed record ManifestAsset(Guid Id, int Type, string Name, string File);

public sealed record ManifestItem(Guid AssetId, string Name, string Description, uint NextOwnerMask, Guid CreatorId);

public sealed record ImportManifest(int Version, IReadOnlyList<ManifestAsset> Assets, ManifestItem Item);

/// <summary>JSON settings for every contract file: camelCase names, enums as camelCase strings, nulls written as null.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
