# rapier-bench

This is a standalone Rapier benchmark that rebuilds the `pile` and `kick` physics test scenes from the bench's
`make_physoar.py`. It uses ubODE's step settings. The results are in
[`../2026-11-06-rapier-early-results.md`](../2026-11-06-rapier-early-results.md).

```bash
cargo build --release                      # single-threaded
cargo build --release --features parallel  # multi-threaded (rayon)
./target/release/rbench pile 500           # SCENE = pile | kick, COUNT = number of prims
./target/release/rbench kick 1000 --nosleep
```

Each run prints one CSV line for each measurement window (0–20 s and 30–90 s of simulated time):

```
engine,tag,window_s,frames,phys_p50_ms,phys_p95_ms,phys_p99_ms,phys_max_ms,phys_mean_ms,awake_at_end
```

This is test code only: it isn't built or shipped with DreamGrid.
