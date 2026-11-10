using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;

namespace GridGate.ModelImport.Building;

/// <summary>Where a part's prim goes and how big it is: Scale is the prim's size, Position its centre, in the model's frame.</summary>
public sealed record PrimPlacement(Vector3 Scale, Vector3 Position);

/// <summary>
/// OpenSim's physics and the viewers read a mesh asset as a unit mesh that the prim's scale stretches. A part's mesh is therefore
/// fitted into -0.5..0.5 on each axis around the centre of its vertex box, and its prim is given the box's size (times the part's
/// scale) and placed at the box's centre. A flat side gets the grid's smallest part size, not 0.
/// </summary>
public static class UnitMesh
{
    public static PrimPlacement Placement(ImportPart part, double minPartSize)
    {
        var (lo, hi) = Box(part);
        var scale = Vector3.Abs(part.Scale);
        var size = Vector3.Max((hi - lo) * scale, new Vector3((float)minPartSize));
        var centre = (lo + hi) / 2 * scale;
        return new PrimPlacement(size, part.Position + Vector3.Transform(centre, part.Rotation));
    }

    /// <summary>
    /// The part's mesh fitted into -0.5..0.5. Normals are multiplied by the stretch the prim's scale undoes (prim size over part
    /// scale), as the Second Life exporter does, so they stay at right angles to the surface once the prim is scaled.
    /// </summary>
    public static ImportMesh Fit(ImportPart part, double minPartSize)
    {
        var (lo, hi) = Box(part);
        var centre = (lo + hi) / 2;
        var extent = hi - lo;
        var shrink = new Vector3(Inverse(extent.X), Inverse(extent.Y), Inverse(extent.Z));

        var size = Placement(part, minPartSize).Scale;
        var scale = Vector3.Abs(part.Scale);
        var normalStretch = new Vector3(Ratio(size.X, scale.X), Ratio(size.Y, scale.Y), Ratio(size.Z, scale.Z));

        var faces = part.Mesh.Faces.Select(f => f with
        {
            Positions = f.Positions.Select(v => (v - centre) * shrink).ToArray(),
            Normals = f.Normals.Select(n => Stretched(n, normalStretch)).ToArray(),
        }).ToList();
        return new ImportMesh(faces);
    }

    private static (Vector3 Lo, Vector3 Hi) Box(ImportPart part)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var f in part.Mesh.Faces)
            foreach (var v in f.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); any = true; }
        return any ? (lo, hi) : (Vector3.Zero, Vector3.Zero);
    }

    // A flat axis maps every vertex to 0.
    private static float Inverse(float extent) => extent > 0 ? 1 / extent : 0;

    private static float Ratio(float size, float scale) => scale > 0 ? size / scale : 1;

    private static Vector3 Stretched(Vector3 n, Vector3 stretch)
    {
        var s = n * stretch;
        var length = s.Length();
        return length > 1e-12f ? s / length : n;
    }
}
