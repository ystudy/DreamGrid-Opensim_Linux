using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridGate.ModelImport.Model;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using NVector4 = System.Numerics.Vector4;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace GridGate.ModelImport.Building;

/// <summary>
/// Builds the object asset (asset type 6): one mesh prim per part, linked into one object, serialised the way OpenSim stores
/// objects. Modelled on BunchOfCaps.UploadCompleteHandler, which builds the object for a viewer's mesh upload.
/// </summary>
public static class ObjectAssetBuilder
{
    /// <summary>
    /// The object's XML (SceneObjectSerializer's original format). meshIds holds the mesh asset of each part, written from
    /// <see cref="UnitMesh.Fit"/>; textureIdByImage the texture of each image that could be used (an image without one leaves its
    /// faces untextured). The part with index RootPart is the root; the others follow in the model's order.
    /// The object sits where the root's prim sits in the model; the guest rezzes it wherever they like.
    /// </summary>
    public static string Build(ImportModel fixedModel, IReadOnlyList<Guid> meshIds, IReadOnlyDictionary<int, Guid> textureIdByImage,
        ImportOptions options, ImportLimits limits)
    {
        if (fixedModel.Parts.Count == 0) throw new ArgumentException("An object needs at least one part.", nameof(fixedModel));
        if (meshIds.Count != fixedModel.Parts.Count) throw new ArgumentException("One mesh id per part is needed.", nameof(meshIds));

        var owner = new UUID(options.UploaderId);
        var order = new[] { fixedModel.RootPart }.Concat(Enumerable.Range(0, fixedModel.Parts.Count).Where(i => i != fixedModel.RootPart));
        uint nextOwnerMask = NextOwnerMask(options.NextOwner);
        int now = Util.UnixTimeSinceEpoch();

        SceneObjectGroup? group = null;
        Vector3 rootPosition = Vector3.Zero;
        Quaternion rootTurnBack = Quaternion.Identity;
        foreach (var i in order)
        {
            var part = fixedModel.Parts[i];
            var place = UnitMesh.Placement(part, limits.MinPartSize);
            var position = new Vector3(place.Position.X, place.Position.Y, place.Position.Z);
            var rotation = Quaternion.Normalize(new Quaternion(part.Rotation.X, part.Rotation.Y, part.Rotation.Z, part.Rotation.W));

            int faces = part.Mesh.Faces.Count;
            var shape = PrimitiveBaseShape.CreateMesh(faces, new UUID(meshIds[i]));
            shape.TextureEntry = TextureEntry(part.Mesh, fixedModel.Materials, textureIdByImage).GetBytes(faces);

            bool isRoot = group == null;
            var prim = new SceneObjectPart(owner, shape, isRoot ? position : rootPosition, isRoot ? rotation : Quaternion.Identity, Vector3.Zero)
            {
                Scale = new Vector3(place.Scale.X, place.Scale.Y, place.Scale.Z),
                UUID = UUID.Random(),
                CreatorID = owner,
                OwnerID = owner,
                LastOwnerID = owner,
                RezzerID = owner,
                GroupID = UUID.Zero,
                CreationDate = now,
                Name = isRoot ? options.ItemName : ChildName(options.ItemName, group!.Parts.Length),
                Description = string.Empty,
                BaseMask = (uint)PermissionMask.All | (uint)PermissionMask.Export,
                OwnerMask = (uint)PermissionMask.All | (uint)PermissionMask.Export,
                GroupMask = 0,
                EveryoneMask = 0,
                NextOwnerMask = nextOwnerMask,
                Material = (byte)Material.Wood,
                PhysicsShapeType = (byte)PhysShapeType.convex,
            };

            if (group == null)
            {
                group = new SceneObjectGroup(prim) { LastOwnerID = owner, RezzerID = owner };
                rootPosition = position;
                rootTurnBack = Quaternion.Conjugate(in rotation);
            }
            else
            {
                group.AddPart(prim);
                // A child's place and turn are relative to the root's.
                prim.OffsetPosition = (position - rootPosition) * rootTurnBack;
                prim.RotationOffset = rootTurnBack * rotation;
            }
        }

        return SceneObjectSerializer.ToOriginalXmlFormat(group!);
    }

    /// <summary>
    /// The next owner's rights: move always (OpenSim takes the owner's rights down to these when the item changes hands), plus the
    /// chosen copy, modify and transfer. With none chosen the next owner gets transfer, as BunchOfCaps does for an upload.
    /// </summary>
    public static uint NextOwnerMask(NextOwner next)
    {
        var mask = PermissionMask.None;
        if (next.Copy) mask |= PermissionMask.Copy;
        if (next.Modify) mask |= PermissionMask.Modify;
        if (next.Transfer) mask |= PermissionMask.Transfer;
        if (mask == PermissionMask.None) mask = PermissionMask.Transfer;
        return (uint)(mask | PermissionMask.Move);
    }

    /// <summary>A prim name holds at most 63 characters (OpenSim cuts longer ones).</summary>
    public const int MaxPrimName = 63;

    // "{item} #{n}", with the item name cut so the whole fits in MaxPrimName. A cut never keeps half of a surrogate pair.
    private static string ChildName(string item, int n)
    {
        var suffix = " #" + n.ToString(CultureInfo.InvariantCulture);
        int keep = MaxPrimName - suffix.Length;
        if (item.Length > keep)
            item = item[..(char.IsHighSurrogate(item[keep - 1]) ? keep - 1 : keep)];
        return item + suffix;
    }

    // One face per mesh face: the material's base colour and its texture, if any. glTF's colour factor is linear and a face
    // colour is sRGB, so red, green and blue are converted; alpha is kept for blend only (opaque and mask are drawn solid: a
    // mask cuts by the picture's alpha, never by the colour's).
    private static Primitive.TextureEntry TextureEntry(ImportMesh mesh, IReadOnlyList<ImportMaterial> materials,
        IReadOnlyDictionary<int, Guid> textureIdByImage)
    {
        var te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            int m = mesh.Faces[f].Material;
            var material = m >= 0 && m < materials.Count ? materials[m] : null;
            var colour = material?.BaseColour ?? NVector4.One;
            float alpha = material?.Alpha != AlphaMode.Blend ? 1 : colour.W;

            var face = te.CreateFace((uint)f);
            face.RGBA = new Color4(Srgb(Clamp(colour.X)), Srgb(Clamp(colour.Y)), Srgb(Clamp(colour.Z)), Clamp(alpha));
            if (material?.BaseColourImage is int image && textureIdByImage.TryGetValue(image, out var texture))
                face.TextureID = new UUID(texture);
        }
        return te;
    }

    private static float Clamp(float v) => float.IsNaN(v) ? 1 : Math.Clamp(v, 0, 1);

    /// <summary>A linear colour channel (0..1) as sRGB, by the sRGB transfer function.</summary>
    internal static float Srgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
}
