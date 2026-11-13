using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridGate.ModelImport.Model;

/// <summary>The two things several stages do to a mesh's faces: find their bounding box, and copy some of a face's vertices into a new face.</summary>
public static class MeshParts
{
    /// <summary>The box around every position of every face; (zero, zero, false) when there is none.</summary>
    public static (Vector3 Lo, Vector3 Hi, bool Any) Bounds(IEnumerable<ImportFace> faces)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var f in faces)
            foreach (var v in f.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); any = true; }
        return any ? (lo, hi, true) : (Vector3.Zero, Vector3.Zero, false);
    }

    /// <summary>A face made of the listed old vertices, in that order, with UVs and weights when the face has them; triangles already numbered for it.</summary>
    public static ImportFace Piece(ImportFace face, IReadOnlyList<int> used, IReadOnlyList<int> triangles)
    {
        var positions = new Vector3[used.Count];
        var normals = new Vector3[used.Count];
        var uv = face.Uv0 != null ? new Vector2[used.Count] : null;
        var joints = face.Weights != null ? new ushort[used.Count * 4] : null;
        var weights = face.Weights != null ? new float[used.Count * 4] : null;
        for (int i = 0; i < used.Count; i++)
        {
            int old = used[i];
            positions[i] = face.Positions[old];
            normals[i] = face.Normals[old];
            if (uv != null) uv[i] = face.Uv0![old];
            if (joints != null)
            {
                Array.Copy(face.Weights!.Joints, old * 4, joints, i * 4, 4);
                Array.Copy(face.Weights.Weights, old * 4, weights!, i * 4, 4);
            }
        }
        var tri = new int[triangles.Count];
        for (int i = 0; i < tri.Length; i++) tri[i] = triangles[i];
        return new ImportFace(positions, normals, uv, tri, face.Material,
            joints != null ? new ImportWeights(joints, weights!) : null);
    }
}
