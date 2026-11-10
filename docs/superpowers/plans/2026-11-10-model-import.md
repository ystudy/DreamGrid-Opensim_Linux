# Model import (proof of concept) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A guest with the upload right uploads a `.glb` on the GridGate guest pages, reads a check report, clicks Make the item, and gets a rezzable object in an "Uploads" inventory folder.

**Architecture:** A new console program in the OpenSim tree, `GridGate.ModelImport`, reads glTF with SharpGLTF into a plain middle model, checks and fixes it against the grid's limits, and writes `report.json`, the finished assets and `manifest.json` into a job folder. The launcher runs it as a child process, renders the report, and on Make the item writes the assets and the inventory item to Robust's private port. The two sides share only JSON files, whose shape is fixed in Task 2.

**Tech Stack:** .NET 10, SharpGLTF.Core, OpenSim.Framework / OpenSim.Region.Framework / OpenMetaverse (in the converter only), ASP.NET Core Razor Pages (launcher), xunit.v3, AngleSharp (page tests).

**Spec:** `docs/superpowers/specs/2026-11-10-model-import-design.md`. Read it before any task; section names below refer to it.

## Global Constraints

- Work in `C:\Users\ystud\Desktop\CODE\DreamGrid-model-import` on branch `feature/model-import`. Other sessions switch branches in `DreamGrid-Linux`; never commit there. Run `git branch --show-current` before every commit and `git add` only your own files.
- The launcher must not reference any OpenSim assembly, SharpGLTF or OpenMetaverse. Only the converter and its tests do.
- The converter never opens a network connection and never reads a file it wasn't given.
- **[X]** Everything builds and its tests pass on Windows and Debian 12. The converter reads images through OpenSim's `System.Drawing`, so its `runtimeconfig.template.json` must set `System.Drawing.EnableUnixSupport` like `OpenSim/Server/runtimeconfig.template.json`.
- Values from the spec, used verbatim: 8 faces per mesh; 65,535 vertices per face; textures resized to a power of two not above **1024**; heavy mesh above **200,000** triangles; upload cap **64 MB**; embedded image cap **8192** px a side, checked from the header before decoding; **2,000,000** triangles; **512** nodes; converter time limit **2 minutes**; job folders deleted after upload or after **1 hour**; one conversion at a time on the whole install; detail levels at about **50% / 25% / 6%** of the triangles.
- OpenSim defaults when a region doesn't set them: largest part 256 m (`NonPhysicalPrimMax`), largest physical part 10 m (`PhysicalPrimMax`), smallest part 0.001 m (`NonPhysicalPrimMin`), linkset limit 0 = no limit (`LinksetPrims`).
- Guest-facing text is short, plain English with no jargon; each finding ends with a Blender tip. The owner's warning heading is verbatim: "**{Item} may look distorted or shaped wrong.** You should correct:".
- Audit actions are exactly `upload-right-changed` and `model-uploaded`.
- Test models in the repo: CC0 or CC BY 4.0 only, credited in `LICENSES.md`. Duck, BrainStem, Dragon Attenuation and Dragon Dispersion are never committed.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A model whose nodes are nested with rotations and non-uniform scales** (common Blender exports): each part must rez where it sits in Blender, not at the origin or rotated 90°. Test in Task 3 (`Nested_transforms_are_baked_into_part_placement`).
2. **A material with no texture but a base colour, or with alpha blend**: the face must keep its colour and transparency, not turn plain white. Test in Task 7 (`Untextured_face_keeps_its_colour_and_alpha`).
3. **A guest who double-clicks Make the item, or reloads the done page**: exactly one item and one charge. Test in Task 11 (`Make_the_item_twice_makes_one_item`).
4. **An upload whose file name has odd characters or is very long** (`../../x.glb`, 300 characters, emoji): the job folder name never comes from it, and the item name is cleaned. Test in Task 11 (`Hostile_file_name_is_cleaned`).
5. **A model whose every part is smaller than the smallest size, or a file with only lines and points**: a blocking finding with a plain message, never an empty item. Test in Task 4 (`Nothing_left_after_dropping_is_blocking`).

---

## File map

Converter (OpenSim tree):
- `OpenSim/Tools/GridGate.ModelImport/GridGate.ModelImport.csproj`, `runtimeconfig.template.json`, `Program.cs` (command line)
- `.../Contracts.cs`: the JSON files (Task 2)
- `.../Model/ImportModel.cs`: the middle model (Task 1)
- `.../Reading/GltfReader.cs`, `.../Reading/ImageHeader.cs` (Tasks 1, 3)
- `.../Checking/ModelChecker.cs`, `.../Checking/FaceSplitter.cs` (Task 4)
- `.../Building/TextureBuilder.cs` (Task 5), `.../Building/Simplifier.cs` (Task 6), `.../Building/MeshAssetWriter.cs` (Task 6), `.../Building/ObjectAssetBuilder.cs` (Task 7), `.../Building/JobWriter.cs` (Task 7)
- `tests/GridGate.ModelImport.Tests/` with `Models/` (committed), `Models/downloaded/` (git-ignored), `LICENSES.md`, `fetch-models.sh`
- `tests/fixtures/model-import/` sample JSON shared with the launcher tests (Task 2)

Launcher:
- `Launcher/DreamGrid.Launcher.Core/ModelImport/ImportContracts.cs` (mirror of Task 2), `ImportLimits.cs`, `ModelConverter.cs`, `ImportJobs.cs`, `UploadCharge.cs`
- `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustAssets.cs`
- `Launcher/DreamGrid.Launcher/Guests/UploadRights.cs`
- `Launcher/DreamGrid.Launcher/Pages/Guest/Upload.cshtml(.cs)`, `UploadReport.cshtml(.cs)`, `UploadLimits.cshtml(.cs)`, `Launcher/DreamGrid.Launcher/ModelImport/FindingText.cs`
- Modify: `Pages/Guest/Index.cshtml(.cs)`, `Pages/People/Person.cshtml(.cs)`, `Security/PeopleActions.cs`, `DreamGrid.Launcher.Core/Security/PeopleRules.cs`, `Program.cs`, `tests/DreamGrid.FakeChild/Program.cs`, `DreamGrid.sln`

---

### Task 1: Converter project, middle model and reading a static glTF

**Files:**
- Create: `OpenSim/Tools/GridGate.ModelImport/GridGate.ModelImport.csproj`, `runtimeconfig.template.json`, `Program.cs` (stub that prints usage and exits 2), `Model/ImportModel.cs`, `Reading/GltfReader.cs`
- Create: `tests/GridGate.ModelImport.Tests/GridGate.ModelImport.Tests.csproj`, `GltfReaderTests.cs`, `Models/Box.glb`, `Models/BoxVertexColors.glb`, `Models/BoxTextured.glb`, `Models/Avocado.glb`, `Models/Fox.glb`, `LICENSES.md`, `fetch-models.sh`
- Modify: `DreamGrid.sln` (add both projects: converter under the `Tools` folder, tests under `tests`), `.gitignore` (`tests/GridGate.ModelImport.Tests/Models/downloaded/`)

**Interfaces:**
- Produces (namespace `GridGate.ModelImport.Model`, plain records, no OpenSim types):
  - `ImportModel(IReadOnlyList<ImportPart> Parts, int RootPart, IReadOnlyList<ImportMaterial> Materials, IReadOnlyList<ImportImage> Images, ImportSkeleton? Skeleton, IReadOnlyList<string> Dropped)`
  - `ImportPart(string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale, ImportMesh Mesh)`; positions in metres, Z-up, in the model's frame
  - `ImportMesh(IReadOnlyList<ImportFace> Faces)`
  - `ImportFace(Vector3[] Positions, Vector3[] Normals, Vector2[]? Uv0, int[] Triangles, int Material, ImportWeights? Weights)`
  - `ImportWeights(ushort[] Joints /* 4 per vertex */, float[] Weights /* 4 per vertex */)`
  - `ImportMaterial(string Name, Vector4 BaseColour, int? BaseColourImage, AlphaMode Alpha, float AlphaCutoff, bool DoubleSided)`; `enum AlphaMode { Opaque, Mask, Blend }`
  - `ImportImage(string Name, string MimeType, byte[] Bytes)`
  - `ImportSkeleton(IReadOnlyList<ImportJoint> Joints, Matrix4x4 BindShape)`; `ImportJoint(string Name, int Parent, Matrix4x4 Rest, Matrix4x4 InverseBind)`
  - `static class GltfReader { static ReadResult Read(Stream input, string fileName, ImportLimits limits); }`, `ReadResult(ImportModel? Model, IReadOnlyList<Finding> Findings)` (`Finding` and `ImportLimits` are Task 2's types: write Task 2's `Contracts.cs` exactly as its Interfaces block gives it, as part of Step 1 here; Task 2 then adds the fixtures and the round-trip test)
- Vector and matrix types are `System.Numerics`.

- [ ] **Step 1: Create the converter project.** Copy the shape of `OpenSim/Tools/Configger/OpenSim.Tools.Configger.csproj` (Exe, `OutputPath` `..\..\..\bin\`, log4net and Nini hint paths), `AssemblyName` `GridGate.ModelImport`, `Nullable` enable, plus `PackageReference` SharpGLTF.Core (latest stable, pinned), `ProjectReference`s to `OpenSim.Framework` and `OpenSim/Region/Framework/OpenSim.Region.Framework.csproj`, and `Reference`s to `OpenMetaverse`, `OpenMetaverseTypes`, `OpenMetaverse.StructuredData`, `OpenMetaverse.Rendering.Meshmerizer` from `$(DgRoot)bin`. Copy `OpenSim/Server/runtimeconfig.template.json` beside it.
- [ ] **Step 2: Create the test project** shaped like `tests/DreamGrid.Physics.Rapier.Tests/DreamGrid.Physics.Rapier.Tests.csproj`, referencing the converter project, with `Models/**` copied to output. Download the five committed models from KhronosGroup/glTF-Sample-Assets (`Models/<Name>/glTF-Binary/`) and write `LICENSES.md` from each model's `LICENSE.md` there. Write `fetch-models.sh` that downloads into `Models/downloaded/`: DamagedHelmet, FlightHelmet, ABeautifulGame, MaterialsVariantsShoe, Sponza, CesiumMan, Lantern (glTF-Draco variant as `Lantern-draco.gltf` with its files), MeshoptCubeTest, NegativeScaleTest, MeshPrimitiveModes, MultiUVTest, NodePerformanceTest. Tests needing a downloaded model call `Assert.Skip` when it is missing.
- [ ] **Step 3: Write the failing tests** in `GltfReaderTests.cs`:

```csharp
[Fact] public void Box_reads_as_one_part_with_12_triangles()
{   var r = Read("Box.glb");
    Assert.Single(r.Model!.Parts);
    Assert.Equal(12, r.Model.Parts[0].Mesh.Faces.Sum(f => f.Triangles.Length / 3)); }

[Fact] public void Y_up_becomes_z_up()  // a vertex at glTF (0,1,0) ends at (0,0,1) after the part's transform
[Fact] public void Avocado_has_one_png_or_jpeg_base_colour_image()
{   var m = Read("Avocado.glb").Model!;
    Assert.NotNull(m.Materials[m.Parts[0].Mesh.Faces[0].Material].BaseColourImage);
    Assert.Contains(m.Images[0].MimeType, new[] { "image/png", "image/jpeg" }); }

[Fact] public void Fox_skeleton_matches_the_file()  // joint count and names equal SharpGLTF's ModelRoot.LogicalSkins[0] joints; weights 4 per vertex
[Fact] public void Fox_animations_are_listed_as_dropped()  // Dropped contains "animation"
```
- [ ] **Step 4: Run** `dotnet test tests/GridGate.ModelImport.Tests`. Expected: FAIL (types missing).
- [ ] **Step 5: Implement `GltfReader.Read`.** Walk the default scene's node tree; each node with a mesh becomes one `ImportPart` whose transform is the node's world matrix decomposed, after converting Y-up to Z-up (rotate +90° about X: `(x, y, z) → (x, -z, y)`). Each glTF primitive with mode Triangles becomes one `ImportFace` (indices widened to `int`; non-indexed primitives get 0..n-1). Skins fill `Skeleton` from the first skin and `Weights` from `JOINTS_0`/`WEIGHTS_0`. Animations, morph targets, cameras and `KHR_lights_punctual` add `"animation"`, `"morphs"`, `"cameras"`, `"lights"` to `Dropped`.
- [ ] **Step 6: Run the tests.** Expected: PASS. Also `dotnet build -c Release DreamGrid.sln` succeeds and `bin/GridGate.ModelImport.dll` exists.
- [ ] **Step 7: Commit** `feat(model-import): converter project and glTF reader`.

### Task 2: The shared JSON contract

**Files:**
- Create: `OpenSim/Tools/GridGate.ModelImport/Contracts.cs`, `tests/fixtures/model-import/limits.json`, `options.json`, `report.json`, `manifest.json`, `tests/GridGate.ModelImport.Tests/ContractTests.cs`

**Interfaces:**
- Produces (namespace `GridGate.ModelImport`, `System.Text.Json`, camelCase, enums as camelCase strings; the launcher mirrors these exactly in Task 8):

```csharp
public sealed record ImportLimits(double MaxPartSize, double MinPartSize, double MaxPhysicalPartSize, int MaxLinksetParts /* 0 = none */,
    long MaxUploadBytes, int MaxImageSide, int MaxTextureSide, int MaxTriangles, int MaxNodes, int HeavyTriangles);
public sealed record NextOwner(bool Copy, bool Modify, bool Transfer);
public sealed record ImportOptions(string ItemName, string FileName, int ScalePercent, NextOwner NextOwner, Guid UploaderId, string UploaderName);
public enum FindingKind { Safe, Risky, Blocking }
public sealed record Finding(string Code, FindingKind Kind, IReadOnlyDictionary<string, string> Values);
public sealed record ReportSummary(int Parts, int Faces, int Triangles, int Textures, double[] SizeMetres, int? LargestFitPercent);
public sealed record ImportReport(int Version /* 1 */, bool Blocked, ReportSummary Summary, IReadOnlyList<Finding> Findings);
public sealed record ManifestAsset(Guid Id, int Type /* 0 texture, 49 mesh, 6 object */, string Name, string File /* "assets/<id>.bin" */);
public sealed record ManifestItem(Guid AssetId, string Name, string Description, uint NextOwnerMask, Guid CreatorId);
public sealed record ImportManifest(int Version /* 1 */, IReadOnlyList<ManifestAsset> Assets, ManifestItem Item);
public static class ContractJson { public static JsonSerializerOptions Options { get; } }
```
- Finding codes are the spec's table plus `texture-unsupported` (risky: an image that isn't PNG or JPEG, such as KTX2 or WebP, is dropped and the face shows its plain colour).

- [ ] **Step 1: Write the four fixture files** with realistic values (the castle example from the spec: 300 m wide, 20 materials, 2,100,000 triangles; `report.json` has `split-materials` safe with values `materials`, `perPart`, `parts`; `heavy-mesh` risky with `triangles`, `threshold`; `too-big` blocking with `side`, `limit`, `fitPercent`).
- [ ] **Step 2: Write the failing test** `Fixtures_round_trip_unchanged`: deserialize each fixture with `ContractJson.Options`, serialize again, and compare the JSON documents (`JsonNode.DeepEquals`).
- [ ] **Step 3: Run.** Expected: FAIL.
- [ ] **Step 4: Implement `Contracts.cs`.**
- [ ] **Step 5: Run.** Expected: PASS.
- [ ] **Step 6: Commit** `feat(model-import): JSON contract between launcher and converter`.

### Task 3: Refusals before decoding, and placement

**Files:**
- Create: `OpenSim/Tools/GridGate.ModelImport/Reading/ImageHeader.cs`; Modify: `Reading/GltfReader.cs`; Test: `tests/GridGate.ModelImport.Tests/ReaderRefusalTests.cs`

**Interfaces:**
- Produces: `static class ImageHeader { static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes); }`, reading PNG `IHDR` and JPEG `SOF0`–`SOF15` (except `SOF4`, `SOF8`, `SOF12`) without decoding; null for anything else.

- [ ] **Step 1: Write the failing tests:**
  - `External_buffer_is_blocking`: a `.gltf` whose buffer `uri` is `"model.bin"` gives one finding `external-files`, Blocking, and `Model` null. Same for an image `uri` of `"https://example.com/a.png"`, and **no request is made** (the reader has no HTTP client to call; assert by loading with a SharpGLTF read context that throws on any satellite-file request).
  - `Draco_is_blocking` (downloaded Lantern-draco) and `Meshopt_is_blocking` (downloaded MeshoptCubeTest): finding `compressed`.
  - `Image_above_8192_is_blocking_before_decode`: a `.glb` built in the test with SharpGLTF holding a PNG whose header says 10000×10 (IHDR only, garbage after): finding `too-large-file`, values `image`, `limit` = `8192`.
  - `Upload_above_cap_is_blocking`: `MaxUploadBytes` = 1000 and Box.glb: `too-large-file`.
  - `Over_2M_triangles_or_512_nodes_is_blocking`: limits lowered to 11 triangles and to 0 nodes, on Box.glb.
  - `Not_glTF_is_blocking`: a text file gives `bad-file`.
  - `Nested_transforms_are_baked_into_part_placement` (Review Focus 1): a model built in the test with a parent node (rotation 90° about glTF Y, scale (2,1,1)) and a child mesh node (translation (1,0,0)) reads as a part whose `Position` is (0,-2,0) in Z-up metres. Assert to 1e-4.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Check order in `GltfReader.Read`: byte cap → parse JSON chunk only → `extensionsUsed`/`extensionsRequired` for `KHR_draco_mesh_compression`, `EXT_meshopt_compression`, `KHR_meshopt_compression` → every buffer/image `uri` must be `data:` (or absent in `.glb`) → node count → image headers → then full read → triangle count. Any `Exception` from SharpGLTF on a full read becomes `bad-file`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(model-import): refuse unsafe or unsupported files before decoding`.

### Task 4: Checks and fixes

**Files:**
- Create: `Checking/ModelChecker.cs`, `Checking/FaceSplitter.cs`; Test: `tests/GridGate.ModelImport.Tests/ModelCheckerTests.cs`

**Interfaces:**
- Consumes: Task 1 model, Task 2 `ImportLimits`, `ImportOptions`, `Finding`, `ReportSummary`.
- Produces: `static class ModelChecker { static CheckResult Check(ImportModel model, ImportLimits limits, ImportOptions options); }`, `CheckResult(ImportModel Fixed, IReadOnlyList<Finding> Findings, ReportSummary Summary, bool Blocked)`. `Fixed` has at most 8 faces per mesh and at most 65,535 vertices per face, scale applied, tiny parts dropped, lines/points removed.
- `static class FaceSplitter { static IReadOnlyList<ImportPart> Split(ImportPart part); }`: splits a face over 65,535 vertices along triangle boundaries (copying the vertices each new face uses), then splits parts over 8 faces into extra parts with the same transform and names `"<name> 2"`, `"<name> 3"`.

- [ ] **Step 1: Write the failing tests**, one per finding the checker owns, asserting code, kind and the values the launcher text needs:
  - `Twenty_materials_split_into_three_parts`: `split-materials`, Safe, `materials`=20, `perPart`=8, `parts`=3; every resulting mesh has ≤ 8 faces.
  - `Face_over_65535_vertices_is_split`: a generated grid of 70,000 vertices: `split-vertices`, Safe; every face ≤ 65,535 vertices; total triangles unchanged.
  - `Too_big_offers_a_fit_percent`: a 300 m part, limit 256: `too-big`, Blocking, `side`=300, `limit`=256, `fitPercent`=85 (floor of 256/300×100); `Summary.LargestFitPercent`=85.
  - `Scaled_to_fit_is_not_blocking`: same model with `ScalePercent`=85: no `too-big`, part scale ×0.85.
  - `Small_parts_dropped_when_under_half`: 1 of 3 parts below `MinPartSize`: `too-small`, Safe, `dropped`=1, and 2 parts remain. 2 of 3: Blocking.
  - `Nothing_left_after_dropping_is_blocking` (Review Focus 5): every part tiny → Blocking; a model with only line primitives → `not-triangles` Blocking.
  - `Linkset_limit_zero_means_no_limit`: 600 parts, `MaxLinksetParts`=0: no `too-many-parts`. With 512: Blocking `too-many-parts`, `parts`=600, `limit`=512.
  - `Heavy_mesh_is_risky`: 250,000 triangles: `heavy-mesh`, Risky, `triangles`=250000, `threshold`=200000.
  - `Negative_scale_is_risky` (downloaded NegativeScaleTest): `inside-out`, Risky.
  - `Textured_face_without_uv_is_risky`: `no-uv`, Risky.
  - `Rigged_model_is_risky_and_made_static`: Fox: `skin-ignored`, Risky; `Fixed.Skeleton` still set (Task 6 needs it), weights still present.
  - `Dropped_things_are_safe_findings`: Fox: `dropped-animation`, Safe.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** `Blocked` = any Blocking finding. Sizes are each part's world-axis bounding box after scale.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(model-import): check the model against the grid's limits and fix what is safe`.

### Task 5: Textures

**Files:**
- Create: `Building/TextureBuilder.cs`; Test: `tests/GridGate.ModelImport.Tests/TextureBuilderTests.cs`

**Interfaces:**
- Produces: `static class TextureBuilder { static TextureResult Build(ImportImage image, int maxSide); }`, `TextureResult(byte[] J2k, int Width, int Height, Finding? Finding)`. Only PNG and JPEG are decoded; anything else returns `J2k` empty and `texture-unsupported`.

- [ ] **Step 1: Write the failing tests:** `Power_of_two_under_cap_is_unchanged_size` (512×256 stays, no finding); `Odd_size_goes_to_nearest_power_of_two` (300×200 → 256×256, `texture-resized` Safe); `Over_1024_is_shrunk_and_risky` (2048×2048 → 1024×1024, `texture-shrunk` Risky with `from`=2048, `to`=1024); `Output_is_jpeg2000` (bytes start with the J2K codestream marker `FF 4F FF 51`); `Webp_is_unsupported`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** with `System.Drawing.Bitmap` (decode, high-quality bicubic resize) and `OpenMetaverse.Imaging.OpenJPEG.EncodeFromImage(bitmap, false)`, as `OpenSim/Region/CoreModules/Scripting/VectorRender/VectorRenderModule.cs` does. Each side is rounded to the nearest power of two, then capped at `maxSide`.
- [ ] **Step 4: Run** on Windows, and on the Debian 12 VM (`dotnet test` there). Expected: PASS on both.
- [ ] **Step 5: Commit** `feat(model-import): textures resized and encoded as JPEG2000`.

### Task 6: Detail levels and the mesh asset

**Files:**
- Create: `Building/Simplifier.cs`, `Building/MeshAssetWriter.cs`; Test: `tests/GridGate.ModelImport.Tests/SimplifierTests.cs`, `MeshAssetWriterTests.cs`

**Interfaces:**
- Produces:
  - `static class Simplifier { static ImportMesh Simplify(ImportMesh mesh, double ratio); }`: quadric-error edge collapse (Garland–Heckbert), pure C#, deterministic, keeps each face's material and never removes a face entirely (a face below 1 triangle keeps its original smallest triangle).
  - `static class MeshAssetWriter { static byte[] Write(ImportMesh high, MeshAssetOptions options); }`, `MeshAssetOptions(bool WriteSkin, ImportSkeleton? Skeleton)`. Medium, low and lowest are `Simplify(high, 0.5 / 0.25 / 0.06)`. `physics_convex` is the mesh's bounding box (8 `BoundingVerts`, `Min`, `Max`).
  - Tests read the asset back with `OpenMetaverse.Rendering.FacetedMesh.TryDecodeFromAsset` and, for the skin, `OSDParser.DeserializeLLSDBinary` on the zlib-inflated block.

- [ ] **Step 1: Write the failing tests:**
  - `Simplify_hits_the_ratio`: Avocado at 0.25 has between 20% and 30% of the triangles; at 1.0 is unchanged.
  - `Simplify_keeps_every_material`: a 3-material model at 0.06 still has 3 faces.
  - `Round_trip_through_OpenSims_reader`: Box and Avocado written, then decoded at `DetailLevel.Highest`: same face count, same triangle count, every position within one quantisation step (`(max - min) / 65535` per axis) of the original, UVs likewise.
  - `All_four_detail_levels_decode`: each `DetailLevel` decodes and has ≤ the triangles of the level above.
  - `Skin_round_trip`: Fox and CesiumMan written with `WriteSkin: true`: the `skin` block's `joint_names`, `inverse_bind_matrix` (16 floats each) and `bind_shape_matrix` equal the input; each vertex's 4 weights sum to 1 within 1/65535.
  - `No_skin_block_unless_asked`: Fox with `WriteSkin: false`: the header map has no `skin` key.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Layout: an LLSD-binary header map `{ "high_lod": { "offset", "size" }, "medium_lod", "low_lod", "lowest_lod", "physics_convex", ["skin"] }` followed by each block, zlib-compressed (RFC 1950, `ZLibStream`), offsets counted from the end of the header. Per face: `Position` (uint16 LE xyz), `PositionDomain` `{Min, Max}`, `Normal` (uint16 mapped from −1..1), `TexCoord0` + `TexCoord0Domain` when UVs exist, `TriangleList` (uint16 LE), `Weights` when rigged and `WriteSkin`. Check the encoding of `Weights` against `FacetedMesh`'s decoder and the Second Life mesh asset format; where they differ, the decoder wins.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(model-import): detail levels and the mesh asset, skin block included`.

### Task 7: The object, the job folder and the command line

**Files:**
- Create: `Building/ObjectAssetBuilder.cs`, `Building/JobWriter.cs`; Modify: `Program.cs`; Test: `tests/GridGate.ModelImport.Tests/ObjectAssetBuilderTests.cs`, `CommandLineTests.cs`

**Interfaces:**
- Produces:
  - `static class ObjectAssetBuilder { static string Build(ImportModel fixedModel, IReadOnlyList<Guid> meshIds, IReadOnlyDictionary<int, Guid> textureIdByImage, ImportOptions options); }` returning the object XML.
  - `static class JobWriter { static int Run(string input, string limitsFile, string optionsFile, string outDir); }`: reads, checks, builds, writes `report.json` always, and `assets/<id>.bin` + `manifest.json` only when not blocked. Returns the exit code.
  - Command line: `GridGate.ModelImport check --input <file> --limits <json> --options <json> --out <dir>`. Exit 0 = report written; 2 = bad arguments or unreadable JSON; 3 = unexpected failure (message to stderr, no report).

- [ ] **Step 1: Write the failing tests:**
  - `Object_round_trips_through_OpenSims_serializer`: build for Box, then `SceneObjectSerializer.FromOriginalXmlFormat`: 1 part; `Shape.SculptType` = `(byte)SculptType.Mesh`; `Shape.SculptTexture` = the mesh id; `CreatorID` and `OwnerID` = `UploaderId`; `OwnerMask` = `PermissionMask.All | Export`; `NextOwnerMask` from options (all three off gives `Transfer`, as `BunchOfCaps` does).
  - `Linkset_keeps_part_positions`: Flight Helmet (downloaded): part count equals the checked model's; each child's offset equals its part position minus the root's, to 1e-3.
  - `Untextured_face_keeps_its_colour_and_alpha` (Review Focus 2): a face with base colour (1, 0, 0, 0.5) and `Blend` has texture-entry colour (1, 0, 0, 0.5) and no texture; a textured face has its texture id.
  - `Check_writes_report_and_assets` (end to end on Box): exit 0, `report.json` `blocked` false, `manifest.json` lists 1 mesh + 1 object, every listed file exists.
  - `Blocked_writes_report_only`: a 300 m model: exit 0, `blocked` true, no `assets/`, no `manifest.json`.
  - `Bad_arguments_exit_2`, `Unreadable_limits_exit_2`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** following `BunchOfCaps.UploadCompleteHandler` (`OpenSim/Region/ClientStack/Linden/Caps/BunchOfCaps/BunchOfCaps.cs`, around lines 870–1040): `PrimitiveBaseShape.CreateMesh(faceCount, meshId)`, a `Primitive.TextureEntry` with one face per mesh face, `new SceneObjectPart(owner, pbs, position, rotation, Vector3.Zero)`, `new SceneObjectGroup(root)` then `AddPart` for children with their offset and rotation relative to the root, `SceneObjectSerializer.ToOriginalXmlFormat`. Textures: every image a kept material uses goes through `TextureBuilder.Build(image, limits.MaxTextureSide)` once; its finding joins the report; an unsupported image leaves the face untextured. Part names are the item name for the root and `"<item> #<n>"` for children. The item description is `"Uploaded from <FileName> on <yyyy-MM-dd>"`.
- [ ] **Step 4: Run.** Expected: PASS. Then run the program by hand on the VM: `dotnet bin/GridGate.ModelImport.dll check --input Avocado.glb --limits tests/fixtures/model-import/limits.json --options tests/fixtures/model-import/options.json --out /tmp/job1` exits 0 and writes a report.
- [ ] **Step 5: Commit** `feat(model-import): object asset, job folder and command line`.

### Task 8: Launcher side of the contract, limits and the converter runner

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/ModelImport/ImportContracts.cs`, `ImportLimits.cs`, `ModelConverter.cs`, `ImportJobs.cs`
- Modify: `tests/DreamGrid.FakeChild/Program.cs` (new modes), `Launcher/DreamGrid.Launcher/Program.cs` (registrations)
- Test: `Launcher/DreamGrid.Launcher.Tests/ImportContractsTests.cs`, `ImportLimitsTests.cs`, `ModelConverterTests.cs`, `ImportJobsTests.cs`

**Interfaces:**
- Produces (namespace `DreamGrid.Launcher.Core.ModelImport`):
  - The records of Task 2, copied with identical JSON names, in `ImportContracts.cs`.
  - `static class ImportLimitsRules { static bool UploadAvailable(LauncherSettings s); static ImportLimits For(LauncherSettings s); }`. `UploadAvailable` = `PlanCatalog.RunsRobust(s)` and at least one region with `DeletedAt is null`. `For` takes, over those regions, the smallest `Options.MaxNonPhysicalSize ?? 256` and `Options.MaxPhysicalSize ?? 10`; `MinPartSize` 0.001; `MaxLinksetParts` 0 (the launcher writes no `LinksetPrims`; if it ever does, take the smallest non-zero value); the importer caps from Global Constraints.
  - `interface IModelConverter { Task<ConverterRun> CheckAsync(string inputFile, ImportLimits limits, ImportOptions options, string jobDir, CancellationToken ct); }`, `ConverterRun(ConverterOutcome Outcome, ImportReport? Report)`, `enum ConverterOutcome { Done, Failed, TimedOut }`.
  - `sealed class ModelConverter(LauncherPaths paths, TimeProvider time, ILogger<ModelConverter> log) : IModelConverter`: writes `limits.json` and `options.json` into `jobDir`, starts `dotnet <paths.RuntimeOpenSim>/GridGate.ModelImport.dll check ...` (host from `GridController.DotnetHost()`, which is `internal static` in the same assembly) with working directory `paths.RuntimeOpenSim`, environment `DOTNET_GCHeapHardLimit` = `0x60000000` (1.5 GB, so the converter fails with out-of-memory instead of growing; this is the plan's choice of memory limit on both platforms), kills the process tree after 2 minutes, and reads `report.json` on exit code 0. A `Func<ProcessStartInfo, ProcessStartInfo>? StartForTests` lets tests swap in FakeChild.
  - `sealed class ImportJobs(LauncherPaths paths, TimeProvider time)`: `string Create(Guid guest)` makes `<data>/state/uploads/<random 32-hex>/` and returns its path; `bool TryEnterSlot()` / `void LeaveSlot()` (one conversion on the install); `bool Owns(Guid guest, string jobId)`; `void Delete(string jobId)`; `void Sweep()` deletes folders older than 1 hour. A `HostedService` calls `Sweep` every 10 minutes. Add `Uploads => Path.Combine(Data, "state", "uploads")` to `LauncherPaths`.

- [ ] **Step 1: Write the failing tests:**
  - `Launcher_contract_reads_the_shared_fixtures`: each file in `tests/fixtures/model-import/` round-trips unchanged through the launcher's records (same check as Task 2).
  - `Limits_take_the_smallest_region_value`: regions with `MaxNonPhysicalSize` 128 and null → `MaxPartSize` 128; both null → 256; `MaxLinksetParts` 0.
  - `Upload_unavailable_on_region_and_connected_servers_and_without_regions`: `OwnerPlan.RegionServer` false, `OwnerPlan.Connected` false, Grid Asset with no regions false, Grid Asset with one region true.
  - `Converter_report_is_read` (FakeChild `--import-ok <outDir>` copies the fixture `report.json`): `Done`, report not null.
  - `Converter_failure_is_failed` (FakeChild `--crash`): `Failed`.
  - `Converter_hang_is_killed` (FakeChild `--import-hang`, test time limit lowered to 1 s through a constructor parameter): `TimedOut`, and the process is gone.
  - `Only_one_slot`: `TryEnterSlot` true, then false until `LeaveSlot`.
  - `Sweep_deletes_jobs_older_than_an_hour` (`FakeTimeProvider`).
  - `Job_folder_name_is_random_hex`: matches `^[0-9a-f]{32}$`.
- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: FAIL.
- [ ] **Step 3: Implement**, and add FakeChild modes `--import-ok <outDir>` and `--import-hang`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(launcher): run the model converter as a limited child process`.

### Task 9: Writing assets and items to Robust

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustAssets.cs`; Modify: `Launcher/DreamGrid.Launcher/Program.cs`; Test: `Launcher/DreamGrid.Launcher.Tests/RobustAssetsTests.cs`

**Interfaces:**
- Produces:
  - `interface IRobustAssets { Task StoreAsync(Guid id, int type, string name, byte[] data, Guid creator, CancellationToken ct); Task<Guid> UploadsFolderAsync(Guid owner, CancellationToken ct); Task AddItemAsync(Guid owner, Guid folder, ManifestItem item, CancellationToken ct); }`; failures throw `RobustAssetsUnavailableException(string message, Exception? inner)`.
  - `sealed class RobustAssets(HttpClient http, Func<LauncherSettings> settings) : IRobustAssets`, using the private port base the existing `RobustClient` uses (`GridEndpoints.ServiceBase`; add `GridServiceKind.Assets` and `GridServiceKind.Inventory` if the base differs per service, otherwise reuse).
  - `StoreAsync`: `POST /assets` with the `AssetBase` XML that `AssetServerPostHandler` deserializes (`FullID`, `ID`, `Name`, `Description`, `Type`, `Local` false, `Temporary` false, `CreatorID`, `Flags` `Normal`, `Data` base64). The reply's id must equal `id`.
  - `UploadsFolderAsync`: `POST /xinventory` `METHOD=GETROOTFOLDER&PRINCIPAL=<owner>`, then `GETFOLDERCONTENT` of the root to find a folder named `Uploads`; if missing, `ADDFOLDER` (`ID` new, `ParentID` root, `Name` `Uploads`, `Type` -1, `Version` 1, `Owner` owner). Returns its id.
  - `AddItemAsync`: `ADDITEM` with `AssetType` 6, `InvType` 6, `AssetID`, `Name`, `Description`, `CreatorId`, `Owner`, `Folder`, base/current masks `All | Export` (as numbers), `NextPermissions` from the item, `EveryOnePermissions` 0, `GroupPermissions` 0, `CreationDate` now (Unix seconds), `Flags` 0. Field names are exactly those `XInventoryInConnector.BuildItem` reads; check them there.

- [ ] **Step 1: Write the failing tests** against a fake HTTP handler that records requests and answers like Robust: `Store_posts_asset_xml` (the body deserializes with `XmlSerializer(typeof(AssetBase))`-shaped XML: assert element values); `Uploads_folder_is_found_not_duplicated`; `Uploads_folder_is_created_when_missing`; `Add_item_sends_object_type_and_permissions`; `Robust_down_throws_unavailable`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(launcher): store assets and inventory items through Robust`.

### Task 10: The upload right (staff side)

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/UploadRights.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/PeopleRules.cs` (`PersonTask` gains `AllowUpload`, `StopUpload`), `Launcher/DreamGrid.Launcher/Security/PeopleActions.cs`, `Pages/People/Person.cshtml(.cs)`, `Program.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/UploadRightsTests.cs`, `PersonUploadRightTests.cs`

**Interfaces:**
- Produces: `sealed class UploadRights(string file, TimeProvider time, ILogger<UploadRights>? log)` over `JsonStateFile<UploadRightsData>` at `<data>/guests/upload-rights.json` (same pattern as `RegionRights`): `bool Has(Guid person)`, `void Grant(Guid person, string name)`, `void Remove(Guid person)`. A missing or damaged file means nobody has the right.
- The two tasks follow the `Inventory` task's permission rule in `PeopleRules` (owner and admins, on residents). Each writes audit `upload-right-changed` with target `"<Full Name>: off -> on"` or `"... on -> off"` (the `IsAbout` format the Person page's history already matches).

- [ ] **Step 1: Write the failing tests:** `Damaged_file_means_nobody`; `Grant_then_remove`; `Admin_switches_upload_on_and_it_is_audited`; `Supervisor_cannot_switch_upload` (forbidden or no button); `Person_page_shows_upload_state` ("May upload models: Yes / No").
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** On the Person page, one row "May upload models" with a toggle button posting `task=AllowUpload` or `StopUpload` through the existing `OnPostTaskAsync`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(launcher): per-guest switch for uploading models`.

### Task 11: The guest pages

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/ModelImport/UploadCharge.cs`, `Launcher/DreamGrid.Launcher/ModelImport/FindingText.cs`, `Pages/Guest/Upload.cshtml(.cs)`, `Pages/Guest/UploadReport.cshtml(.cs)`, `Pages/Guest/UploadLimits.cshtml(.cs)`
- Modify: `Pages/Guest/Index.cshtml(.cs)` (link), `Program.cs` (registrations, request size limit for the upload route = 64 MB)
- Test: `Launcher/DreamGrid.Launcher.Tests/UploadPagesTests.cs`, `FindingTextTests.cs`

**Interfaces:**
- Consumes: `IModelConverter`, `ImportJobs`, `ImportLimitsRules`, `IRobustAssets`, `UploadRights`, `AuditLog`, `GuestSignIn.Read(User)`.
- Produces:
  - `interface IUploadCharge { Task<ChargeResult> TryChargeAsync(Guid guest, ImportReport report, CancellationToken ct); }`, `ChargeResult(bool Allowed, string? Message)`, `sealed class FreeUploads : IUploadCharge` (always allowed). Registered as a singleton.
  - `static class FindingText { static FindingLine Of(Finding f, string itemName); }`, `FindingLine(string Title, string Why, string Tip)`. One entry per finding code from Task 2; values substituted; a code it doesn't know gives a generic line, never an exception.
  - Routes: `GET/POST /guest/upload` (form), `GET /guest/upload/{job}` (report), `POST /guest/upload/{job}` handlers `Rescale` and `Make`, `GET /guest/upload/limits`.
- Visibility: every route returns 404 unless `ImportLimitsRules.UploadAvailable(settings())` and `UploadRights.Has(guest)`. The hub link "Upload a model" shows under the same rule.

- [ ] **Step 1: Write the failing tests** (pattern of `MyRegionsTests`: `LauncherFactory`, `/__test/guest-signin`, AngleSharp, a fake `IModelConverter` and fake `IRobustAssets` registered in the factory):
  - `Upload_hidden_without_right` / `Upload_hidden_on_region_server`: 404 and no hub link.
  - `Upload_shows_report`: the fake converter returns the fixture report: page shows the safe line for `split-materials`, the warning box headed "**Castle may look distorted or shaped wrong.** You should correct:" listing the risky findings in order, and a `too-big` block with a scale field pre-filled 85; **Make the item** is absent because it's blocked.
  - `Rescale_runs_the_check_again_with_the_percent`: the fake converter sees `ScalePercent` 85.
  - `Make_charges_once_then_writes_then_audits`: call order recorded by fakes: `TryChargeAsync`, then every `StoreAsync`, then `UploadsFolderAsync`, then `AddItemAsync`; audit `model-uploaded` with target containing the item name and asset ids; done page says "Castle is in your inventory, in the Uploads folder. If you're logged in, log out and back in to see it."; job folder deleted.
  - `Charge_refused_stops_before_writing`: no `StoreAsync`; the charge message is shown.
  - `Make_the_item_twice_makes_one_item` (Review Focus 3): two POSTs to `Make` for the same job: one `AddItemAsync`, one charge; the second request shows the done page (or "This upload is already finished") without writing.
  - `Hostile_file_name_is_cleaned` (Review Focus 4): upload named `../../evil<script>🙂` plus 300 characters: the job folder is hex, the item name defaults to the cleaned name (path parts removed, control and `<>` characters removed, trimmed to 63 characters, the item-name limit), and it shows escaped.
  - `Another_guests_job_is_404`.
  - `Busy_slot_says_try_again`: `TryEnterSlot` false → "The uploader is busy, try again in a minute".
  - `Robust_down_shows_nothing_charged`: `StoreAsync` throws: "That didn't work, nothing was charged", and the error is logged.
  - `Converter_failed_says_couldnt_read`: `Failed` and `TimedOut` both show "We couldn't read this file".
  - `Limits_page_lists_this_grids_values`: shows 256 m (or the region's lower value), 8 faces, 1024 px, 64 MB, and on a Grid Asset plan the sentence that regions on joined computers aren't checked.
  - `FindingTextTests.Every_code_has_text`: every code in Task 2's list gives a non-generic line with a non-empty tip.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Upload form: one file input (accept `.glb,.gltf`), optional item name, the anti-forgery token. Save the file as `<job>/input.glb` (or `.gltf`), enter the slot, run the check, leave the slot (in `finally`), redirect to the report. The report page keeps next-owner tick boxes (all on) and the item name; **Make the item** posts them. Order inside `Make`: check ownership and that `manifest.json` exists and is valid (types are 0, 6 or 49; files exist inside the job folder; at most 1 object asset; total size ≤ 64 MB) → mark the job finished (a `done` file written atomically before anything else, which is what makes a second POST a no-op) → charge → store assets → folder → item → audit → delete job. On failure after the `done` mark, write the error to the log and delete the job.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(launcher): guest upload pages with check report and Make the item`.

### Task 12: End-to-end check on the VM and on Windows

**Files:**
- Create: `docs/superpowers/specs/2026-11-10-model-import-results.md` (what was tried and seen)

- [ ] **Step 1:** Build the branch's package and install it on the Debian 12 test VM as the debian12-test-vm notes describe; on Windows run the launcher from the build.
- [ ] **Step 2:** As the owner, switch **May upload models** on for a test resident.
- [ ] **Step 3:** As that resident, upload Box, Avocado, Damaged Helmet, Flight Helmet and A Beautiful Game. For each, write down the report's findings.
- [ ] **Step 4:** Log in with Firestorm (log out and back in if already on), rez each item from Uploads, and check by eye: right size, right place for each part, textured, colours right, nothing inside out. Screenshot each.
- [ ] **Step 5:** Upload the 300 m case (Sponza scaled in Blender, or a generated model) and confirm the scale offer works; upload a Draco file and confirm the message.
- [ ] **Step 6:** Record the results, including the converter's time and peak memory for each model, and propose values for the `heavy-mesh` threshold and the detail-level ratios. Commit `docs: model import proof-of-concept results`.

The proof of concept passes when Step 4 looks right on both platforms.
