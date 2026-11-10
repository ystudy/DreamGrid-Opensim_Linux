using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridGate.ModelImport;

/// <summary>The region's limits, as the launcher passes them to the converter.</summary>
public sealed record ImportLimits(double MaxPartSize, double MinPartSize, double MaxPhysicalPartSize, int MaxLinksetParts /* 0 = none */,
    long MaxUploadBytes, int MaxImageSide, int MaxTextureSide, int MaxTriangles, int MaxNodes, int HeavyTriangles);

public sealed record NextOwner(bool Copy, bool Modify, bool Transfer);

public sealed record ImportOptions(string ItemName, string FileName, int ScalePercent, NextOwner NextOwner, Guid UploaderId, string UploaderName);

public enum FindingKind { Safe, Risky, Blocking }

public sealed record Finding(string Code, FindingKind Kind, IReadOnlyDictionary<string, string> Values);

public sealed record ReportSummary(int Parts, int Faces, int Triangles, int Textures, double[] SizeMetres, int? LargestFitPercent);

public sealed record ImportReport(int Version /* 1 */, bool Blocked, ReportSummary Summary, IReadOnlyList<Finding> Findings);

public sealed record ManifestAsset(Guid Id, int Type /* 0 texture, 49 mesh, 6 object */, string Name, string File /* "assets/<id>.bin" */);

public sealed record ManifestItem(Guid AssetId, string Name, string Description, uint NextOwnerMask, Guid CreatorId);

public sealed record ImportManifest(int Version /* 1 */, IReadOnlyList<ManifestAsset> Assets, ManifestItem Item);

/// <summary>JSON settings shared by every file the converter writes: camelCase names, enums as camelCase strings.</summary>
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
