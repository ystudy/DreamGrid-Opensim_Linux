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
            fit = (int)Math.Floor(limits.MaxPartSize * 100.0 / (biggestSide / factor) + 1e-9);
            findings.Add(Make("too-big", FindingKind.Blocking, ("part", biggest.Name), ("side", Num(biggestSide)), ("limit", Num(limits.MaxPartSize)),
                ("fitPercent", Num(fit.Value))));
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
            var triangles = f.Triangles;
            if (flip)
            {
                triangles = (int[])triangles.Clone();
                for (int i = 0; i + 2 < triangles.Length; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            }
            return f with { Positions = f.Positions.Select(v => v * sign).ToArray(), Normals = f.Normals.Select(v => v * sign).ToArray(), Triangles = triangles };
        }).ToList();
        return part with { Scale = Vector3.Abs(part.Scale), Mesh = new ImportMesh(faces) };
    }

    // The sides of the part's bounding box along the world axes (scale and rotation applied, position not). Null without vertices.
    private static float[]? Sides(ImportPart part)
    {
        var (min, max, any) = Bounds(part);
        if (!any) return null;
        var size = max - min;
        return new[] { size.X, size.Y, size.Z };
    }

    private static (Vector3 Min, Vector3 Max, bool Any) Bounds(ImportPart part, bool withPosition = false)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var f in part.Mesh.Faces)
            foreach (var v in f.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); any = true; }
        if (!any) return (Vector3.Zero, Vector3.Zero, false);

        // The corners of the local box carry the same extremes as the vertices after any scale and rotation.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            var w = Vector3.Transform(corner * part.Scale, part.Rotation) + (withPosition ? part.Position : Vector3.Zero);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        return (min, max, true);
    }

    // The size of the whole item along the world axes, in metres.
    private static double[] Overall(IReadOnlyList<ImportPart> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var p in parts)
        {
            var (lo, hi, has) = Bounds(p, true);
            if (!has) continue;
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
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
