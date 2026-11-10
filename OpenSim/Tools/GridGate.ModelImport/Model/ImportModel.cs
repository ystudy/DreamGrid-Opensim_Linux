using System.Collections.Generic;
using System.Numerics;

namespace GridGate.ModelImport.Model;

// The middle model: what the reader produces and later stages check and write. Plain records, no OpenSim types.
// Positions are metres, Z-up, in the model's own frame.

public sealed record ImportModel(IReadOnlyList<ImportPart> Parts, int RootPart, IReadOnlyList<ImportMaterial> Materials,
    IReadOnlyList<ImportImage> Images, ImportSkeleton? Skeleton, IReadOnlyList<string> Dropped);

public sealed record ImportPart(string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale, ImportMesh Mesh);

public sealed record ImportMesh(IReadOnlyList<ImportFace> Faces);

public sealed record ImportFace(Vector3[] Positions, Vector3[] Normals, Vector2[]? Uv0, int[] Triangles, int Material, ImportWeights? Weights);

/// <summary>Four joints and four weights per vertex.</summary>
public sealed record ImportWeights(ushort[] Joints, float[] Weights);

public enum AlphaMode { Opaque, Mask, Blend }

public sealed record ImportMaterial(string Name, Vector4 BaseColour, int? BaseColourImage, AlphaMode Alpha, float AlphaCutoff, bool DoubleSided);

public sealed record ImportImage(string Name, string MimeType, byte[] Bytes);

public sealed record ImportSkeleton(IReadOnlyList<ImportJoint> Joints, Matrix4x4 BindShape);

public sealed record ImportJoint(string Name, int Parent, Matrix4x4 Rest, Matrix4x4 InverseBind);
