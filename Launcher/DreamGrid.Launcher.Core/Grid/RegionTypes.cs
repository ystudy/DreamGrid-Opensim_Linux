namespace DreamGrid.Launcher.Core.Grid;

/// <summary>A kind of land a new region can start as; its OAR and picture are optional files in the program's content folder.</summary>
public sealed record RegionType(string Id, string Name, string Description, TerrainShape Shape);

public static class RegionTypes
{
    public static IReadOnlyList<RegionType> BuiltIn { get; } =
    [
        new("island", "Default Island", "A round island with a gentle hill.", TerrainShape.Island),
        new("beach", "Sandy Beach", "A low, flat island with wide sandy shores.", TerrainShape.Beach),
        new("forest", "Forest", "Hilly ground for a forest.", TerrainShape.Forest),
        new("meadow", "Meadow", "Gently rolling open land.", TerrainShape.Meadow),
    ];

    public static RegionType? Find(string id) => BuiltIn.FirstOrDefault(t => t.Id == id);

    /// <summary>Path of the type's OAR (<c>content/region-types/&lt;id&gt;.oar</c>), or null for an unknown type or a missing file.</summary>
    public static string? Oar(LauncherPaths paths, string id) => ContentFile(paths, id, ".oar");

    /// <summary>Path of the type's picture (<c>&lt;id&gt;.png</c>), or null for an unknown type or a missing file.</summary>
    public static string? Picture(LauncherPaths paths, string id) => ContentFile(paths, id, ".png");

    /// <summary>True for <c>starter</c> (the setup checklist's content) and each built-in id.</summary>
    public static bool IsContent(string content) => content == "starter" || Find(content) is not null;

    private static string? ContentFile(LauncherPaths paths, string id, string extension)
    {
        if (Find(id) is null) return null;
        var path = Path.Combine(StarterContent.ProgramRoot(paths), "content", "region-types", id + extension);
        return File.Exists(path) ? path : null;
    }
}
