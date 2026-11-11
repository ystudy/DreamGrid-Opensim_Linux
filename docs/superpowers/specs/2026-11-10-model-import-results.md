# Model import proof of concept: results

Design: `2026-11-10-model-import-design.md`. Plan: `../plans/2026-11-10-model-import.md`. Branch `feature/model-import`.

## Outcome

**Pass.** On the Debian 12 test VM, with Firestorm, the owner reported on 2026-11-11: uploads load, the report warns of the
problems it should, and the items "look great in world".

## What was checked

- **Build and package:** release builds of e2816df (run 42944995906) and bc15d3f (run 42957892973) passed: Linux and Windows
  packages, smoke tests on Debian 12, Debian 13 and Windows.
- **Converter on Linux, from the package** (before installing): Box, BoxTextured and Avocado converted in 0.6–0.9 s each.
  Textures came out as JPEG2000, so `System.Drawing` and the OpenJPEG encoder load on Linux. Avocado's 2048 px texture was
  shrunk to 1024 with a risky finding, as designed.
- **Installed on the VM:** the converter, SharpGLTF and the Linux encoder are staged in the runtime folder, the uploads folder
  exists, and the upload page sends someone not signed in to sign in.
- **In Firestorm (the owner):** Box, BoxTextured and Fox at first, then the rest of the test list. Textures are the right way
  up (the V flip is correct), colours and placement are right, and the reports warn as expected.

## Problem found and fixed

**Parts vanished at a distance.** A few metres away the box lost a side and the fox lost its legs. Each lower detail level kept a
fixed share of the triangles (50%, 25%, 6%) however much shape that cost: the box's lowest level was one triangle, and the fox's
low level moved its surface by 17% of its size. Fix (bc15d3fd): each level also has a shape limit, 0.5%, 1% and 2% of the mesh's
size, that wins over the triangle goal; collapses are ranked by mean distance and each is checked against the real distance from
every original point it folds away. Low-poly models now keep their triangles; dense ones are still reduced. The owner confirmed
it is "much better".

## Tests

- Converter: 142 tests, all passing (including a 500,000-triangle timing test).
- Launcher: 3,273 passing, 0 failing, 10 skipped (Linux-only tests on Windows) at the final-review fixes.

## Left for later

From the design's non-goals and the final review's deferred list:

- PBR materials (normal, metal-roughness, emissive maps); proper physics hulls for Rapier; the 3D preview; payment.
- A hard native-memory cap on the converter process (only the .NET heap is capped now); streaming large asset uploads to
  Robust.
- Live inventory update (today the guest logs in again to see the item).
- The detail-level limits (0.5/1/2%) and the `heavy-mesh` threshold (200,000 triangles) can be tuned further with more models.
- Phase 2: rigged heads and bodies from VRM.
