using System.Numerics;
using GridGate.ModelImport.Model;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class MeshPartsTests
{
    private static ImportFace Face(Vector3[] positions, int[]? triangles = null) =>
        new(positions, new Vector3[positions.Length], null, triangles ?? new int[0], 0, null);

    [Fact]
    public void Bounds_of_no_vertices_is_zero_and_none()
    {
        Assert.Equal((Vector3.Zero, Vector3.Zero, false), MeshParts.Bounds(new ImportFace[0]));
        Assert.Equal((Vector3.Zero, Vector3.Zero, false), MeshParts.Bounds(new[] { Face(new Vector3[0]) }));
    }

    [Fact]
    public void Bounds_span_every_face()
    {
        var a = Face(new[] { new Vector3(0, 0, 0), new Vector3(1, 1, 0) });
        var b = Face(new[] { new Vector3(-1, 2, 3) });
        var (lo, hi, any) = MeshParts.Bounds(new[] { a, b });
        Assert.Equal(new Vector3(-1, 0, 0), lo);
        Assert.Equal(new Vector3(1, 2, 3), hi);
        Assert.True(any);
    }

    private static ImportFace FourVertices(bool withUvAndWeights)
    {
        var p = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(3, 0, 0) };
        var n = new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, -1) };
        if (!withUvAndWeights) return new ImportFace(p, n, null, new[] { 0, 1, 2 }, 5, null);
        var uv = new[] { new Vector2(0, 0), new Vector2(0.1f, 0.2f), new Vector2(0.3f, 0.4f), new Vector2(0.5f, 0.6f) };
        var joints = new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var weights = new float[] { 1, 0, 0, 0, .5f, .5f, 0, 0, .25f, .25f, .25f, .25f, .1f, .2f, .3f, .4f };
        return new ImportFace(p, n, uv, new[] { 0, 1, 2 }, 5, new ImportWeights(joints, weights));
    }

    [Fact]
    public void Piece_keeps_the_listed_vertices_in_order()
    {
        var face = FourVertices(true);
        var piece = MeshParts.Piece(face, new[] { 3, 1 }, new[] { 0, 1, 0 });
        Assert.Equal(new[] { face.Positions[3], face.Positions[1] }, piece.Positions);
        Assert.Equal(new[] { face.Normals[3], face.Normals[1] }, piece.Normals);
        Assert.Equal(new[] { face.Uv0![3], face.Uv0[1] }, piece.Uv0);
        Assert.Equal(new ushort[] { 12, 13, 14, 15, 4, 5, 6, 7 }, piece.Weights!.Joints);
        Assert.Equal(new[] { .1f, .2f, .3f, .4f, .5f, .5f, 0, 0 }, piece.Weights.Weights);
        Assert.Equal(new[] { 0, 1, 0 }, piece.Triangles);
        Assert.Equal(5, piece.Material);
    }

    [Fact]
    public void Piece_of_a_face_without_uvs_or_weights_has_none()
    {
        var piece = MeshParts.Piece(FourVertices(false), new[] { 0, 2 }, new[] { 0, 1, 0 });
        Assert.Null(piece.Uv0);
        Assert.Null(piece.Weights);
    }
}
