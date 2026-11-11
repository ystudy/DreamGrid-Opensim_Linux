using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.ModelImport;

/// <summary>One finding in words: what happened, why it matters, and how to fix it in Blender.</summary>
public sealed record FindingLine(string Title, string Why, string Tip);

/// <summary>
/// The guest-facing words for each finding code the converter writes into <c>report.json</c>, in the page's language
/// (<c>Finding.*</c> keys in ResidentText), with its numbers put in. A code it doesn't know gives a generic line; a missing value
/// gives a plain word in its place. Neither ever throws.
/// </summary>
public static class FindingText
{
    public static FindingLine Of(IStringLocalizer<ResidentText> t, Finding f, string itemName)
    {
        var v = f.Values ?? new Dictionary<string, string>();
        bool Has(string key) => v.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s);
        // Part and picture names come from the uploaded file, so they are cut short (the page escapes them).
        string Get(string key, string fallback) => Has(key) ? Short(v[key]) : fallback;
        string L(string key, params object[] args) => t[key, args].Value;
        // A number from the report, written in the page's language; a missing one is the given default, or "several".
        string N(string key, string? fallback = null) => Has(key) ? Number(v[key]) : fallback is null ? L("Finding.Several") : Number(fallback);
        string Picture() => Get("image", L("Finding.InTheModel"));
        var item = string.IsNullOrWhiteSpace(itemName) ? L("Finding.TheModel") : itemName;
        FindingLine Line(string name, params object[] title) =>
            new(L($"Finding.{name}.Title", title), L($"Finding.{name}.Why"), L($"Finding.{name}.Tip"));
        FindingLine LineWhy(string name, object[] title, params object[] why) =>
            new(L($"Finding.{name}.Title", title), L($"Finding.{name}.Why", why), L($"Finding.{name}.Tip"));

        return f.Code switch
        {
            "split-materials" => LineWhy("SplitMaterials", [N("parts")], N("materials"), N("perPart", "8")),
            "split-vertices" => LineWhy("SplitVertices", [N("faces")], N("limit", "65535")),
            "texture-resized" => Line("TextureResized", Picture()),
            "texture-shrunk" => Line("TextureShrunk", Picture(), Has("from") ? N("from") : L("Finding.ItsSize"), N("to", "1024")),
            "texture-unsupported" => LineWhy("TextureUnsupported", [Picture()],
                Get("format", "unknown") is var format && format != "unknown" ? format.ToUpperInvariant() : L("Finding.AnotherKind")),
            "heavy-mesh" => new(
                Has("triangles") ? L("Finding.HeavyMesh.Title", N("triangles")) : L("Finding.HeavyMesh.TitleNoCount"),
                L("Finding.HeavyMesh.Why", N("threshold", "200000")),
                L("Finding.HeavyMesh.Tip")),
            "too-big" => new(
                Has("side") ? L("Finding.TooBig.Title", Get("part", item), N("side")) : L("Finding.TooBig.TitleNoSide", Get("part", item)),
                L("Finding.TooBig.Why", N("limit", "1024")),
                L("Finding.TooBig.Tip")),
            "too-small" => new(
                Has("dropped") && Has("total") ? L("Finding.TooSmall.Title", N("dropped"), N("total")) : L("Finding.TooSmall.TitleNoCount"),
                L("Finding.TooSmall.Why", N("limit", "0.001")),
                L("Finding.TooSmall.Tip")),
            "too-many-parts" => new(
                L("Finding.TooManyParts.Title", N("parts")),
                Has("limit") ? L("Finding.TooManyParts.Why", N("limit")) : L("Finding.TooManyParts.WhyNoLimit"),
                L("Finding.TooManyParts.Tip")),
            "inside-out" => Get("parts", "1") is var n && n != "1"
                ? Line("InsideOutMany", Get("part", item), Number(n))
                : Line("InsideOut", Get("part", item)),
            "no-uv" => Line("NoUv", N("faces")),
            "dropped-animation" => Line("DroppedAnimation"),
            "dropped-morphs" => Line("DroppedMorphs"),
            "dropped-extras" => Line("DroppedExtras", Words(t, Get("what", "cameras,lights"))),
            "vertex-colours-ignored" => Line("VertexColours"),
            "double-sided-ignored" => Get("materials", "1") is var m && m != "1"
                ? Line("DoubleSidedMany", Get("material", L("Finding.SomeFaces")), Number(m))
                : Line("DoubleSided", Get("material", L("Finding.SomeFaces"))),
            "texture-coords-ignored" => Line("TextureCoords"),
            "skin-ignored" => Line("SkinIgnored", N("joints")),
            "compressed" => Line("Compressed", Get("extension", "Draco / meshopt")),
            "external-files" => Line("ExternalFiles", N("count")),
            "not-triangles" => Line("NotTriangles", Words(t, Get("what", "lines,points"))),
            "bad-file" => new(
                L("Finding.BadFile.Title"),
                // The reason is the glTF reader's own message (English and technical), unless it is one of the converter's.
                Get("reason", "") switch { "" => L("Finding.BadFile.Why"), "The file has no mesh." => L("Finding.BadFile.NoMesh"), var r => r },
                L("Finding.BadFile.Tip")),
            "too-large-file" => TooLarge(t, Get("what", "upload"), v),
            _ => Line("Unknown"),
        };
    }

    private static FindingLine TooLarge(IStringLocalizer<ResidentText> t, string what, IReadOnlyDictionary<string, string> v)
    {
        var limit = v.TryGetValue("limit", out var l) ? l : null;
        string L(string key, params object[] args) => t[key, args].Value;
        FindingLine Line(string name, params object[] title) =>
            new(L($"Finding.TooLarge.{name}.Title", title), L($"Finding.TooLarge.{name}.Why", title), L($"Finding.TooLarge.{name}.Tip"));
        return what switch
        {
            "image" => Line("Image",
                v.TryGetValue("image", out var size) && size.Split('x') is [var w, var h] ? $"{Number(w)} × {Number(h)}" : L("Finding.Several"),
                Number(limit ?? "8192")),
            "triangles" => Line("Triangles", Number(limit ?? "2000000")),
            "nodes" => Line("Nodes", Number(limit ?? "512")),
            "assets" => Line("Assets", Megabytes(t, limit)),
            _ => Line("Upload", Megabytes(t, limit)),
        };
    }

    private static string Short(string s) => s.Length <= 64 ? s : s[..(char.IsHighSurrogate(s[63]) ? 63 : 64)] + "…";

    /// <summary>"cameras,lights" as "Cameras and lights", in the page's language. A word the page doesn't know is shown as it is.</summary>
    private static string Words(IStringLocalizer<ResidentText> t, string list)
    {
        var parts = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p switch
            {
                "cameras" => t["Finding.What.Cameras"].Value,
                "lights" => t["Finding.What.Lights"].Value,
                "lines" => t["Finding.What.Lines"].Value,
                "points" => t["Finding.What.Points"].Value,
                _ => p,
            }).ToArray();
        if (parts.Length == 0) return t["Finding.SomeParts"].Value;
        var joined = parts.Length == 1 ? parts[0] : t["Finding.And", string.Join(", ", parts[..^1]), parts[^1]].Value;
        return char.ToUpper(joined[0], CultureInfo.CurrentCulture) + joined[1..];
    }

    /// <summary>A number in the page's language (2,100,000 in English); anything else as it was written.</summary>
    private static string Number(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.CurrentCulture)
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.###", CultureInfo.CurrentCulture)
        : s;

    private static string Megabytes(IStringLocalizer<ResidentText> t, string? bytes) =>
        t["Upload.Megabytes", long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? (n / (1024 * 1024)).ToString(CultureInfo.CurrentCulture)
            : "64"].Value;
}
