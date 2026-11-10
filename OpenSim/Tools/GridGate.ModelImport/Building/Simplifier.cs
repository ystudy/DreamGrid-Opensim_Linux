using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridGate.ModelImport.Model;

namespace GridGate.ModelImport.Building;

/// <summary>
/// Makes the lower detail levels: quadric-error edge collapse (Garland and Heckbert, 1997), pure C# and deterministic.
/// Each face is simplified on its own, so material boundaries never move. A collapse moves one vertex onto a neighbour
/// (a half-edge collapse), so every kept vertex is an original vertex with its own normal, UV and weights. A UV seam or an open
/// edge is a border in the face's index list (the vertices there are duplicated); a border vertex only slides along the border,
/// and border edges carry an extra plane that holds the border's shape. A vertex on an edge shared by more than two triangles
/// never moves. A face never disappears: if it would, it keeps its original smallest triangle.
/// </summary>
public static class Simplifier
{
    // How strongly a border keeps its shape compared with the surface itself.
    private const double BorderWeight = 10.0;

    /// <summary>The mesh with about ratio of each face's triangles (at least one each). A ratio of 1 or more returns the mesh.</summary>
    public static ImportMesh Simplify(ImportMesh mesh, double ratio)
    {
        if (ratio >= 1) return mesh;
        return new ImportMesh(mesh.Faces.Select(f => SimplifyFace(f, ratio)).ToArray());
    }

    private static ImportFace SimplifyFace(ImportFace face, double ratio)
    {
        int triangles = face.Triangles.Length / 3;
        int target = Math.Max(1, (int)Math.Round(triangles * ratio));
        if (target >= triangles) return face;

        var collapse = new FaceCollapse(face);
        collapse.Run(target);
        var kept = collapse.KeptTriangles();
        if (kept.Count == 0) kept = new List<int>(SmallestTriangle(face));
        return Compact(face, kept);
    }

    private static int[] SmallestTriangle(ImportFace face)
    {
        int best = 0;
        float bestArea = float.MaxValue;
        var p = face.Positions;
        var t = face.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            var area = Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]).LengthSquared();
            if (area < bestArea) { bestArea = area; best = i; }
        }
        return new[] { t[best], t[best + 1], t[best + 2] };
    }

    // A face made of the given triangles, keeping only the vertices they use, in their original order.
    private static ImportFace Compact(ImportFace face, List<int> triangles)
    {
        var map = new int[face.Positions.Length];
        Array.Fill(map, -1);
        foreach (var v in triangles) map[v] = 0;
        var used = new List<int>();
        for (int v = 0; v < map.Length; v++)
            if (map[v] == 0) { map[v] = used.Count; used.Add(v); }

        var positions = used.Select(v => face.Positions[v]).ToArray();
        var normals = used.Select(v => face.Normals[v]).ToArray();
        var uv = face.Uv0 != null ? used.Select(v => face.Uv0[v]).ToArray() : null;
        ImportWeights? weights = null;
        if (face.Weights != null)
        {
            var joints = new ushort[used.Count * 4];
            var w = new float[used.Count * 4];
            for (int i = 0; i < used.Count; i++)
            {
                Array.Copy(face.Weights.Joints, used[i] * 4, joints, i * 4, 4);
                Array.Copy(face.Weights.Weights, used[i] * 4, w, i * 4, 4);
            }
            weights = new ImportWeights(joints, w);
        }
        return new ImportFace(positions, normals, uv, triangles.Select(v => map[v]).ToArray(), face.Material, weights);
    }

    /// <summary>The collapse state for one face.</summary>
    private sealed class FaceCollapse
    {
        private readonly double[] _x, _y, _z;      // positions
        private readonly int[] _tris;              // triangle corners, rewritten as vertices collapse
        private readonly bool[] _triDead;
        private readonly List<int>?[] _vertexTris; // alive triangles around each vertex
        private readonly double[] _q;              // 10 quadric coefficients per vertex
        private readonly int[] _version;           // bumped whenever a vertex's best collapse may have changed
        private readonly bool[] _gone;             // collapsed into a neighbour
        private readonly bool[] _locked;           // on an edge shared by more than two triangles
        // Each vertex's cheapest collapse; ties go to the lower vertex index, so the order never depends on anything else.
        private readonly PriorityQueue<(int Vertex, int Version, int Target), (double Cost, int Vertex)> _queue = new();
        private int _alive;

        // Scratch lists for neighbour scans: (neighbour, alive triangles shared with the vertex).
        private readonly List<(int Vertex, int Count)> _around = new();
        private readonly List<(int Vertex, int Count)> _aroundOther = new();

        public FaceCollapse(ImportFace face)
        {
            int n = face.Positions.Length;
            _x = new double[n]; _y = new double[n]; _z = new double[n];
            for (int i = 0; i < n; i++) { _x[i] = face.Positions[i].X; _y[i] = face.Positions[i].Y; _z[i] = face.Positions[i].Z; }
            _tris = (int[])face.Triangles.Clone();
            int triangleCount = _tris.Length / 3;
            _triDead = new bool[triangleCount];
            _alive = triangleCount;
            _vertexTris = new List<int>?[n];
            _q = new double[n * 10];
            _version = new int[n];
            _gone = new bool[n];
            _locked = new bool[n];

            for (int t = 0; t < triangleCount; t++)
                for (int k = 0; k < 3; k++)
                    (_vertexTris[_tris[t * 3 + k]] ??= new List<int>(6)).Add(t);

            for (int t = 0; t < triangleCount; t++)
            {
                // An edge of three or more triangles locks its ends.
                for (int k = 0; k < 3; k++)
                {
                    int u = _tris[t * 3 + k], v = _tris[t * 3 + (k + 1) % 3];
                    if (SharedTriangles(u, v) > 2) { _locked[u] = true; _locked[v] = true; }
                }

                var (nx, ny, nz, len) = Normal(_tris[t * 3], _tris[t * 3 + 1], _tris[t * 3 + 2]);
                if (len <= 0) continue;
                nx /= len; ny /= len; nz /= len;

                // The triangle's plane, weighted by its area, goes to its corners.
                int a = _tris[t * 3];
                double d = -(nx * _x[a] + ny * _y[a] + nz * _z[a]);
                for (int k = 0; k < 3; k++) AddPlane(_tris[t * 3 + k], nx, ny, nz, d, len / 2);

                // A border edge (one triangle) also gets the plane through it at right angles to the triangle, weighted by its
                // length squared.
                for (int k = 0; k < 3; k++)
                {
                    int u = _tris[t * 3 + k], v = _tris[t * 3 + (k + 1) % 3];
                    if (SharedTriangles(u, v) != 1) continue;
                    double ex = _x[v] - _x[u], ey = _y[v] - _y[u], ez = _z[v] - _z[u];
                    double px = ey * nz - ez * ny, py = ez * nx - ex * nz, pz = ex * ny - ey * nx;
                    double plen = Math.Sqrt(px * px + py * py + pz * pz);
                    if (plen <= 0) continue;
                    px /= plen; py /= plen; pz /= plen;
                    double pd = -(px * _x[u] + py * _y[u] + pz * _z[u]);
                    double weight = BorderWeight * (ex * ex + ey * ey + ez * ez);
                    AddPlane(u, px, py, pz, pd, weight);
                    AddPlane(v, px, py, pz, pd, weight);
                }
            }

            for (int v = 0; v < n; v++) Offer(v, check: false);
        }

        public void Run(int target)
        {
            while (_alive > target && _queue.TryDequeue(out var entry, out _))
            {
                var (x, version, y) = entry;
                if (_gone[x] || _version[x] != version) continue;
                if (CanCollapse(x, y)) Collapse(x, y);
                // The cheapest neighbour would fold or pinch the surface; offer the cheapest one that does not.
                else Offer(x, check: true);
            }
        }

        public List<int> KeptTriangles()
        {
            var kept = new List<int>(_alive * 3);
            for (int t = 0; t < _triDead.Length; t++)
                if (!_triDead[t]) { kept.Add(_tris[t * 3]); kept.Add(_tris[t * 3 + 1]); kept.Add(_tris[t * 3 + 2]); }
            return kept;
        }

        private (double X, double Y, double Z, double Length) Normal(int a, int b, int c) => NormalAt(a, b, c, -1, 0, 0, 0);

        // The triangle's normal (not unit length) with the corner moved, if it is one, placed at (mx, my, mz).
        private (double X, double Y, double Z, double Length) NormalAt(int a, int b, int c, int moved, double mx, double my, double mz)
        {
            double ax = a == moved ? mx : _x[a], ay = a == moved ? my : _y[a], az = a == moved ? mz : _z[a];
            double bx = b == moved ? mx : _x[b], by = b == moved ? my : _y[b], bz = b == moved ? mz : _z[b];
            double cx = c == moved ? mx : _x[c], cy = c == moved ? my : _y[c], cz = c == moved ? mz : _z[c];
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            return (nx, ny, nz, Math.Sqrt(nx * nx + ny * ny + nz * nz));
        }

        private void AddPlane(int v, double a, double b, double c, double d, double w)
        {
            int i = v * 10;
            _q[i] += w * a * a; _q[i + 1] += w * a * b; _q[i + 2] += w * a * c; _q[i + 3] += w * a * d;
            _q[i + 4] += w * b * b; _q[i + 5] += w * b * c; _q[i + 6] += w * b * d;
            _q[i + 7] += w * c * c; _q[i + 8] += w * c * d;
            _q[i + 9] += w * d * d;
        }

        // The error of moving x onto y: both quadrics, at y's position.
        private double Cost(int x, int y)
        {
            double px = _x[y], py = _y[y], pz = _z[y];
            int i = x * 10, j = y * 10;
            double a2 = _q[i] + _q[j], ab = _q[i + 1] + _q[j + 1], ac = _q[i + 2] + _q[j + 2], ad = _q[i + 3] + _q[j + 3];
            double b2 = _q[i + 4] + _q[j + 4], bc = _q[i + 5] + _q[j + 5], bd = _q[i + 6] + _q[j + 6];
            double c2 = _q[i + 7] + _q[j + 7], cd = _q[i + 8] + _q[j + 8], d2 = _q[i + 9] + _q[j + 9];
            double e = a2 * px * px + 2 * ab * px * py + 2 * ac * px * pz + 2 * ad * px
                     + b2 * py * py + 2 * bc * py * pz + 2 * bd * py
                     + c2 * pz * pz + 2 * cd * pz + d2;
            return double.IsNaN(e) ? double.MaxValue : Math.Max(0, e);
        }

        private bool Contains(int t, int v) => _tris[t * 3] == v || _tris[t * 3 + 1] == v || _tris[t * 3 + 2] == v;

        private int SharedTriangles(int u, int v)
        {
            int count = 0;
            foreach (var t in _vertexTris[u]!) if (Contains(t, v)) count++;
            return count;
        }

        // The vertex's neighbours, each with the number of alive triangles it shares with the vertex, in first-seen order.
        private void Around(int v, List<(int Vertex, int Count)> into)
        {
            into.Clear();
            var tris = _vertexTris[v];
            if (tris == null) return;
            foreach (var t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int o = _tris[t * 3 + k];
                    if (o == v) continue;
                    int at = -1;
                    for (int i = 0; i < into.Count; i++) if (into[i].Vertex == o) { at = i; break; }
                    if (at < 0) into.Add((o, 1));
                    else into[at] = (o, into[at].Count + 1);
                }
        }

        // Queues x's cheapest collapse onto a neighbour (only along a border edge when x is on a border). With check, only a
        // collapse that CanCollapse allows is queued. Nothing is queued for a vertex that must stay.
        private void Offer(int x, bool check)
        {
            if (_gone[x] || _locked[x]) return;
            Around(x, _around);
            if (_around.Count == 0) return;
            bool border = false;
            foreach (var (_, count) in _around)
            {
                if (count > 2) return;
                if (count == 1) border = true;
            }

            // CanCollapse reuses the scratch lists, so a checked offer works on a copy.
            var candidates = check ? _around.ToArray() : null;
            int length = candidates?.Length ?? _around.Count;
            int best = -1;
            double bestCost = double.MaxValue;
            for (int i = 0; i < length; i++)
            {
                var (y, count) = candidates != null ? candidates[i] : _around[i];
                if (border && count != 1) continue;
                var cost = Cost(x, y);
                if (best >= 0 && (cost > bestCost || (cost == bestCost && y > best))) continue;
                if (check && !CanCollapse(x, y)) continue;
                best = y;
                bestCost = cost;
            }
            if (best >= 0) _queue.Enqueue((x, _version[x], best), (bestCost, x));
        }

        // Refused when the two vertices' neighbourhoods meet anywhere but the triangles on the edge (the link condition, which
        // keeps the surface from pinching), or when a triangle would turn over or shrink to nothing.
        private bool CanCollapse(int x, int y)
        {
            if (_gone[y]) return false;
            Around(x, _around);
            Around(y, _aroundOther);
            int shared = 0;
            foreach (var (v, _) in _around)
                foreach (var (w, _) in _aroundOther)
                    if (v == w) { shared++; break; }
            int both = 0;
            foreach (var t in _vertexTris[x]!) if (Contains(t, y)) both++;
            if (both == 0 || shared != both) return false;

            foreach (var t in _vertexTris[x]!)
            {
                if (Contains(t, y)) continue;
                int a = _tris[t * 3], b = _tris[t * 3 + 1], c = _tris[t * 3 + 2];
                var before = Normal(a, b, c);
                if (before.Length <= 0) continue;
                var after = NormalAt(a, b, c, x, _x[y], _y[y], _z[y]);
                if (after.Length <= 0) return false;
                if (before.X * after.X + before.Y * after.Y + before.Z * after.Z <= 0) return false;
            }
            return true;
        }

        private void Collapse(int x, int y)
        {
            for (int k = 0; k < 10; k++) _q[y * 10 + k] += _q[x * 10 + k];

            var yTris = _vertexTris[y]!;
            foreach (var t in _vertexTris[x]!)
            {
                if (Contains(t, y))
                {
                    _triDead[t] = true;
                    _alive--;
                    for (int k = 0; k < 3; k++)
                    {
                        int o = _tris[t * 3 + k];
                        if (o != x) _vertexTris[o]!.Remove(t);
                    }
                }
                else
                {
                    for (int k = 0; k < 3; k++) if (_tris[t * 3 + k] == x) _tris[t * 3 + k] = y;
                    yTris.Add(t);
                }
            }
            _gone[x] = true;
            _vertexTris[x] = null;

            // y and everything around it may now have a different cheapest collapse.
            Around(y, _around);
            var affected = new int[_around.Count + 1];
            affected[0] = y;
            for (int i = 0; i < _around.Count; i++) affected[i + 1] = _around[i].Vertex;
            foreach (var v in affected)
            {
                _version[v]++;
                Offer(v, check: false);
            }
        }
    }
}
