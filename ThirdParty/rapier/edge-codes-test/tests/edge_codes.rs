// The edge-codes patch must give the same contact constraints as Parry's own pseudo-normals.
//
// A mesh built with FIX_INTERNAL_EDGES alone uses the patch's edge codes; the same mesh built with
// FIX_INTERNAL_EDGES | ORIENTED keeps Parry's full pseudo-normals (the unpatched code path). Both
// merge vertices the same way, so triangle i is the same triangle in both, and
// `triangle_normal_constraints(i)` (what contact generation reads) is compared between them.
use parry3d::math::{Pose, Vector};
use parry3d::shape::{TriMesh, TriMeshFlags, TrianglePseudoNormals};
use std::f32::consts::PI;

type Mesh = (Vec<Vector>, Vec<[u32; 3]>);

/// A 10-bit angle over [-90, 90] degrees is within half a step (0.088 degrees) of the exact one.
const TOLERANCE_DEG: f32 = 0.12;

/// n x n unit tiles in the XY plane, four vertices of their own per tile (as meshers emit them), normals +Z.
fn tiles(n: usize, h: impl Fn(f32, f32) -> f32) -> Mesh {
    let mut v = Vec::new();
    let mut t = Vec::new();
    for i in 0..n {
        for j in 0..n {
            let (x0, y0) = (i as f32, j as f32);
            let b = v.len() as u32;
            for (x, y) in [(x0, y0), (x0 + 1.0, y0), (x0 + 1.0, y0 + 1.0), (x0, y0 + 1.0)] {
                v.push(Vector::new(x, y, h(x, y)));
            }
            t.push([b, b + 1, b + 2]);
            t.push([b, b + 2, b + 3]);
        }
    }
    (v, t)
}

/// A sculpt-style sphere: a lat-long grid whose pole rows collapse to (nearly) one point, giving degenerate
/// triangles and slivers.
fn sculpt_sphere(n: usize) -> Mesh {
    let mut v = Vec::new();
    let mut t = Vec::new();
    for i in 0..=n {
        for j in 0..=n {
            let th = PI * i as f32 / n as f32;
            let ph = if j == n { 0.0 } else { 2.0 * PI * j as f32 / n as f32 };
            v.push(Vector::new(th.sin() * ph.cos(), th.sin() * ph.sin(), th.cos()) * 2.0);
        }
    }
    let w = (n + 1) as u32;
    for i in 0..n as u32 {
        for j in 0..n as u32 {
            let a = i * w + j;
            t.push([a, a + w, a + 1]);
            t.push([a + 1, a + w, a + w + 1]);
        }
    }
    (v, t)
}

/// A cube with s x s tiles per face, wound outward: flat seams on the faces, 90-degree creases between them.
fn tiled_cube(s: usize) -> Mesh {
    let mut v = Vec::new();
    let mut t = Vec::new();
    for (n, u, w) in [
        (Vector::X, Vector::Y, Vector::Z),
        (-Vector::X, Vector::Z, Vector::Y),
        (Vector::Y, Vector::Z, Vector::X),
        (-Vector::Y, Vector::X, Vector::Z),
        (Vector::Z, Vector::X, Vector::Y),
        (-Vector::Z, Vector::Y, Vector::X),
    ] {
        for i in 0..s {
            for j in 0..s {
                let p = |a: usize, b: usize| {
                    n + u * (2.0 * a as f32 / s as f32 - 1.0) + w * (2.0 * b as f32 / s as f32 - 1.0)
                };
                let b0 = v.len() as u32;
                v.extend([p(i, j), p(i + 1, j), p(i + 1, j + 1), p(i, j + 1)]);
                if u.cross(w).dot(n) > 0.0 {
                    t.extend([[b0, b0 + 1, b0 + 2], [b0, b0 + 2, b0 + 3]]);
                } else {
                    t.extend([[b0, b0 + 2, b0 + 1], [b0, b0 + 3, b0 + 2]]);
                }
            }
        }
    }
    (v, t)
}

fn angle_deg(a: Vector, b: Vector) -> f32 {
    // atan2 rather than acos: acos loses all precision near 0 in f32.
    a.cross(b).length().atan2(a.dot(b)).to_degrees()
}

fn fix(m: &Mesh) -> TriMesh {
    TriMesh::with_flags(m.0.clone(), m.1.clone(), TriMeshFlags::FIX_INTERNAL_EDGES).unwrap()
}

fn reference(m: &Mesh) -> TriMesh {
    TriMesh::with_flags(m.0.clone(), m.1.clone(), TriMeshFlags::FIX_INTERNAL_EDGES | TriMeshFlags::ORIENTED).unwrap()
}

/// Compares every triangle's constraints. Edges whose reference pseudo-normal is more than `skip_folded_deg` from
/// the face normal are skipped: there two nearly opposite faces cancel and Parry's own value is rounding noise.
/// Returns the worst edge difference in degrees.
fn compare(codes: &TriMesh, full: &TriMesh, skip_folded_deg: f32) -> f32 {
    assert_eq!(codes.indices(), full.indices(), "both builds must merge the same way");
    let mut worst = 0.0f32;
    for i in 0..codes.num_triangles() as u32 {
        let (c, f) = (codes.triangle_normal_constraints(i), full.triangle_normal_constraints(i));
        assert_eq!(c.is_some(), f.is_some(), "triangle {i}: constraints present in one build only");
        let (Some(c), Some(f)): (Option<TrianglePseudoNormals>, Option<TrianglePseudoNormals>) = (c, f) else {
            continue;
        };
        assert!(angle_deg(c.face, f.face) < 1.0e-3, "triangle {i}: face normals differ");
        for k in 0..3 {
            if angle_deg(f.edges[k], f.face) > skip_folded_deg {
                continue;
            }
            // Parry's projection ignores the order of the edges (and `reverse` does not keep it), so take the
            // closest of the three.
            let d = (0..3).map(|l| angle_deg(c.edges[l], f.edges[k])).fold(180.0f32, f32::min);
            assert!(d <= TOLERANCE_DEG, "triangle {i} edge {k}: {d} degrees from Parry's pseudo-normal");
            worst = worst.max(d);
        }
    }
    worst
}

#[test]
fn flat_tiles_give_exactly_the_face_normal() {
    let m = tiles(16, |_, _| 0.0);
    let codes = fix(&m);
    compare(&codes, &reference(&m), 90.0);
    for i in 0..codes.num_triangles() as u32 {
        let c = codes.triangle_normal_constraints(i).unwrap();
        // A flat seam or a boundary edge: the cone is the face normal alone, as in Parry.
        assert_eq!(c.edges, [c.face; 3], "triangle {i}");
    }
}

#[test]
fn curved_mesh_matches_parry() {
    let m = tiles(40, |x, y| 0.4 * ((x * 0.37).sin() + (y * 0.29).cos()) + 0.05 * (x * 7.3 + y * 3.1).sin());
    let worst = compare(&fix(&m), &reference(&m), 90.0);
    assert!(worst > 0.0, "a curved mesh should have non-zero edge angles");
    let codes = fix(&m);
    assert!(
        (0..codes.num_triangles() as u32).any(|i| {
            let c = codes.triangle_normal_constraints(i).unwrap();
            c.edges.iter().any(|e| *e != c.face)
        }),
        "a curved mesh should have edge pseudo-normals off the face normal"
    );
}

#[test]
fn creases_match_parry() {
    let cube = tiled_cube(4);
    compare(&fix(&cube), &reference(&cube), 90.0);
    // A 30-degree ridge and a 30-degree valley across a tiled sheet.
    let ridge = tiles(8, |x, _| (4.0 - (x - 4.0).abs()) * (15.0f32).to_radians().tan());
    compare(&fix(&ridge), &reference(&ridge), 90.0);
    let valley = tiles(8, |x, _| (x - 4.0).abs() * (15.0f32).to_radians().tan());
    compare(&fix(&valley), &reference(&valley), 90.0);
}

#[test]
fn slivers_and_degenerate_triangles_match_parry() {
    // Sculpt-style poles: degenerate triangles (no normal, no constraints in either build) and slivers.
    let sphere = sculpt_sphere(32);
    compare(&fix(&sphere), &reference(&sphere), 80.0);

    // An explicit zero-area triangle and a needle sliver next to ordinary ones.
    let (mut v, mut t) = tiles(2, |_, _| 0.0);
    let b = v.len() as u32;
    v.extend([Vector::new(0.0, 0.0, 0.0), Vector::new(1.0, 0.0, 0.0), Vector::new(2.0, 0.0, 0.0)]);
    t.push([b, b + 1, b + 2]);
    v.push(Vector::new(1.0, -1.0e-4, 0.0));
    t.push([b, b + 3, b + 1]);
    let m = (v, t);
    compare(&fix(&m), &reference(&m), 80.0);
}

#[test]
fn opposite_neighbours_give_no_constraints_as_in_parry() {
    // Half the triangles wound backwards: a sum of opposite normals cannot be normalised.
    let (v, mut t) = tiles(4, |_, _| 0.0);
    for (q, tri) in t.iter_mut().enumerate() {
        if q % 2 == 1 {
            tri.swap(1, 2);
        }
    }
    let m = (v, t);
    let codes = fix(&m);
    compare(&codes, &reference(&m), 90.0);
    assert!((0..codes.num_triangles() as u32).any(|i| codes.triangle_normal_constraints(i).is_none()));
}

#[test]
fn edge_data_costs_at_most_4_bytes_per_triangle() {
    let m = tiles(32, |x, y| 0.2 * (x * 0.3).sin() * (y * 0.2).cos());
    let plain = TriMesh::with_flags(m.0.clone(), m.1.clone(), TriMeshFlags::DELETE_DEGENERATE_TRIANGLES).unwrap();
    let codes = fix(&m);
    assert_eq!(plain.num_triangles(), codes.num_triangles());
    let extra = (codes.heap_memory_size() - plain.heap_memory_size()) as f64 / codes.num_triangles() as f64;
    assert!(extra <= 4.5, "the internal-edge data costs {extra} B per triangle");
    assert!(codes.pseudo_normals().is_none());
}

#[test]
fn merging_alone_computes_no_pseudo_normals() {
    let m = tiles(4, |_, _| 0.0);
    let merged = TriMesh::with_flags(m.0, m.1, TriMeshFlags::MERGE_DUPLICATE_VERTICES).unwrap();
    assert!(merged.pseudo_normals().is_none());
}

#[test]
fn codes_follow_transforms_scaling_and_reversal() {
    let m = tiles(12, |x, y| 0.3 * (x * 0.5).sin() + 0.2 * (y * 0.4).cos());

    // A rigid transform leaves the codes valid.
    let pose = Pose::new(Vector::new(3.0, -2.0, 1.0), Vector::new(0.3, -0.7, 1.1));
    let mut moved = fix(&m);
    moved.transform_vertices(&pose);
    let mut moved_ref = reference(&m);
    moved_ref.transform_vertices(&pose);
    compare(&moved, &moved_ref, 90.0);

    // A non-uniform scale changes the dihedral angles: the codes must be recomputed.
    let scale = Vector::new(1.0, 1.0, 4.0);
    let scaled = fix(&m).scaled(scale);
    let fresh = (m.0.iter().map(|p| *p * scale).collect::<Vec<_>>(), m.1.clone());
    compare(&scaled, &reference(&fresh), 90.0);

    // Reversed winding: normals and cones flip.
    let mut reversed = fix(&m);
    reversed.reverse();
    let mut reversed_ref = reference(&m);
    reversed_ref.reverse();
    compare(&reversed, &reversed_ref, 90.0);
}
