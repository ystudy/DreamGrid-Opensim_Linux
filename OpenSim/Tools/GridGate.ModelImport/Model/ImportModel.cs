using System.Collections.Generic;
using System.Numerics;

namespace GridGate.ModelImport.Model;

// The middle model: what the reader produces and later stages check and write. Plain records, no OpenSim types.
// Positions are metres, Z-up, in the model's own frame.

public sealed record ImportModel(IReadOnlyList<ImportPart> Parts, int RootPart, IReadOnlyList<ImportMaterial> Materials,
    IReadOnlyList<ImportImage> Images, ImportSkeleton? Skeleton, IReadOnlyList<string> Dropped);

public sealed record ImportPart(string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale, ImportMesh Mesh);

public sealed record ImportMesh(IReadOnlyList<ImportFace> Faces);

/// <summary>One material's triangles. Uv0 is as glTF has it, (0, 0) at the image's top left; the mesh writer turns v for the grid.</summary>
public sealed record ImportFace(Vector3[] Positions, Vector3[] Normals, Vector2[]? Uv0, int[] Triangles, int Material, ImportWeights? Weights);

/// <summary>Four joints and four weights per vertex.</summary>
public sealed record ImportWeights(ushort[] Joints, float[] Weights);

public enum AlphaMode { Opaque, Mask, Blend }

public sealed record ImportMaterial(string Name, Vector4 BaseColour, int? BaseColourImage, AlphaMode Alpha, float AlphaCutoff, bool DoubleSided);

public sealed record ImportImage(string Name, string MimeType, byte[] Bytes);

/// <summary>
/// The first glTF skin. Joints are in the skin's order, so the joint indices in <see cref="ImportWeights"/> point into this list.
/// Matrices use System.Numerics' row-vector convention (a point is transformed as p * M; translation in M41..M43).
/// BindShape is identity: a skinned face's vertices are used as they are, in their part's own frame. For a glTF skin that is
/// the mesh node's local frame, still with glTF's axes (Y up); the part's Rotation carries the turn to Z-up (Fox and Cesium Man
/// read with a 90 degree part rotation).
/// Neither the joints nor BindShape are adjusted for the user's scale or for the checker's negative-scale bake: the part's Scale
/// carries those. No rigged item is uploaded yet; re-binding for wearables belongs to the rigged-mesh project.
/// </summary>
public sealed record ImportSkeleton(IReadOnlyList<ImportJoint> Joints, Matrix4x4 BindShape);

/// <summary>
/// One joint. Parent is the index of its parent joint, or -1.
/// Rest: for a joint whose parent is a joint, its glTF local matrix relative to that parent (glTF axes, metres), so its world
/// matrix is Rest * the parent's world matrix. For a joint with no joint parent, its glTF world matrix followed by the Y-up to
/// Z-up turn: its world matrix in the Z-up scene (the turn is applied once, at the top of each chain).
/// InverseBind: the glTF inverse bind matrix preceded by the Z-up to Y-up turn, so it takes a point of the Z-up scene at bind
/// time into the joint's own frame.
/// </summary>
public sealed record ImportJoint(string Name, int Parent, Matrix4x4 Rest, Matrix4x4 InverseBind);
