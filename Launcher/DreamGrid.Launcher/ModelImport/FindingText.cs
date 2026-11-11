using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;

namespace DreamGrid.Launcher.ModelImport;

/// <summary>One finding in words: what happened, why it matters, and how to fix it in Blender.</summary>
public sealed record FindingLine(string Title, string Why, string Tip);

/// <summary>
/// The guest-facing words for each finding code the converter writes into <c>report.json</c>, with its numbers put in. Kept
/// here, not in the converter, so the words can go through the languages work later. A code it doesn't know gives a generic
/// line; a missing value gives a plain word in its place. Neither ever throws.
/// </summary>
public static class FindingText
{
    public static FindingLine Of(Finding f, string itemName)
    {
        var v = f.Values ?? new Dictionary<string, string>();
        // Part and picture names come from the uploaded file, so they are cut short (the page escapes them).
        string Get(string key, string fallback) => v.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s) ? Short(s) : fallback;
        string N(string key, string fallback) => Number(Get(key, fallback));
        var item = string.IsNullOrWhiteSpace(itemName) ? "The model" : itemName;

        return f.Code switch
        {
            "split-materials" => new(
                $"Split into {N("parts", "more")} linked parts",
                $"({N("materials", "many")} materials, {N("perPart", "8")} per part). This won't change how it looks.",
                "In Blender, join materials that share a picture, or bake them into one, to keep it to one part."),
            "split-vertices" => new(
                $"Split {N("faces", "some")} large faces into smaller ones",
                $"A face can hold {N("limit", "65535")} points at most. This won't change how it looks.",
                "In Blender, a Decimate modifier lowers the number of points."),
            "texture-resized" => new(
                $"Resized the picture {Get("image", "in the model")}",
                "Pictures here are sized in powers of two (like 512 or 1024). This won't change how it looks.",
                "In Blender, save pictures at sizes like 512 × 512 or 1024 × 1024."),
            "texture-shrunk" => new(
                $"The picture {Get("image", "in the model")} was made smaller, from {N("from", "its size")} to {N("to", "1024")} pixels",
                "Pictures on this grid are at most 1024 pixels on a side, so it will look softer up close.",
                "In Blender, bake or save the picture at 1024 × 1024 or smaller, then export again."),
            "texture-unsupported" => new(
                $"The picture {Get("image", "in the model")} was left out",
                $"Only PNG and JPEG pictures can be used, and this one is {Get("format", "another kind")}. Its faces will have no picture.",
                "In Blender, save the picture as PNG or JPEG, then export again."),
            "heavy-mesh" => new(
                $"The model has {N("triangles", "a great many")} triangles",
                $"Above {N("threshold", "200000")}, the simpler versions shown from far away may look lumpy or have holes.",
                "In Blender, add a Decimate modifier to lower the number of triangles."),
            "too-big" => new(
                v.ContainsKey("side") ? $"{Get("part", item)} is {N("side", "")} m on its longest side" : $"{Get("part", item)} is too big",
                $"Parts on this grid can be at most {N("limit", "256")} m, so every region can hold them.",
                "In Blender, scale the model down (S), apply the scale (Ctrl+A), then export again. Or choose a smaller size here."),
            "too-small" => new(
                $"{N("dropped", "Some")} of {N("total", "the")} parts are too small and were left out",
                $"Parts must be at least {N("limit", "0.001")} m on a side.",
                "In Blender, join tiny pieces to a larger part (Ctrl+J), then export again."),
            "too-many-parts" => new(
                $"The item has {N("parts", "too many")} parts",
                $"This grid allows {N("limit", "only so many")} parts in one item.",
                "In Blender, join objects (Ctrl+J) or use fewer materials, then export again."),
            "inside-out" => new(
                $"{Get("part", item)} may be inside out" + (Get("parts", "1") is var n && n != "1" ? $" ({Number(n)} parts)" : ""),
                "It was mirrored, so its faces may point the wrong way and look see-through.",
                "In Blender, apply the scale (Ctrl+A), then in Edit Mode recalculate the normals (Shift+N)."),
            "no-uv" => new(
                $"{N("faces", "Some")} faces have a picture but no UV map",
                "Without one the picture can't be placed, so it will look smeared.",
                "In Blender, unwrap the mesh in Edit Mode (U), then export again."),
            "dropped-animation" => new(
                "Animations were left out",
                "Items here can't play glTF animations. This won't change how it looks standing still.",
                "Nothing to fix. In Blender's glTF export you can untick Animation."),
            "dropped-morphs" => new(
                "Shape keys were left out",
                "The item is made in its basic shape.",
                "In Blender, apply the shape key you want before exporting, or untick Shape Keys in the glTF export."),
            "dropped-extras" => new(
                $"{Words(Get("what", "cameras,lights"))} were left out",
                "Items can't carry them. This won't change how it looks.",
                "In Blender's glTF export, untick Cameras and Punctual Lights."),
            "skin-ignored" => new(
                $"The model is rigged ({N("joints", "with")} bones), but it's made as a plain object",
                "In this version it stands in its rest pose and can't be worn.",
                "In Blender, export without the armature if you only want the object."),
            "compressed" => new(
                $"The file is compressed ({Get("extension", "Draco or meshopt")})",
                "Compressed models can't be read here.",
                "In Blender's glTF export, untick Compression, then export again."),
            "external-files" => new(
                $"The file uses {N("count", "other")} separate files",
                "Only one file can be uploaded, with everything inside it.",
                "In Blender's glTF export, choose glTF Binary (.glb)."),
            "not-triangles" => new(
                $"{Words(Get("what", "lines,points"))} were left out",
                "Only faces can be made into an item.",
                "In Blender, give lines some thickness (a Skin or Solidify modifier) or convert curves to a mesh."),
            "bad-file" => new(
                "We couldn't read this file",
                Get("reason", "It isn't a readable glTF 2.0 file."),
                "In Blender, use File > Export > glTF 2.0 and choose glTF Binary (.glb)."),
            "too-large-file" => TooLarge(Get("what", "upload"), v),
            _ => new(
                "Something in the model needs a look",
                "The check found something this page can't describe yet.",
                "In Blender, check the model and export it again as glTF Binary (.glb)."),
        };
    }

    private static FindingLine TooLarge(string what, IReadOnlyDictionary<string, string> v)
    {
        var limit = v.TryGetValue("limit", out var l) ? l : null;
        return what switch
        {
            "image" => new(
                $"A picture is {(v.TryGetValue("image", out var size) ? size.Replace("x", " × ", StringComparison.Ordinal) : "too many")} pixels",
                $"Pictures in an upload can be at most {Number(limit ?? "8192")} pixels on a side.",
                "In Blender, save the picture at 1024 × 1024 or smaller, then export again."),
            "triangles" => new(
                $"The model has more than {Number(limit ?? "2000000")} triangles",
                "That's more than this grid can take in one item.",
                "In Blender, add a Decimate modifier to lower the number of triangles."),
            "nodes" => new(
                $"The model has more than {Number(limit ?? "512")} objects",
                "That's more than this grid can take in one item.",
                "In Blender, join objects (Ctrl+J), then export again."),
            _ => new(
                $"The file is larger than {Megabytes(limit)}",
                "That's the largest file this grid takes.",
                "In Blender, use smaller pictures and a Decimate modifier, then export again."),
        };
    }

    private static string Short(string s) => s.Length <= 64 ? s : s[..(char.IsHighSurrogate(s[63]) ? 63 : 64)] + "…";

    /// <summary>"cameras,lights" as "Cameras and lights".</summary>
    private static string Words(string list)
    {
        var parts = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "Some parts";
        var joined = parts.Length == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return char.ToUpperInvariant(joined[0]) + joined[1..];
    }

    /// <summary>A whole number with thousands separators; anything else as it was written.</summary>
    private static string Number(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.InvariantCulture) : s;

    private static string Megabytes(string? bytes) =>
        long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? (n / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"
            : "64 MB";
}
