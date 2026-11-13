using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;

namespace GridGate.ModelImport.Checking;

public sealed record CheckResult(ImportModel Fixed, IReadOnlyList<Finding> Findings, ReportSummary Summary, bool Blocked);

/// <summary>
/// Checks a model against the grid's limits and fixes what is safe to fix. Fixed has the scale applied, negative scales baked into
/// the vertices, parts below the smallest size dropped, and every mesh within 8 faces of 65,535 vertices. The skeleton and the
/// vertex weights are kept as they are for the writer. Blocking findings do not stop the fixing, so the report can still show sizes.
/// </summary>
public static class ModelChecker
{
    public static CheckResult Check(ImportModel model, ImportLimits limits, ImportOptions options)
    {
        var findings = new List<Finding>();
        var factor = Math.Max(1, options.ScalePercent) / 100.0;

        // Lines, points, animation, shape keys, cameras and lights were left out by the reader; say so.
        var linesAndPoints = model.Dropped.Where(d => d is "lines" or "points").ToArray();
        if (linesAndPoints.Length > 0)
            findings.Add(Make("not-triangles", model.Parts.Count == 0 ? FindingKind.Blocking : FindingKind.Safe, ("what", string.Join(",", linesAndPoints))));
        if (model.Dropped.Contains("animation")) findings.Add(Make("dropped-animation", FindingKind.Safe));
        if (model.Dropped.Contains("morphs")) findings.Add(Make("dropped-morphs", FindingKind.Safe));
        var extras = model.Dropped.Where(d => d is "cameras" or "lights").ToArray();
        if (extras.Length > 0) findings.Add(Make("dropped-extras", FindingKind.Safe, ("what", string.Join(",", extras))));

        // Negative scales go into the vertices: a prim cannot have one.
        var mirrored = new List<string>();
        var parts = model.Parts.Select(p =>
        {
            if (p.Scale.X >= 0 && p.Scale.Y >= 0 && p.Scale.Z >= 0) return p;
            mirrored.Add(p.Name);
            return BakeNegativeScale(p);
        }).ToList();
        if (mirrored.Count > 0)
            findings.Add(Make("inside-out", FindingKind.Risky, ("parts", Num(mirrored.Count)), ("part", mirrored[0])));

        // The user's scale, then the parts below the smallest size.
        parts = parts.Select(p => p with { Position = p.Position * (float)factor, Scale = p.Scale * (float)factor }).ToList();
        var tiny = parts.Where(p => Sides(p) is { } s && s.Max() < limits.MinPartSize).ToList();
        if (tiny.Count > 0)
        {
            var kind = tiny.Count * 2 < parts.Count ? FindingKind.Safe : FindingKind.Blocking;
            findings.Add(Make("too-small", kind, ("dropped", Num(tiny.Count)), ("total", Num(parts.Count)), ("limit", Num(limits.MinPartSize))));
            parts = parts.Where(p => !tiny.Contains(p)).ToList();
        }
        if (parts.Count == 0 && tiny.Count == 0 && linesAndPoints.Length == 0)
            findings.Add(Make("bad-file", FindingKind.Blocking, ("reason", "The file has no mesh.")));

        // Too big, measured after the scale. The fit percentage is for the model as it was read.
        ImportPart? biggest = null;
        double biggestSide = 0;
        foreach (var p in parts)
        {
            var side = Sides(p)?.Max() ?? 0;
            if (side > biggestSide) { biggestSide = side; biggest = p; }
        }
        int? fit = null;
        if (biggest != null && biggestSide > limits.MaxPartSize)
        {
            // The offer is at least 1%; when even that does not fit there is no offer.
            var offer = (int)Math.Floor(limits.MaxPartSize * 100.0 / (biggestSide / factor) + 1e-9);
            if (offer >= 1) fit = offer;
            var values = new List<(string, string)> { ("part", biggest.Name), ("side", Num(biggestSide)), ("limit", Num(limits.MaxPartSize)) };
            if (fit != null) values.Add(("fitPercent", Num(fit.Value)));
            findings.Add(Make("too-big", FindingKind.Blocking, values.ToArray()));
        }

        // Faces and parts within the grid's mesh format.
        var tooManyVertices = parts.SelectMany(p => p.Mesh.Faces).Count(f => f.Positions.Length > FaceSplitter.MaxVerticesPerFace);
        if (tooManyVertices > 0)
            findings.Add(Make("split-vertices", FindingKind.Safe, ("faces", Num(tooManyVertices)), ("limit", Num(FaceSplitter.MaxVerticesPerFace))));

        var split = new List<ImportPart>();
        ImportPart? mostMaterials = null;
        int mostMaterialCount = 0, mostMaterialParts = 0;
        foreach (var p in parts)
        {
            var pieces = FaceSplitter.Split(p);
            split.AddRange(pieces);
            // Only a mesh that has more materials than faces fit is a material split; a face cut by the vertex limit is reported above.
            if (p.Mesh.Faces.Count > FaceSplitter.MaxFacesPerMesh)
            {
                var materials = p.Mesh.Faces.Select(f => f.Material).Distinct().Count();
                if (materials > mostMaterialCount) { mostMaterials = p; mostMaterialCount = materials; mostMaterialParts = pieces.Count; }
            }
        }
        if (mostMaterials != null)
            findings.Add(Make("split-materials", FindingKind.Safe, ("part", mostMaterials.Name), ("materials", Num(mostMaterialCount)),
                ("perPart", Num(FaceSplitter.MaxFacesPerMesh)), ("parts", Num(mostMaterialParts))));

        if (limits.MaxLinksetParts > 0 && split.Count > limits.MaxLinksetParts)
            findings.Add(Make("too-many-parts", FindingKind.Blocking, ("parts", Num(split.Count)), ("limit", Num(limits.MaxLinksetParts))));

        var fixedModel = model with { Parts = split, RootPart = 0 };
        var triangles = split.Sum(p => p.Mesh.Faces.Sum(f => (long)f.Triangles.Length / 3));
        if (triangles > limits.HeavyTriangles)
            findings.Add(Make("heavy-mesh", FindingKind.Risky, ("triangles", Num(triangles)), ("threshold", Num(limits.HeavyTriangles))));

        var noUv = split.SelectMany(p => p.Mesh.Faces).Count(f => f.Uv0 == null && f.Material >= 0 && f.Material < model.Materials.Count
            && model.Materials[f.Material].BaseColourImage != null);
        if (noUv > 0) findings.Add(Make("no-uv", FindingKind.Risky, ("faces", Num(noUv))));

        // What a prim cannot show is said plainly: vertex colours, a second UV set or a moved picture, and back faces.
        if (model.Dropped.Contains("vertex-colours")) findings.Add(Make("vertex-colours-ignored", FindingKind.Risky));
        if (model.Dropped.Contains("texture-coords")) findings.Add(Make("texture-coords-ignored", FindingKind.Risky));
        var doubleSided = split.SelectMany(p => p.Mesh.Faces).Select(f => f.Material).Distinct()
            .Where(m => m >= 0 && m < model.Materials.Count && model.Materials[m].DoubleSided).OrderBy(m => m).ToList();
        if (doubleSided.Count > 0)
            findings.Add(Make("double-sided-ignored", FindingKind.Risky, ("material", model.Materials[doubleSided[0]].Name), ("materials", Num(doubleSided.Count))));

        if (model.Skeleton != null)
            findings.Add(Make("skin-ignored", FindingKind.Risky, ("joints", Num(model.Skeleton.Joints.Count))));

        var summary = new ReportSummary(split.Count, split.Sum(p => p.Mesh.Faces.Count), (int)triangles, model.Images.Count, Overall(split), fit);
        return new CheckResult(fixedModel, findings, summary, findings.Any(f => f.Kind == FindingKind.Blocking));
    }

    // The part with each negative scale component moved into its positions and normals. An odd number of mirrors turns the
    // triangles inside out, so their winding is reversed to point outwards again.
    private static ImportPart BakeNegativeScale(ImportPart part)
    {
        var sign = new Vector3(part.Scale.X < 0 ? -1 : 1, part.Scale.Y < 0 ? -1 : 1, part.Scale.Z < 0 ? -1 : 1);
        bool flip = sign.X * sign.Y * sign.Z < 0;
        var faces = part.Mesh.Faces.Select(f =>
        {
            var triangles = flip ? Winding.Reversed(f.Triangles) : f.Triangles;
            return f with { Positions = f.Positions.Select(v => v * sign).ToArray(), Normals = f.Normals.Select(v => v * sign).ToArray(), Triangles = triangles };
        }).ToList();
        return part with { Scale = Vector3.Abs(part.Scale), Mesh = new ImportMesh(faces) };
    }

    // The part's own size the way OpenSim sees a prim: its local vertex box times the scale, whatever its rotation. Null without vertices.
    private static float[]? Sides(ImportPart part)
    {
        var (lo, hi, any) = LocalBox(part);
        if (!any) return null;
        var size = (hi - lo) * Vector3.Abs(part.Scale);
        return new[] { size.X, size.Y, size.Z };
    }

    private static (Vector3 Min, Vector3 Max, bool Any) LocalBox(ImportPart part) => MeshParts.Bounds(part.Mesh.Faces);

    // The size of the whole item along the world axes, in metres: every vertex through scale, rotation and position. Display only.
    private static double[] Overall(IReadOnlyList<ImportPart> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var p in parts)
            foreach (var f in p.Mesh.Faces)
                foreach (var v in f.Positions)
                {
                    var w = Vector3.Transform(v * p.Scale, p.Rotation) + p.Position;
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                    any = true;
                }
        if (!any) return new double[3];
        var size = max - min;
        return new double[] { size.X, size.Y, size.Z };
    }

    private static Finding Make(string code, FindingKind kind, params (string Key, string Value)[] values) =>
        new(code, kind, values.ToDictionary(v => v.Key, v => v.Value));

    // Numbers in findings are text; invariant culture, at most three decimals.
    private static string Num(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
