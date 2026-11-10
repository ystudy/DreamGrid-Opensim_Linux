using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;

namespace GridGate.ModelImport.Checking;

/// <summary>Brings a part within the grid's mesh format: at most 65,535 vertices in a face and 8 faces in a mesh.</summary>
public static class FaceSplitter
{
    public const int MaxVerticesPerFace = 65_535;
    public const int MaxFacesPerMesh = 8;

    /// <summary>
    /// The part as one or more parts that fit the format. A face over the vertex limit is cut along triangle boundaries into several
    /// faces, each copying the vertices (and weights) its triangles use. A mesh over 8 faces is then cut into parts with the same
    /// placement, named "name 2", "name 3" and so on; the first keeps the part's own name.
    /// </summary>
    public static IReadOnlyList<ImportPart> Split(ImportPart part)
    {
        var faces = part.Mesh.Faces.SelectMany(SplitFace).ToList();
        if (faces.Count <= MaxFacesPerMesh)
            return new[] { part with { Mesh = new ImportMesh(faces) } };

        var parts = new List<ImportPart>();
        for (int start = 0; start < faces.Count; start += MaxFacesPerMesh)
        {
            var name = start == 0 ? part.Name : part.Name + " " + (parts.Count + 1);
            parts.Add(part with { Name = name, Mesh = new ImportMesh(faces.GetRange(start, Math.Min(MaxFacesPerMesh, faces.Count - start))) });
        }
        return parts;
    }

    /// <summary>The faces a face becomes: itself when it fits, otherwise pieces of at most 65,535 vertices.</summary>
    internal static IReadOnlyList<ImportFace> SplitFace(ImportFace face)
    {
        if (face.Positions.Length <= MaxVerticesPerFace) return new[] { face };

        var pieces = new List<ImportFace>();
        var newIndex = new int[face.Positions.Length];
        Array.Fill(newIndex, -1);
        var used = new List<int>();
        var triangles = new List<int>();

        void Flush()
        {
            if (triangles.Count > 0) pieces.Add(Piece(face, used, triangles));
            foreach (var old in used) newIndex[old] = -1;
            used.Clear();
            triangles.Clear();
        }

        int Take(int old)
        {
            if (newIndex[old] < 0)
            {
                newIndex[old] = used.Count;
                used.Add(old);
            }
            return newIndex[old];
        }

        for (int t = 0; t + 2 < face.Triangles.Length; t += 3)
        {
            int a = face.Triangles[t], b = face.Triangles[t + 1], c = face.Triangles[t + 2];
            int needed = (newIndex[a] < 0 ? 1 : 0) + (newIndex[b] < 0 && b != a ? 1 : 0)
                + (newIndex[c] < 0 && c != a && c != b ? 1 : 0);
            if (used.Count + needed > MaxVerticesPerFace) Flush();
            triangles.Add(Take(a));
            triangles.Add(Take(b));
            triangles.Add(Take(c));
        }
        Flush();
        return pieces;
    }

    // A face made of the listed old vertices, in that order, with triangles already numbered for it.
    private static ImportFace Piece(ImportFace face, List<int> used, List<int> triangles)
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
        return new ImportFace(positions, normals, uv, triangles.ToArray(), face.Material,
            joints != null ? new ImportWeights(joints, weights!) : null);
    }
}
