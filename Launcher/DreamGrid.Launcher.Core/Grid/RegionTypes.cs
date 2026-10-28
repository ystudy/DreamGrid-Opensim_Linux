using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// A kind of land a new region can start as. A built-in type's OAR and picture are optional files in the program's content
/// folder; a <see cref="Custom"/> type is made from a file in the OAR library.
/// </summary>
public sealed record RegionType(string Id, string Name, string Description, TerrainShape Shape, bool Custom = false);

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

    /// <summary>The built-in types, then the custom ones in the order they were added.</summary>
    public static IReadOnlyList<RegionType> All(LauncherSettings s) =>
        [.. BuiltIn, .. s.CustomTypes.Select(c => new RegionType(c.Id, c.Name, $"Made from {c.File}.", TerrainShape.Island, Custom: true))];

    /// <summary>The built-in or custom type with this id, or null.</summary>
    public static RegionType? Find(LauncherSettings s, string id) => All(s).FirstOrDefault(t => t.Id == id);

    /// <summary>True for a custom type's id (<c>c-</c> and 8 lowercase hex characters).</summary>
    public static bool IsCustom(string id) =>
        id.Length == 10 && id.StartsWith("c-", StringComparison.Ordinal)
        && id.AsSpan(2).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Path of the type's OAR (<c>content/region-types/&lt;id&gt;.oar</c>), or null for an unknown type or a missing file.</summary>
    public static string? Oar(LauncherPaths paths, string id) => ContentFile(paths, id, ".oar");

    /// <summary>Path of a built-in type's OAR, or of a custom type's library file; null for an unknown type or a missing file.</summary>
    public static string? Oar(LauncherPaths paths, LauncherSettings s, string id) =>
        s.CustomTypes.FirstOrDefault(c => c.Id == id) is { } custom
            ? new OarLibrary(paths).Resolve(custom.File)
            : Oar(paths, id);

    /// <summary>Path of the type's picture (<c>&lt;id&gt;.png</c>), or null for an unknown type or a missing file.</summary>
    public static string? Picture(LauncherPaths paths, string id) => ContentFile(paths, id, ".png");

    /// <summary>True for <c>starter</c> (the setup checklist's content) and each built-in id.</summary>
    public static bool IsContent(string content) => content == "starter" || Find(content) is not null;

    /// <summary>True for <c>starter</c> and each built-in or custom id.</summary>
    public static bool IsContent(LauncherSettings s, string content) => content == "starter" || Find(s, content) is not null;

    private static string? ContentFile(LauncherPaths paths, string id, string extension)
    {
        if (Find(id) is null) return null;
        var path = Path.Combine(StarterContent.ProgramRoot(paths), "content", "region-types", id + extension);
        return File.Exists(path) ? path : null;
    }
}
