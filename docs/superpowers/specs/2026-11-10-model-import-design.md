# Model import from the guest pages: design (proof of concept)

A resident who has been given the right uploads a glTF model on the GridGate guest pages, reads a report that says what will change and what may look wrong, clicks **Make the item**, and finds a finished object in their inventory, ready to rez in Firestorm. No viewer upload window and no running region are involved.

The owner set the direction on 2026-11-10 in the session that produced this document. Their answers are listed below; choices not quoted are marked **(my call)**.

Legend, as in the other designs: **[S]** security requirement, **[X]** cross-platform note, **[?]** decision still open.

## Owner's answers (2026-11-10)
- **Library:** SharpGLTF. AssimpNet is stale and was dropped. Other formats (FBX and the rest) are not planned; people convert to glTF in Blender.
- **Order:** this project imports full items. A later, separate project adds rigged mesh heads and bodies (VRM from VRoid Studio is the preferred source). **The new import format must support that project from the start.**
- **Where:** a web upload on the GridGate guest pages. A Firestorm integration is much later and not part of this design.
- **Who:** a per-guest switch for who may upload.
- **Paying:** a hook for paying per upload. The payment itself is built later.
- **Limits:** tell the guest the limits and what is wrong, as helpfully as possible. Much of this is for learning and hobby use, so the pages teach on the way.
- **Honesty:** a guest may be paying, so before **Make the item** the page says plainly when the result may not be good. The owner's own wording: "Castle may look distorted or shaped wrong. You should correct X, Y, Z." The guest can still go ahead.
- **Preview:** the written report is enough for now. A 3D preview comes later.
- **Seeing the item:** "log out and back in" is fine for the proof of concept.
- **Grid Asset Server installs:** limits come from that computer's own regions only; hidden when it has none.
- **First a proof of concept.** The scope below is the line the owner agreed to.
- **Architecture:** the converter is a separate program that uses OpenSim's own libraries. The launcher starts it and does not load OpenSim code itself.

## Goals
- A guest gets from a `.glb` file to a rezzable item in their inventory with nothing but a web browser.
- The guest always knows, before clicking Make the item, what was changed and what may look wrong, with the grid's limit, the reason for it and a tip for fixing it in Blender.
- The middle model and the mesh writer carry skeleton and skin data now, so the heads-and-bodies project adds a mapping step, not a redesign.
- **[X]** The same on Windows and Debian 12.

## Non-goals (this proof of concept)
- The 3D preview, the payment itself, refunds.
- Rigged or wearable items. Skin data is read and survives a round-trip test, but no rigged item is offered.
- Full PBR materials (normal, metal-roughness, emissive maps). Only base colour, a base colour texture and alpha mode are carried.
- Good detail levels. The proof of concept makes simple ones; better simplification is a later item.
- Proper physics shapes for Rapier. One simple shape per part.
- Animation, morph targets (shape keys), cameras and lights in the file: dropped, with a note in the report.
- Installs that don't run their own Robust (a Region Server joined to another grid, a Connected Server). The upload page is hidden there **(my call: writing assets into another grid isn't ours to do)**.
- Formats other than glTF 2.0.

## Anti-goals (failure even if it technically works)
- A guest pays for an item that looks broken and nobody warned them.
- A bad or hostile file crashes the launcher, which supervises the whole grid, or fills its disk or memory.
- A report full of jargon a hobbyist can't act on.
- A design that has to be torn up when rigged meshes arrive.

## How the pieces fit

```
guest's browser ── upload .glb ──► launcher (guest pages)
                                     │ saves it in a job folder
                                     │ starts the converter as a child process
                                     ▼
                         GridGate.ModelImport (OpenSim tree, own process)
                           reads glTF (SharpGLTF) → middle model → checks and fixes
                           → writes report.json and the finished assets into the job folder
                                     │
launcher ◄── report.json ────────────┘
  shows the report; on Make the item: payment hook → POST each asset to Robust
  → inventory item in the guest's "Uploads" folder → done page
```

Facts this rests on, checked in the code on 2026-11-10:
- Robust's private port (`PrivatePort`, 5003 in `bin/Robust.HG.ini`) runs `AssetServiceConnector` (`POST /assets` takes an `AssetBase` as XML, see `OpenSim/Server/Handlers/Asset/AssetServerPostHandler.cs`) and `XInventoryInConnector` (`GETFOLDERFORTYPE`, `ADDFOLDER`, `ADDITEM`, see `OpenSim/Server/Handlers/Inventory/XInventoryInConnector.cs`). The launcher already calls that port for accounts and passwords (`Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs`).
- The launcher reads inventory straight from the database (`IInventoryStore`). For **writes** it goes through Robust instead **(my call: Robust keeps the asset store, whichever kind, and its caches consistent)**.
- `SceneObjectSerializer.ToOriginalXmlFormat` works on a `SceneObjectGroup` built without a scene; the inventory-archive tests do this.
- OpenSim already encodes JPEG2000 (`OpenJPEG.EncodeFromImage`, used by `VectorRenderModule`), through `System.Drawing`. On Linux this needs the repo's `bin/System.Drawing.Common.dll.linux` and `System.Drawing.EnableUnixSupport` in the program's runtimeconfig, as OpenSim's own programs have **[X]**.
- Both the launcher and OpenSim target `net10.0`.

## The guest side

### Who sees it
- The page is `/guest/upload` **(my call for the name)**. It is linked from the guest pages' menu only for a signed-in guest whose upload switch is on, on an install that runs its own Robust. Anyone else gets a 404 for the page. On a Grid Asset Server with no regions of its own, the page is hidden too (there are no limits to take).

### Upload
- One file: `.glb`, or a `.gltf` whose buffers and images are all embedded as `data:` URIs. A `.gltf` that points at separate files is refused with a message saying to export as `.glb` ("glTF Binary" in Blender) **(my call: one file keeps the upload simple and safe)**.
- An optional item name, defaulting to the file name without extension.
- On submit the converter runs in check mode and the guest lands on the report page.

### The report page
Look and wording follow the mockup shown on 2026-11-10 ("A: check first, then you decide"), with the owner's honesty rule.

- **Heading:** what was found, for example "castle.glb was checked: 1 thing may look wrong, 2 things will be fixed for you."
- Every finding is one of three kinds **(the split is my call; the honesty wording is the owner's)**:
  - **Safe fix** (one line, neutral colour): changed for you, won't change how it looks. Example: "Split into 3 linked parts (20 materials, 8 per part). This won't change how it looks."
  - **Risky** (warning colour): the item will be made, but may look wrong. These are gathered into one warning box at the top in the owner's wording: "**Castle may look distorted or shaped wrong.** You should correct: 1. … 2. …", each with the grid's limit, the model's number, why the limit exists and a Blender tip.
  - **Blocking** (error colour): the item can't be made. The page says why and how to fix it, and offers to upload a different file. **Make the item** is not shown.
- **Size:** if the model is larger than the grid's largest part size, the page offers a scale field pre-filled with the largest percentage that fits, and says so. Changing it re-runs the check. If the guest doesn't scale it, it's a blocking finding **(my call: shrinking someone's model without asking is a change they'd notice)**.
- **Next-owner permissions:** copy, modify, transfer tick boxes, all on by default **(my call; matches the viewer's upload window)**.
- A **Limits on this grid** link to a page listing every limit, its value on this grid, why it exists and a Blender tip.
- **Make the item** (shown unless something blocks). Its label carries the price once payment exists.

### Make the item
1. Check that the job is still there and its options match the report that was shown (a re-check replaces the job).
2. **Payment hook.** Call `IUploadCharge.TryChargeAsync(guest, job summary)`. The proof-of-concept version always says yes and charges nothing. A no stops here with the message it gives.
3. Write every asset to Robust, then the inventory item (below).
4. Show the done page: "Castle is in your inventory, in the Uploads folder. If you're logged in, log out and back in to see it.", with the report repeated below, and **Upload another**.

**Owner, 2026-11-10: logging in again to see the item is fine for the proof of concept.** The launcher writes to Robust directly, and an open Firestorm session isn't told; making the item appear live (through the region the guest is in) is a later item.

A failure partway through shows a plain "That didn't work, nothing was charged" message and is logged; assets already written stay in the store (unreferenced assets are harmless) **(my call)**. The payment hook must know about this case once payment is real: charge after the writes succeed, or refund on failure **[?]**, to be settled by the payment project.

### Findings the proof of concept knows
Text is written by the launcher from a code and numbers in `report.json`, so it can go through the languages work later **(my call)**.

| Code | Kind | When |
|---|---|---|
| `split-materials` | safe | more than 8 materials in one mesh: extra parts in the linkset |
| `split-vertices` | safe | a material's geometry exceeds 65,535 vertices: split into more faces or parts |
| `texture-resized` | safe | image side not a power of two, or above 1024: resized to the nearest allowed size not above 1024 |
| `texture-shrunk` | risky | an image was larger than 1024 on a side (it will look softer) |
| `heavy-mesh` | risky | more than 200,000 triangles in the item **(my call for the number; tune on the VM)**: the far-away detail levels may look lumpy or have holes |
| `too-big` | blocking (with the scale offer) | a part's side is larger than the grid's largest part size |
| `too-small` | safe if under half the parts, else blocking | parts smaller than the grid's smallest part size are dropped, as `MeshCost.cs` does |
| `too-many-parts` | blocking | after splitting, the item has more parts than the linkset limit |
| `inside-out` | risky | a mesh has a negative scale or flipped winding |
| `no-uv` | risky | a textured material has no UV coordinates |
| `dropped-animation`, `dropped-morphs`, `dropped-extras` | safe | animations, shape keys, cameras or lights were left out |
| `skin-ignored` | risky | the model is rigged; in this version it is made as a plain object in its rest pose |
| `compressed` | blocking | the file uses Draco or meshopt compression: export again without it |
| `external-files` | blocking | a `.gltf` refers to separate files |
| `not-triangles` | blocking if nothing else is left | lines or points; they are dropped |
| `bad-file` | blocking | not a readable glTF 2.0 file, or no mesh in it |
| `too-large-file` | blocking | the upload or an embedded image is above the size caps (Security) |

## The staff side
- A switch **May upload models** on a person's page (`/people/{id}`, `Pages/People/Person.cshtml`), for the owner and admins **(my call: the same people who handle takedowns)**. Changing it is audited as `upload-right-changed`.
- Stored in the launcher's data folder like the other per-person launcher files (the builder picks the file, following `ResidentTwoFactor` or similar) **(my call: no new database table)**.
- Every finished upload is audited as `model-uploaded` with the guest, file name, item name and asset ids, so a takedown can find it.

## The converter: `GridGate.ModelImport`
A console program in the OpenSim tree (for example `OpenSim/Tools/GridGate.ModelImport/`, the builder picks), built into `bin/` with the other OpenSim programs. References SharpGLTF, `OpenSim.Framework`, `OpenSim.Region.Framework` (for the object serializer) and OpenMetaverse.

### Command line
```
GridGate.ModelImport check --input model.glb --limits limits.json --options options.json --out <job folder>
```
- `limits.json` comes from the launcher: largest part size (`NonPhysicalPrimMax`), smallest part size (`NonPhysicalPrimMin`, 0.001 m when unset; `MeshCost.PrimScaleMin` comes from it and `GridConfigWriter` doesn't write it today), largest physical part size (`PhysicalPrimMax`), linkset limit (`LinksetPrims`, where **0 means no limit** and must not be taken as the smallest value), and the importer's own caps. The launcher takes the strictest value across the regions it knows about, so the item rezzes on any of them **(my call)**. On a Grid Asset Server those are **this computer's own regions only** (owner, 2026-11-10); regions on computers that joined the grid are not consulted, and a joined region with stricter settings could refuse the item. The Limits page says so on that plan.
- `options.json`: item name, scale percentage, next-owner permissions, the uploader's id and name (creator and owner of the object).
- Writes `report.json` and, unless something blocks, `assets/` plus `manifest.json` into the job folder. Exit code 0 means a report was written (blocking or not); anything else means the converter failed, and the launcher shows a "couldn't read this file" message.
- There is one mode: check always builds everything, so Make the item only uploads what's in the job folder and doesn't run the converter again **(my call: what the guest reads is exactly what they get)**.

### Stages
1. **Read** with SharpGLTF. Refuse external URIs, Draco and meshopt before anything is decoded.
2. **Middle model** (below), in metres, Z-up (glTF is Y-up: rotate +90° about X), with node transforms baked into each part's position, rotation and scale.
3. **Check and fix** against `limits.json`, collecting findings.
4. **Detail levels:** high = the original; medium, low, lowest made by simplification to about 50%, 25% and 6% of the triangles **(my call for the ratios)**. The builder picks a simplifier: a small pure-C# quadric-error simplifier (an MIT library or our own, no native code) **[?]**.
5. **Physics:** one box-shaped convex hull per part (its bounding box) **(my call for the proof of concept)**.
6. **Write assets:** textures, meshes, then the object (below).

### Middle model
Plain C# records, no OpenSim types, so a VRM reader or anything else can fill them later:
- `Model` → `Parts[]` (name, position, rotation, scale, `Mesh`), the root part, `Skeleton?`, `Materials[]`, `Images[]`, and notes about what was dropped.
- `Mesh` → `Faces[]`, at most 8 after splitting. Each face: positions, normals, UV0, triangle indices, material index, and, when rigged, up to 4 joint indices and weights per vertex.
- `Skeleton` → joints (name, parent, rest transform), inverse bind matrices, bind-shape matrix. Filled in from the glTF skin even now; the writer is told whether to emit it.

### The three kinds of asset

**Mesh (asset type 49).** We write this ourselves; OpenSim only reads it. An LLSD-binary header map giving `offset` and `size` for each block, followed by the blocks, each a zlib-compressed LLSD map: `high_lod`, `medium_lod`, `low_lod`, `lowest_lod` (arrays of faces with `Position`, `PositionDomain`, `Normal`, `TexCoord0`, `TexCoord0Domain`, `TriangleList`, and `Weights` when rigged, positions and UVs quantised to 16 bits inside their domain), `physics_convex`, and `skin` (`joint_names`, `inverse_bind_matrix`, `bind_shape_matrix`). The builder checks the exact layout against OpenSim's own readers (Meshmerizer, and `MeshCost.cs` in `OpenSim/Region/ClientStack/Linden/Caps/BunchOfCaps/`) and the published Second Life mesh asset format. In this version the `skin` block is written only by the round-trip test, never in an uploaded item.

**Texture (asset type 0).** JPEG2000 through OpenSim's encoder. One per glTF image that a kept material uses, after resizing.

**Object (asset type 6).** A `SceneObjectGroup` built in memory: one `SceneObjectPart` per part, shape set to mesh (`SculptType` mesh, `SculptTexture` = the mesh asset id), scale from the part's bounds, a texture entry giving each face its texture and colour (alpha mode carried), creator and owner = the uploader, full permissions for the owner, next-owner permissions from the options. Serialised with `SceneObjectSerializer.ToOriginalXmlFormat`.

`manifest.json` lists every asset file with its id, type and name, and the inventory item's name, description and permissions.

## Writing to Robust (launcher)
- Extend the launcher's Robust client (or add a sibling) with: store an asset (`POST /assets`), find the guest's root folder, find or create an **Uploads** folder under it **(my call)**, add an inventory item (asset type 6, inventory type 6).
- Asset ids are new random UUIDs chosen by the converter. The item's description is "Uploaded from <file name> on <date>".

## Security **[S]**
Uploads are the riskiest part of the guest side (the support-tickets design left them out for that reason).
- Only signed-in guests with the switch on; the usual anti-forgery token; one job per guest at a time.
- **Caps** **(my calls, all set in `limits.json`)**: upload 64 MB; an embedded image 8192 px on a side, checked from the image header **before** decoding; 2,000,000 triangles in total; 512 nodes. Above a cap is a blocking finding, decided before the expensive work.
- No network: the converter never fetches a URI. Only `data:` URIs and `.glb` buffers.
- The converter runs as a child process with a time limit (2 minutes) and a memory limit **[X]**. The launcher has no limit mechanism today (`ManagedProcess` starts children with `setsid` and only kills the process tree), so this is new work on both platforms: for example `prlimit`/`setrlimit` or a systemd scope on Linux, a job object on Windows. Killed or crashed means a "couldn't read this file" message, never a launcher crash.
- **At most one conversion runs at a time on the whole install** **(my call)**. A second guest gets "The uploader is busy, try again in a minute" rather than a queue, so several uploads can't add up to more memory than one.
- Job folders live under the launcher's data folder, named by a random id, never by the uploaded file's name. Deleted after upload, or after 1 hour.
- The launcher validates `manifest.json` (types, sizes, counts) before writing anything to Robust; it doesn't trust the converter's output blindly.
- File names and item names are cleaned (length, characters) before they're shown or stored.

## Ready for heads and bodies (the next project)
- The middle model has a skeleton and per-vertex weights, filled from glTF skins now.
- The mesh writer can write the `skin` block and weights; a test writes Fox and Cesium Man with skin, reads them back with OpenSim's reader, and compares joints, weights and matrices.
- The next project adds: a VRM reader, mapping to Second Life bone names, re-binding to Second Life's rest pose, cutting to 4 weights per vertex, and offering the result as wearable. None of that changes the shapes above.

## Testing
- **Converter tests** (OpenSim test project or its own): each finding code has a test; test models are the small CC0 and CC BY files from Khronos glTF-Sample-Assets (Box, Box Vertex Colors, Box Textured, Avocado, Fox), committed with a `LICENSES.md`. Larger models (Damaged Helmet, Flight Helmet, A Beautiful Game, Materials Variants Shoe, Sponza, and Cesium Man for the skin round trip) come from a download script. Duck, BrainStem and the Stanford dragons are never committed.
- **Mesh round trip:** write, then read back with OpenSim's own reader; positions within the 16-bit quantisation step.
- **Launcher tests:** with a fake converter (like the existing fake child process), covering the page visibility rules, the report rendering, the payment hook being called once before writes, failures, and job clean-up.
- **On the Debian 12 test VM and on Windows** **[X]**: upload Box, Avocado, Damaged Helmet, Flight Helmet and A Beautiful Game through the guest pages, log in again, rez each in Firestorm, and check by eye that it looks right, sits at the right size and is textured. That check is the proof of concept's pass mark.

## Left to the builder
- The simplifier for detail levels (pure C#, no native code).
- Folder and project names, the file that stores the upload switch, the child-process limit mechanism on each platform.
- The exact wording of each finding and of the Limits page, kept short and plain, with a Blender tip each.
- How the report page looks, within the mockup's layout.

## Open questions **[?]**
- Charge before or after the Robust writes, and refunds: for the payment project.
- The `heavy-mesh` threshold and the detail-level ratios: tune with real models on the VM.
