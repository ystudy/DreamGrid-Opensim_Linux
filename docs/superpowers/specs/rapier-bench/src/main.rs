//! Standalone Rapier bench that mirrors the ubODE pile/kick scenes from make_physoar.py.
//! rbench SCENE COUNT [--nosleep]   SCENE = pile | kick
//! World is Y-up (Rapier cylinders are Y-axis). Region terrain is a flat 256x256 heightfield.
//! Each OpenSim frame (0.0909 s) runs ubODE-style 0.02 s steps through an accumulator, and the
//! time spent stepping is recorded per frame. Windows match physrun.py: 0-20 s and 30-90 s.
use rapier3d::prelude::*;
use rapier3d::geometry::Array2;
macro_rules! v3 { ($x:expr, $y:expr, $z:expr) => { Vector::new($x, $y, $z) } }
use std::time::Instant;

const FRAME: f32 = 0.0909;
const STEP: f32 = 0.02;
const GROUND: f32 = 21.0;

struct Rng(u64);
impl Rng {
    fn f(&mut self) -> f32 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        (self.0 >> 40) as f32 / (1u64 << 24) as f32
    }
    fn range(&mut self, a: f32, b: f32) -> f32 { a + (b - a) * self.f() }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let scene = args[1].as_str();
    let count: usize = args[2].parse().unwrap();
    let nosleep = args.iter().any(|a| a == "--nosleep");
    let mut rng = Rng(0x9e3779b97f4a7c15);

    let mut bodies = RigidBodySet::new();
    let mut colliders = ColliderSet::new();
    let hf = ColliderBuilder::heightfield(Array2::repeat(257, 257, GROUND), v3![256.0, 1.0, 256.0])
        .translation(v3![128.0, 0.0, 128.0])
        .friction(0.6)
        .build();
    colliders.insert(hf);

    let shape = |i: usize| match i % 3 {
        0 => ColliderBuilder::cuboid(0.25, 0.25, 0.25),
        1 => ColliderBuilder::ball(0.25),
        _ => ColliderBuilder::cylinder(0.25, 0.25),
    };
    let mut handles = Vec::with_capacity(count);
    let mut next_kick = Vec::with_capacity(count);
    for i in 0..count {
        let (x, y, z) = if scene == "pile" {
            let side = 12;
            let (layer, cell) = (i / (side * side), i % (side * side));
            (128.0 - side as f32 * 0.3 + (cell % side) as f32 * 0.6 + rng.range(-0.05, 0.05),
             128.0 - side as f32 * 0.3 + (cell / side) as f32 * 0.6 + rng.range(-0.05, 0.05),
             30.0 + layer as f32 * 0.8)
        } else {
            let grid = ((count as f32).sqrt() + 0.999) as usize;
            let step = 220.0 / grid as f32;
            (18.0 + (i % grid) as f32 * step, 18.0 + (i / grid) as f32 * step, 30.0)
        };
        let mut rb = RigidBodyBuilder::dynamic().translation(v3![x, z, y]).linear_damping(0.001).angular_damping(0.002);
        if nosleep { rb = rb.can_sleep(false); }
        let h = bodies.insert(rb.build());
        colliders.insert_with_parent(shape(i).density(10.0).friction(0.6).build(), h, &mut bodies);
        handles.push(h);
        next_kick.push(rng.range(2.0, 4.0));
    }

    let gravity = v3![0.0, -9.8, 0.0];
    let mut params = IntegrationParameters::default();
    params.dt = STEP;
    let mut pipeline = PhysicsPipeline::new();
    let mut islands = IslandManager::new();
    let mut broad = DefaultBroadPhase::new();
    let mut narrow = NarrowPhase::new();
    let mut ij = ImpulseJointSet::new();
    let mut mj = MultibodyJointSet::new();
    let mut ccd = CCDSolver::new();
    let mut soft = SoftBodySet::new();

    let mut sim_t = 0.0f32;
    let mut acc = 0.0f32;
    let mut samples: Vec<(f32, f64)> = Vec::new();
    while sim_t < 90.0 {
        let t0 = Instant::now();
        if scene == "kick" {
            for (i, h) in handles.iter().enumerate() {
                if sim_t >= next_kick[i] {
                    let rb = &mut bodies[*h];
                    let m = rb.mass();
                    rb.apply_impulse(v3![rng.range(-2.0, 2.0), rng.range(3.0, 5.0), rng.range(-2.0, 2.0)] * m, true);
                    rb.apply_torque_impulse(v3![rng.f(), rng.f(), rng.f()] * m, true);
                    next_kick[i] = sim_t + rng.range(2.0, 4.0);
                }
            }
        }
        acc += FRAME;
        while acc > STEP * 0.5 {
            pipeline.step(gravity, &params, &mut islands, &mut broad, &mut narrow, &mut bodies, &mut colliders,
                          &mut ij, &mut mj, &mut soft, &mut ccd, &(), &());
            acc -= STEP;
        }
        samples.push((sim_t, t0.elapsed().as_secs_f64() * 1000.0));
        sim_t += FRAME;
    }
    let awake = islands.active_bodies().count();
    for (a, b) in [(0.0f32, 20.0f32), (30.0, 90.0)] {
        let mut v: Vec<f64> = samples.iter().filter(|s| s.0 >= a && s.0 < b).map(|s| s.1).collect();
        v.sort_by(|x, y| x.partial_cmp(y).unwrap());
        let p = |q: f64| v[((q * v.len() as f64) as usize).min(v.len() - 1)];
        let mean = v.iter().sum::<f64>() / v.len() as f64;
        println!("rapier,{scene}-{count}{},{a}-{b},{},{:.2},{:.2},{:.2},{:.2},{:.2},{awake}",
                 if nosleep { "-nosleep" } else { "" }, v.len(), p(0.5), p(0.95), p(0.99), v[v.len() - 1], mean);
    }
}
