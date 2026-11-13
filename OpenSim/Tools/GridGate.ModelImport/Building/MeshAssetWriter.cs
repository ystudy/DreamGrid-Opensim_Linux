using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;
using OpenMetaverse.StructuredData;
using OmvVector2 = OpenMetaverse.Vector2;
using OmvVector3 = OpenMetaverse.Vector3;

namespace GridGate.ModelImport.Building;

/// <summary>What goes into a mesh asset besides the shape: the skin block and per-vertex weights, written only when asked.</summary>
public sealed record MeshAssetOptions(bool WriteSkin, ImportSkeleton? Skeleton);

/// <summary>
/// Writes a mesh asset (asset type 49) in the Second Life mesh asset format, as OpenSim's readers (Meshmerizer, ubODE's
/// Meshmerizer, MeshCost) and OpenMetaverse's FacetedMesh decode it: an LLSD-binary header map (no "&lt;? LLSD/Binary ?&gt;" line)
/// giving each block's offset, counted from the end of the header, and size; then the blocks, each a zlib stream (RFC 1950)
/// of an LLSD-binary value.
/// <list type="bullet">
/// <item>high_lod, medium_lod, low_lod, lowest_lod: an array with one map per face. The lower levels aim at 50%, 25% and 6% of
/// the triangles, but never move the surface by more than 0.5%, 1% and 2% of the mesh's size, so a low-poly mesh keeps its
/// outline at a distance.</item>
/// <item>physics_convex: the mesh's bounding box as 8 BoundingVerts with Min and Max.</item>
/// <item>skin, when WriteSkin: joint_names, inverse_bind_matrix and bind_shape_matrix.</item>
/// </list>
/// Positions are written as given: OpenSim and the viewers treat them as a unit mesh that the prim's scale stretches, so the
/// caller passes positions already fitted to the part (see the remarks on <see cref="Write"/>).
/// </summary>
public static class MeshAssetWriter
{
    // The detail levels below high: the share of the triangles each aims at, and the most each may move the surface, as a share
    // of the mesh's bounding-box diagonal. The shape limit wins over the triangle goal.
    private static readonly (string Name, double Ratio, double MaxError)[] LowerLevels =
        { ("medium_lod", 0.5, 0.005), ("low_lod", 0.25, 0.01), ("lowest_lod", 0.06, 0.02) };

    private const byte EndOfInfluences = 0xFF;

    /// <summary>
    /// The asset bytes for a mesh of at most 8 faces of at most 65,535 vertices. Each face's positions and UVs are stored as
    /// 16-bit steps inside the face's own box (PositionDomain, TexCoord0Domain), normals as 16-bit steps from -1 to 1.
    /// UVs are turned from glTF's (v runs down the image) to Second Life's (v runs up): v becomes 1 - v.
    /// </summary>
    /// <remarks>
    /// The viewer and OpenSim's physics read positions as a mesh of unit size, then apply the prim's scale; a part's mesh must
    /// therefore be fitted into -0.5..0.5 on each axis by the caller, with the prim scaled to the part's size.
    /// </remarks>
    public static byte[] Write(ImportMesh high, MeshAssetOptions options)
    {
        if (high.Faces.Count == 0) throw new ArgumentException("A mesh asset needs at least one face.", nameof(high));
        if (options.WriteSkin && options.Skeleton == null)
            throw new ArgumentException("WriteSkin needs the skeleton.", nameof(options));
        bool weights = options.WriteSkin;

        var blocks = new List<(string Name, byte[] Bytes)>
        {
            ("high_lod", Compress(Faces(high, weights))),
        };
        foreach (var (name, ratio, maxError) in LowerLevels)
            blocks.Add((name, Compress(Faces(Simplifier.Simplify(high, ratio, maxError), weights))));
        blocks.Add(("physics_convex", Compress(BoundingBox(high))));
        if (options.WriteSkin) blocks.Add(("skin", Compress(Skin(options.Skeleton!))));

        var header = new OSDMap();
        int offset = 0;
        foreach (var (name, bytes) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = OSD.FromInteger(offset), ["size"] = OSD.FromInteger(bytes.Length) };
            offset += bytes.Length;
        }

        using var asset = new MemoryStream();
        var headerBytes = OSDParser.SerializeLLSDBinary(header, false);
        asset.Write(headerBytes, 0, headerBytes.Length);
        foreach (var (_, bytes) in blocks) asset.Write(bytes, 0, bytes.Length);
        return asset.ToArray();
    }

    private static byte[] Compress(OSD value)
    {
        var raw = OSDParser.SerializeLLSDBinary(value, false);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static OSDArray Faces(ImportMesh mesh, bool weights)
    {
        var faces = new OSDArray();
        foreach (var face in mesh.Faces) faces.Add(Face(face, weights));
        return faces;
    }

    private static OSDMap Face(ImportFace face, bool writeWeights)
    {
        if (face.Positions.Length == 0 || face.Positions.Length > 65_535)
            throw new ArgumentException($"A face needs 1 to 65,535 vertices, not {face.Positions.Length}.");

        var min = face.Positions.Aggregate(Vector3.Min);
        var max = face.Positions.Aggregate(Vector3.Max);
        var map = new OSDMap
        {
            ["Position"] = OSD.FromBinary(Quantise3(face.Positions, min, max)),
            ["PositionDomain"] = Domain(min, max),
            ["Normal"] = OSD.FromBinary(Quantise3(face.Normals.Select(n => Vector3.Clamp(n, -Vector3.One, Vector3.One)).ToArray(), -Vector3.One, Vector3.One)),
        };

        if (face.Uv0 != null)
        {
            var uv = face.Uv0.Select(t => new Vector2(t.X, 1 - t.Y)).ToArray();
            var uvMin = uv.Aggregate(Vector2.Min);
            var uvMax = uv.Aggregate(Vector2.Max);
            var bytes = new byte[uv.Length * 4];
            for (int i = 0; i < uv.Length; i++)
            {
                Put(bytes, i * 4, Step(uv[i].X, uvMin.X, uvMax.X));
                Put(bytes, i * 4 + 2, Step(uv[i].Y, uvMin.Y, uvMax.Y));
            }
            map["TexCoord0"] = OSD.FromBinary(bytes);
            map["TexCoord0Domain"] = new OSDMap
            {
                ["Min"] = OSD.FromVector2(new OmvVector2(uvMin.X, uvMin.Y)),
                ["Max"] = OSD.FromVector2(new OmvVector2(uvMax.X, uvMax.Y)),
            };
        }

        var triangles = new byte[face.Triangles.Length * 2];
        for (int i = 0; i < face.Triangles.Length; i++) Put(triangles, i * 2, (ushort)face.Triangles[i]);
        map["TriangleList"] = OSD.FromBinary(triangles);

        if (writeWeights && face.Weights != null) map["Weights"] = OSD.FromBinary(Weights(face.Weights, face.Positions.Length));
        return map;
    }

    /// <summary>
    /// The Second Life weight list: per vertex, up to four influences, each a joint index byte then the weight as a uint16 LE
    /// share of 65535; fewer than four end with joint 0xFF. Zero weights are left out, a joint named twice is merged, and the
    /// shares are rounded so each vertex's sum is exactly 65535. A vertex with no weight goes wholly to its first joint.
    /// </summary>
    private static byte[] Weights(ImportWeights weights, int vertices)
    {
        var bytes = new List<byte>(vertices * 13);
        var joints = new List<int>(4);
        var shares = new List<double>(4);
        var steps = new int[4];
        for (int v = 0; v < vertices; v++)
        {
            joints.Clear();
            shares.Clear();
            for (int k = 0; k < 4; k++)
            {
                int joint = weights.Joints[v * 4 + k];
                float w = weights.Weights[v * 4 + k];
                if (!(w > 0) || float.IsInfinity(w)) continue;
                int at = joints.IndexOf(joint);
                if (at >= 0) shares[at] += w;
                else { joints.Add(joint); shares.Add(w); }
            }
            if (joints.Count == 0) { joints.Add(weights.Joints[v * 4]); shares.Add(1); }

            // Largest remainder: floor every share, then hand the missing steps to the largest remainders (earlier slot on a tie).
            double total = shares.Sum();
            int given = 0;
            for (int i = 0; i < joints.Count; i++)
            {
                steps[i] = (int)Math.Floor(shares[i] / total * 65535);
                given += steps[i];
            }
            var byRemainder = Enumerable.Range(0, joints.Count)
                .OrderByDescending(i => shares[i] / total * 65535 - steps[i]).ThenBy(i => i).ToArray();
            for (int i = 0; given < 65535; i = (i + 1) % byRemainder.Length, given++) steps[byRemainder[i]]++;

            int written = 0;
            for (int i = 0; i < joints.Count; i++)
            {
                if (steps[i] == 0) continue;
                if (joints[i] >= EndOfInfluences)
                    throw new ArgumentException($"Joint {joints[i]} cannot be written: a mesh asset has at most 255 joints.");
                bytes.Add((byte)joints[i]);
                bytes.Add((byte)(steps[i] & 0xFF));
                bytes.Add((byte)(steps[i] >> 8));
                written++;
            }
            if (written < 4) bytes.Add(EndOfInfluences);
        }
        return bytes.ToArray();
    }

    // physics_convex with no hulls: the box's 8 corners, as 16-bit steps between Min and Max.
    private static OSDMap BoundingBox(ImportMesh mesh)
    {
        var all = mesh.Faces.SelectMany(f => f.Positions).ToArray();
        var min = all.Aggregate(Vector3.Min);
        var max = all.Aggregate(Vector3.Max);
        var corners = new byte[8 * 6];
        for (int c = 0; c < 8; c++)
        {
            Put(corners, c * 6, (c & 1) != 0 ? ushort.MaxValue : (ushort)0);
            Put(corners, c * 6 + 2, (c & 2) != 0 ? ushort.MaxValue : (ushort)0);
            Put(corners, c * 6 + 4, (c & 4) != 0 ? ushort.MaxValue : (ushort)0);
        }
        var map = Domain(min, max);
        map["BoundingVerts"] = OSD.FromBinary(corners);
        return map;
    }

    // Matrices are written row by row (M11, M12, ... M44), which is the viewer's LLMatrix4 order: translation in the last row.
    private static OSDMap Skin(ImportSkeleton skeleton)
    {
        var names = new OSDArray();
        var inverse = new OSDArray();
        foreach (var joint in skeleton.Joints)
        {
            names.Add(OSD.FromString(joint.Name));
            inverse.Add(Matrix(joint.InverseBind));
        }
        return new OSDMap
        {
            ["joint_names"] = names,
            ["inverse_bind_matrix"] = inverse,
            ["bind_shape_matrix"] = Matrix(skeleton.BindShape),
        };
    }

    private static OSDArray Matrix(Matrix4x4 m)
    {
        var values = new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
        var array = new OSDArray();
        foreach (var v in values) array.Add(OSD.FromReal(v));
        return array;
    }

    private static OSDMap Domain(Vector3 min, Vector3 max) => new()
    {
        ["Min"] = OSD.FromVector3(new OmvVector3(min.X, min.Y, min.Z)),
        ["Max"] = OSD.FromVector3(new OmvVector3(max.X, max.Y, max.Z)),
    };

    private static byte[] Quantise3(Vector3[] values, Vector3 min, Vector3 max)
    {
        var bytes = new byte[values.Length * 6];
        for (int i = 0; i < values.Length; i++)
        {
            Put(bytes, i * 6, Step(values[i].X, min.X, max.X));
            Put(bytes, i * 6 + 2, Step(values[i].Y, min.Y, max.Y));
            Put(bytes, i * 6 + 4, Step(values[i].Z, min.Z, max.Z));
        }
        return bytes;
    }

    // The nearest of the 65,536 steps from min to max; a flat domain (max = min) is all step 0.
    private static ushort Step(float value, float min, float max)
    {
        double range = (double)max - min;
        if (!(range > 0)) return 0;
        var step = Math.Round((value - (double)min) / range * 65535.0);
        return (ushort)Math.Clamp(step, 0, 65535);
    }

    private static void Put(byte[] bytes, int at, ushort value)
    {
        bytes[at] = (byte)(value & 0xFF);
        bytes[at + 1] = (byte)(value >> 8);
    }
}
