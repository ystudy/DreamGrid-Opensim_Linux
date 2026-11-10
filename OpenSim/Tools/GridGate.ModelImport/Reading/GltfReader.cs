using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;
using SharpGLTF.Schema2;
using AlphaMode = GridGate.ModelImport.Model.AlphaMode;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace GridGate.ModelImport.Reading;

public sealed record ReadResult(ImportModel? Model, IReadOnlyList<Finding> Findings);

/// <summary>
/// Reads a glTF or GLB into the middle model: one part per node with a mesh, one face per triangle primitive.
/// Everything is converted from glTF's Y-up to Z-up. The file is only ever read from the stream it is given;
/// a reference to an outside file fails the read instead of opening it.
/// </summary>
public static class GltfReader
{
    // glTF Y-up to Z-up: a +90 degree turn about X, (x, y, z) -> (x, -z, y). System.Numerics matrices act on row vectors,
    // so a node's Z-up world matrix is its glTF world matrix followed by this turn.
    private static readonly Matrix4x4 YUpToZUp = Matrix4x4.CreateRotationX(MathF.PI / 2f);
    private static readonly Matrix4x4 ZUpToYUp = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    public static ReadResult Read(Stream input, string fileName, ImportLimits limits)
    {
        ModelRoot root;
        try
        {
            var context = ReadContext.Create(uri => throw new IOException("External file references are not read: " + uri));
            root = context.ReadSchema2(input);
        }
        catch (Exception e)
        {
            return new ReadResult(null, new[] { Unreadable(e.Message) });
        }

        try
        {
            return new ReadResult(Convert(root), Array.Empty<Finding>());
        }
        catch (Exception e)
        {
            return new ReadResult(null, new[] { Unreadable(e.Message) });
        }
    }

    private static Finding Unreadable(string why) =>
        new("file-unreadable", FindingKind.Blocking, new Dictionary<string, string> { ["reason"] = why });

    private static ImportModel Convert(ModelRoot root)
    {
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException("The file has no scene.");

        var dropped = new List<string>();
        var materials = new MaterialTable(root);
        var skin = root.LogicalSkins.FirstOrDefault();
        var parts = new List<ImportPart>();
        bool morphs = false, cameras = false;

        foreach (var node in Walk(scene))
        {
            if (node.Camera != null) cameras = true;
            var mesh = node.Mesh;
            if (mesh == null) continue;

            var faces = new List<ImportFace>();
            foreach (var prim in mesh.Primitives)
            {
                if (prim.MorphTargetsCount > 0) morphs = true;
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                var face = ReadFace(prim, materials);
                if (face != null) faces.Add(face);
            }
            if (faces.Count == 0) continue;

            var world = node.WorldMatrix * YUpToZUp;
            if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var translation))
            {
                scale = Vector3.One;
                rotation = Quaternion.Identity;
                translation = world.Translation;
            }
            var name = string.IsNullOrEmpty(node.Name) ? (string.IsNullOrEmpty(mesh.Name) ? "Part" + (parts.Count + 1) : mesh.Name) : node.Name;
            parts.Add(new ImportPart(name, translation, rotation, scale, new ImportMesh(faces)));
        }

        if (root.LogicalAnimations.Count > 0) dropped.Add("animation");
        if (morphs) dropped.Add("morphs");
        if (cameras) dropped.Add("cameras");
        if (UsesLights(root)) dropped.Add("lights");

        return new ImportModel(parts, 0, materials.Materials, materials.Images, skin != null ? ReadSkeleton(skin) : null, dropped);
    }

    private static bool UsesLights(ModelRoot root) =>
        root.ExtensionsUsed.Contains("KHR_lights_punctual") && root.LogicalPunctualLights.Count > 0;

    // The scene's nodes, parents before children.
    private static IEnumerable<Node> Walk(Scene scene)
    {
        var stack = new Stack<Node>(scene.VisualChildren.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.VisualChildren.Reverse()) stack.Push(c);
        }
    }

    private static ImportFace? ReadFace(MeshPrimitive prim, MaterialTable materials)
    {
        var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
        if (positions == null || positions.Count == 0) return null;
        var pos = positions.ToArray();

        var tris = prim.IndexAccessor != null
            ? prim.IndexAccessor.AsIndicesArray().Select(i => (int)i).ToArray()
            : Enumerable.Range(0, pos.Length).ToArray();
        // A trailing partial triangle is not a triangle.
        if (tris.Length % 3 != 0) Array.Resize(ref tris, tris.Length - tris.Length % 3);
        if (tris.Length == 0) return null;

        var normalsAccessor = prim.GetVertexAccessor("NORMAL");
        var normals = normalsAccessor != null ? normalsAccessor.AsVector3Array().ToArray() : SmoothNormals(pos, tris);
        var uv = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array()?.ToArray();

        ImportWeights? weights = null;
        var jointsAcc = prim.GetVertexAccessor("JOINTS_0");
        var weightsAcc = prim.GetVertexAccessor("WEIGHTS_0");
        if (jointsAcc != null && weightsAcc != null)
        {
            var j = jointsAcc.AsVector4Array();
            var w = weightsAcc.AsVector4Array();
            var joints = new ushort[pos.Length * 4];
            var wts = new float[pos.Length * 4];
            for (int i = 0; i < pos.Length; i++)
            {
                var jv = j[i];
                var wv = w[i];
                joints[i * 4] = (ushort)jv.X; joints[i * 4 + 1] = (ushort)jv.Y; joints[i * 4 + 2] = (ushort)jv.Z; joints[i * 4 + 3] = (ushort)jv.W;
                wts[i * 4] = wv.X; wts[i * 4 + 1] = wv.Y; wts[i * 4 + 2] = wv.Z; wts[i * 4 + 3] = wv.W;
            }
            weights = new ImportWeights(joints, wts);
        }

        return new ImportFace(pos, normals, uv, tris, materials.IndexOf(prim.Material), weights);
    }

    private static Vector3[] SmoothNormals(Vector3[] pos, int[] tris)
    {
        var n = new Vector3[pos.Length];
        for (int t = 0; t < tris.Length; t += 3)
        {
            var a = tris[t]; var b = tris[t + 1]; var c = tris[t + 2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); // length is twice the area, so larger faces weigh more
            n[a] += fn; n[b] += fn; n[c] += fn;
        }
        for (int i = 0; i < n.Length; i++)
            n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    // The first skin. Joint order is the skin's, so JOINTS_0 indices stay valid. A joint's Rest is its local matrix, except a joint
    // with no joint parent, which takes its world matrix turned to Z-up (the turn is applied once, at the top of each chain).
    // InverseBind takes Z-up world positions to joint space. BindShape is identity: skinned vertices are in glTF's bind frame.
    private static ImportSkeleton ReadSkeleton(Skin skin)
    {
        var nodes = Enumerable.Range(0, skin.JointsCount).Select(i => skin.GetJoint(i)).ToArray();
        var index = new Dictionary<Node, int>();
        for (int i = 0; i < nodes.Length; i++) index[nodes[i].Joint] = i;

        var joints = new List<ImportJoint>();
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i].Joint;
            var parent = node.VisualParent != null && index.TryGetValue(node.VisualParent, out var p) ? p : -1;
            var rest = parent >= 0 ? node.LocalMatrix : node.WorldMatrix * YUpToZUp;
            joints.Add(new ImportJoint(node.Name ?? "Joint" + i, parent, rest, ZUpToYUp * nodes[i].InverseBindMatrix));
        }
        return new ImportSkeleton(joints, Matrix4x4.Identity);
    }

    /// <summary>The materials the faces use (a default one for primitives with none) and the base colour images they point at.</summary>
    private sealed class MaterialTable
    {
        private readonly List<ImportMaterial> _materials = new();
        private readonly List<ImportImage> _images = new();
        private readonly Dictionary<Image, int> _imageIndex = new();
        private readonly Dictionary<Material, int> _materialIndex = new();
        private int _default = -1;

        public MaterialTable(ModelRoot root)
        {
            foreach (var m in root.LogicalMaterials) _materialIndex[m] = Add(m);
        }

        public IReadOnlyList<ImportMaterial> Materials => _materials;
        public IReadOnlyList<ImportImage> Images => _images;

        public int IndexOf(Material? m)
        {
            if (m != null) return _materialIndex[m];
            if (_default < 0)
            {
                _default = _materials.Count;
                _materials.Add(new ImportMaterial("default", Vector4.One, null, AlphaMode.Opaque, 0.5f, false));
            }
            return _default;
        }

        private int Add(Material m)
        {
            var channel = m.FindChannel("BaseColor");
            var colour = channel?.Parameter ?? Vector4.One;
            int? image = null;
            var img = channel?.Texture?.PrimaryImage;
            if (img != null) image = ImageIndex(img);
            var alpha = m.Alpha switch
            {
                GltfAlphaMode.MASK => AlphaMode.Mask,
                GltfAlphaMode.BLEND => AlphaMode.Blend,
                _ => AlphaMode.Opaque,
            };
            _materials.Add(new ImportMaterial(m.Name ?? "Material" + _materials.Count, colour, image, alpha, m.AlphaCutoff, m.DoubleSided));
            return _materials.Count - 1;
        }

        private int? ImageIndex(Image img)
        {
            if (_imageIndex.TryGetValue(img, out var i)) return i;
            var content = img.Content;
            if (!content.IsValid) return null;
            var mime = content.IsPng ? "image/png" : content.IsJpg ? "image/jpeg" : content.MimeType;
            _images.Add(new ImportImage(img.Name ?? "Image" + _images.Count, mime, content.Content.ToArray()));
            return _imageIndex[img] = _images.Count - 1;
        }
    }
}
