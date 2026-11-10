using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using GridGate.ModelImport.Model;
using SharpGLTF.Schema2;
using AlphaMode = GridGate.ModelImport.Model.AlphaMode;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GridGate.ModelImport.Tests")]

namespace GridGate.ModelImport.Reading;

public sealed record ReadResult(ImportModel? Model, IReadOnlyList<Finding> Findings);

/// <summary>
/// Reads a glTF or GLB into the middle model: one part per node with a mesh, one face per triangle primitive.
/// Everything is converted from glTF's Y-up to Z-up. The file is only ever read from the stream it is given;
/// a reference to an outside file is refused before anything is read.
/// </summary>
public static class GltfReader
{
    // glTF Y-up to Z-up: a +90 degree turn about X, (x, y, z) -> (x, -z, y). System.Numerics matrices act on row vectors,
    // so a node's Z-up world matrix is its glTF world matrix followed by this turn.
    private static readonly Matrix4x4 YUpToZUp = Matrix4x4.CreateRotationX(MathF.PI / 2f);
    private static readonly Matrix4x4 ZUpToYUp = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    private static readonly string[] CompressionExtensions = { "KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_meshopt_compression" };

    public static ReadResult Read(Stream input, string fileName, ImportLimits limits) => Read(input, fileName, limits, null);

    // onSatelliteRequest is a test seam: it is told when SharpGLTF asks for a file outside the stream. That must never happen,
    // because external uris are refused before SharpGLTF reads anything; the request itself always fails.
    internal static ReadResult Read(Stream input, string fileName, ImportLimits limits, Action<string>? onSatelliteRequest)
    {
        // 1. The upload cap, before anything is parsed. At most one chunk more than the cap is read.
        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += n;
                if (total > limits.MaxUploadBytes)
                    return Blocked(Refusal("too-large-file", ("what", "upload"), ("limit", Num(limits.MaxUploadBytes))));
                buffer.Write(chunk, 0, n);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception e)
        {
            return Blocked(Unreadable(e.Message));
        }

        // 2. The JSON chunk only: extensions, uris and the node count are decided without building anything.
        try
        {
            using var json = JsonDocument.Parse(JsonChunk(bytes));
            var refused = CheckJson(json.RootElement, limits);
            if (refused != null) return Blocked(refused);
        }
        catch (Exception e)
        {
            return Blocked(Unreadable(e.Message));
        }

        // 3. The full read. SharpGLTF keeps images as bytes; it does not decode them.
        ModelRoot root;
        try
        {
            var context = ReadContext.Create(uri =>
            {
                onSatelliteRequest?.Invoke(uri);
                throw new IOException("External file references are not read: " + uri);
            });
            root = context.ReadSchema2(new MemoryStream(bytes, false));
        }
        catch (Exception e)
        {
            return Blocked(Unreadable(e.Message));
        }

        try
        {
            // 4. Image headers, before any image is decoded.
            foreach (var image in root.LogicalImages)
            {
                var content = image.Content;
                if (!content.IsValid) continue;
                if (ImageHeader.Size(content.Content.Span) is { } size && (size.Width > limits.MaxImageSide || size.Height > limits.MaxImageSide))
                    return Blocked(Refusal("too-large-file", ("what", "image"), ("image", size.Width + "x" + size.Height), ("limit", Num(limits.MaxImageSide))));
            }

            // 5. Triangle count, from accessor counts, before any geometry is read. A mesh used by several nodes counts for each.
            var scene = RootScene(root);
            long triangles = 0;
            int primitives = 0;
            foreach (var node in Walk(scene))
            {
                if (node.Mesh == null) continue;
                foreach (var prim in node.Mesh.Primitives)
                {
                    primitives++;
                    triangles += TriangleCount(prim);
                    if (triangles > limits.MaxTriangles)
                        return Blocked(Refusal("too-large-file", ("what", "triangles"), ("limit", Num(limits.MaxTriangles))));
                }
            }
            if (primitives == 0) return Blocked(Unreadable("The file has no mesh."));

            return new ReadResult(Convert(root, scene), Array.Empty<Finding>());
        }
        catch (Exception e)
        {
            return Blocked(Unreadable(e.Message));
        }
    }

    private static ReadResult Blocked(Finding f) => new(null, new[] { f });

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static Finding Refusal(string code, params (string Key, string Value)[] values) =>
        new(code, FindingKind.Blocking, values.ToDictionary(v => v.Key, v => v.Value));

    private static Finding Unreadable(string why) => Refusal("bad-file", ("reason", why));

    private static Scene RootScene(ModelRoot root) =>
        root.DefaultScene ?? root.LogicalScenes.FirstOrDefault() ?? throw new InvalidDataException("The file has no scene.");

    // The JSON text of a .gltf, or of a .glb's first chunk. Throws when it is neither.
    private static ReadOnlyMemory<byte> JsonChunk(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F')
        {
            // 12-byte header, then the first chunk: length, type ("JSON" = 0x4E4F534A), data.
            if (bytes.Length < 20) throw new InvalidDataException("The binary glTF header is cut short.");
            var length = BitConverter.ToUInt32(bytes, 12);
            var type = BitConverter.ToUInt32(bytes, 16);
            if (type != 0x4E4F534A) throw new InvalidDataException("The binary glTF has no JSON chunk first.");
            if (length > bytes.Length - 20) throw new InvalidDataException("The binary glTF JSON chunk is cut short.");
            return new ReadOnlyMemory<byte>(bytes, 20, (int)length);
        }
        return bytes;
    }

    // Compression, outside files and the node count, from the JSON alone. Null when nothing is refused.
    private static Finding? CheckJson(JsonElement root, ImportLimits limits)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The file is not a glTF.");

        foreach (var list in new[] { "extensionsUsed", "extensionsRequired" })
            if (root.TryGetProperty(list, out var ext) && ext.ValueKind == JsonValueKind.Array)
                foreach (var e in ext.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && Array.IndexOf(CompressionExtensions, e.GetString()) >= 0)
                        return Refusal("compressed", ("extension", e.GetString()!));

        // Every buffer and image must be a data: uri; a .glb's own buffer and its images in the buffer have no uri at all.
        int external = 0;
        foreach (var list in new[] { "buffers", "images" })
            if (root.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out var uri)
                        && !(uri.ValueKind == JsonValueKind.String && uri.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                        external++;
        if (external > 0) return Refusal("external-files", ("count", Num(external)));

        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() > limits.MaxNodes)
            return Refusal("too-large-file", ("what", "nodes"), ("limit", Num(limits.MaxNodes)));

        return null;
    }

    // Triangles a primitive gives, from counts only. Lines and points give none.
    private static long TriangleCount(MeshPrimitive prim)
    {
        long n = prim.IndexAccessor != null ? prim.IndexAccessor.Count : (prim.GetVertexAccessor("POSITION")?.Count ?? 0);
        return prim.DrawPrimitiveType switch
        {
            PrimitiveType.TRIANGLES => n / 3,
            PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN => Math.Max(0, n - 2),
            _ => 0,
        };
    }

    private static ImportModel Convert(ModelRoot root, Scene scene)
    {
        var dropped = new List<string>();
        var materials = new MaterialTable(root);
        var skin = root.LogicalSkins.FirstOrDefault();
        var parts = new List<ImportPart>();
        bool morphs = false, cameras = false, lines = false, points = false;

        foreach (var node in Walk(scene))
        {
            if (node.Camera != null) cameras = true;
            var mesh = node.Mesh;
            if (mesh == null) continue;

            var faces = new List<ImportFace>();
            foreach (var prim in mesh.Primitives)
            {
                if (prim.MorphTargetsCount > 0) morphs = true;
                switch (prim.DrawPrimitiveType)
                {
                    case PrimitiveType.POINTS:
                        points = true;
                        break;
                    case PrimitiveType.LINES:
                    case PrimitiveType.LINE_LOOP:
                    case PrimitiveType.LINE_STRIP:
                        lines = true;
                        break;
                    default: // TRIANGLES, TRIANGLE_STRIP, TRIANGLE_FAN: all become triangle lists
                        var face = ReadFace(prim, materials);
                        if (face != null) faces.Add(face);
                        break;
                }
            }
            if (faces.Count == 0) continue;

            var world = node.WorldMatrix * YUpToZUp;
            var name = string.IsNullOrEmpty(node.Name) ? (string.IsNullOrEmpty(mesh.Name) ? "Part" + (parts.Count + 1) : mesh.Name) : node.Name;
            if (DecomposesCleanly(world, out var scale, out var rotation, out var translation))
            {
                parts.Add(new ImportPart(name, translation, rotation, scale, new ImportMesh(faces)));
            }
            else
            {
                // The matrix has shear (a rotated child under a non-uniformly scaled parent) and cannot be a position, rotation and
                // scale. Its effect is put into the vertices instead, and the part gets the identity placement.
                var baked = faces.Select(f => Bake(f, world)).ToList();
                parts.Add(new ImportPart(name, Vector3.Zero, Quaternion.Identity, Vector3.One, new ImportMesh(baked)));
            }
        }

        if (root.LogicalAnimations.Count > 0) dropped.Add("animation");
        if (morphs) dropped.Add("morphs");
        if (cameras) dropped.Add("cameras");
        if (UsesLights(root)) dropped.Add("lights");
        if (lines) dropped.Add("lines");
        if (points) dropped.Add("points");

        return new ImportModel(parts, 0, materials.Materials, materials.Images, skin != null ? ReadSkeleton(skin) : null, dropped);
    }

    // True when scale, then rotation, then position put back the same matrix. System.Numerics' Decompose does not notice shear, so
    // the result is rebuilt and compared. A negative scale that decomposes cleanly is kept as a negative Scale component.
    private static bool DecomposesCleanly(Matrix4x4 world, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        if (Matrix4x4.Decompose(world, out scale, out rotation, out translation))
        {
            var back = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            if (Near(back, world)) return true;
        }
        scale = Vector3.One;
        rotation = Quaternion.Identity;
        translation = Vector3.Zero;
        return false;
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        static bool Close(float x, float y) => MathF.Abs(x - y) <= 1e-4f * MathF.Max(1f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
        return Close(a.M11, b.M11) && Close(a.M12, b.M12) && Close(a.M13, b.M13) && Close(a.M14, b.M14)
            && Close(a.M21, b.M21) && Close(a.M22, b.M22) && Close(a.M23, b.M23) && Close(a.M24, b.M24)
            && Close(a.M31, b.M31) && Close(a.M32, b.M32) && Close(a.M33, b.M33) && Close(a.M34, b.M34)
            && Close(a.M41, b.M41) && Close(a.M42, b.M42) && Close(a.M43, b.M43) && Close(a.M44, b.M44);
    }

    // The face with the matrix applied to its positions and normals (normals by the inverse transpose). A matrix that mirrors
    // (negative determinant) also reverses the winding, so the face still points outwards.
    private static ImportFace Bake(ImportFace face, Matrix4x4 m)
    {
        var positions = face.Positions.Select(p => Vector3.Transform(p, m)).ToArray();
        var normalMatrix = Matrix4x4.Invert(m, out var inverse) ? Matrix4x4.Transpose(inverse) : m;
        var normals = face.Normals.Select(n =>
        {
            var t = Vector3.TransformNormal(n, normalMatrix);
            return t.LengthSquared() > 0 ? Vector3.Normalize(t) : Vector3.UnitZ;
        }).ToArray();
        var triangles = face.Triangles;
        if (m.GetDeterminant() < 0)
        {
            triangles = (int[])triangles.Clone();
            for (int i = 0; i + 2 < triangles.Length; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
        }
        return face with { Positions = positions, Normals = normals, Triangles = triangles };
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

        // Triangle lists, strips and fans all come out as a list of triangles (a trailing partial triangle is dropped).
        var tris = prim.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
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
            // The colour is the channel's Vector4 parameter (RGBA).
            var colour = channel?.Parameters.Select(p => p.Value).OfType<Vector4>().FirstOrDefault() ?? Vector4.One;
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
