# Plan 12b — Splash page: pictures — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Staff upload pictures for the splash page (background, picture blocks, block backgrounds, places) and the grid's one logo; every picture is checked by its contents and redrawn; residents see only pictures the published page uses.

**Architecture:** `PictureStore` (Core) streams an upload to a part file, opens it with SkiaSharp, refuses anything that isn't a PNG, JPEG or WebP within the size limits, redraws it (orientation applied, EXIF and anything hidden dropped, scaled to fit 3840 px) under a random name, and keeps a small index. One store for splash pictures (`Data/splash/images`), one for the theme (`Data/theme/images`). `SplashStore.PictureExists` checks the splash store, so designs can finally hold pictures. An hourly job removes splash pictures nothing has used for 30 days.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs and Razor, **SkiaSharp** (MIT; owner's choice 2026-11-01) with `SkiaSharp.NativeAssets.Linux.NoDependencies`, xUnit v3 with `LauncherFactory`.

**Spec:** `docs/superpowers/specs/2026-11-01-viewer-splash-design.md` (§4.2 blocks, §4.4 pictures, §4.8 audit). Builds on plan 12a (`docs/superpowers/plans/2026-11-01-plan12a-splash-page.md`, merged in PR 24).

## Global Constraints

- Accepted: PNG, JPEG and WebP, judged by the file's contents (SkiaSharp's detected format), never by its name or the request's content type. No SVG, no GIF.
- At most **10 MB** per upload (10 × 1024 × 1024 bytes); beyond that the upload stops and nothing is kept.
- Before decoding, refuse a picture whose header says more than **50 megapixels** (decompression bombs).
- Every picture is **redrawn**: orientation from EXIF applied, then EXIF and every other extra dropped, scaled down (never up) so neither side exceeds **3840 px**. PNG stays PNG, JPEG stays JPEG (quality 90), WebP stays WebP (quality 90).
- Stored under a launcher-chosen name `^[0-9a-f]{32}\.(png|jpg|webp)\z` in the owner-only data folder; the uploader's file name is only a label (cleaned, at most 120 characters).
- Public picture routes serve only pictures the **published** splash design uses (or the current logo), with the right content type, `X-Content-Type-Options: nosniff` (already set site-wide), and `Cache-Control: public, max-age=31536000, immutable` (names never change content).
- Staff routes are owner and admins (AdminPolicy), 404 where there is no guest side, antiforgery header `RequestVerificationToken` on every change, as `/api/splash` does.
- Audit: `splash-image-uploaded` (target: the stored name and the label), `splash-image-deleted`, `theme-logo-set`, `theme-logo-removed`.
- Security headers and the inline-script/inline-style scan unchanged. Pictures in the public page are CSS backgrounds from the generated stylesheet or `<img>` with a `/…` source; never `style=`.
- Works the same on Windows and Debian 12 and 13: the package must carry SkiaSharp's native library for `win-x64` and `linux-x64`.
- Names, comments and file layout follow the 12a code (`Core/Splash/*`, `Launcher/Splash/*`, `Endpoints/SplashApi.cs`).

## Review Focus

1. **A picture that is not what its name says.** An HTML or SVG file named `.png`, a GIF, a truncated JPEG: refused with a plain message and nothing left in the folder. Test: `Wrong_content_is_refused_and_nothing_kept` in Task 1.
2. **An upload cut off halfway** (the browser goes away, or it passes 10 MB): no part file left behind. Test: `Oversize_upload_leaves_nothing` in Task 1 and `Api_oversize_upload_is_413` in Task 3.
3. **Deleting a picture the page still uses.** Refused while the draft or the published design names it, so the public page never loses a picture under it. Test: `Picture_in_use_cannot_be_deleted` in Task 3.
4. **A draft picture leaking before publish.** A picture only the draft uses must 404 on the public route. Test: `Draft_only_picture_is_not_public` in Task 3.
5. **The clean-up removing a picture someone just chose.** A picture that becomes used again before its 30 days are up must survive. Test: `Cleanup_spares_a_picture_used_again` in Task 2.

---

## File map

Core (`Launcher/DreamGrid.Launcher.Core/`):
- Modify `DreamGrid.Launcher.Core.csproj`: `SkiaSharp`, `SkiaSharp.NativeAssets.Linux.NoDependencies` (same exact 3.x version for both, the newest stable on nuget.org at build time).
- Create `Pictures/PictureStore.cs`: upload, check, redraw, index, open, delete.
- Create `Splash/SplashPictures.cs`: which picture names a design uses.
- Modify `Grid/GridRegions.cs`: regions carry their map square (X, Y).
- Modify `LauncherPaths.cs`: `SplashImages`, `Theme`, `ThemeImages`.
- Create `Theme/ThemeStore.cs`: the grid's logo name.

Web (`Launcher/DreamGrid.Launcher/`):
- Create `Splash/SplashPictureCleanup.cs`: the hourly clean-up.
- Modify `Endpoints/SplashApi.cs`: picture routes, map-tile copy, logo routes.
- Modify `Splash/SplashSnapshot.cs`: the logo name in the view.
- Modify `Pages/Shared/_SplashBlock.cshtml`, `Core/Splash/SplashCss.cs`, `wwwroot/css/splash.css`: draw pictures and the logo.
- Modify `Program.cs`, `Security/AdminNetworkFilter.cs` (`/theme`).

Tests (`Launcher/DreamGrid.Launcher.Tests/`): `PictureStoreTests.cs`, `SplashPicturesTests.cs`, `SplashPictureApiTests.cs`, `ThemeLogoTests.cs`; additions to `SplashCssTests.cs`, `WelcomePageTests.cs`, `AccessTests.cs`; a `TestPictures.cs` helper. Integration: `GridRegionsTests.cs`.

Test command for the plan: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~Picture|FullyQualifiedName~Splash|FullyQualifiedName~WelcomePage|FullyQualifiedName~ThemeLogo"`. Full suite (about 9 minutes) once at the end.

---

### Task 1: The picture store (check, redraw, keep)

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/DreamGrid.Launcher.Core.csproj`, `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Pictures/PictureStore.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/TestPictures.cs`, `Launcher/DreamGrid.Launcher.Tests/PictureStoreTests.cs`

**Interfaces:**
- Produces (namespace `DreamGrid.Launcher.Core.Pictures`):
```csharp
public sealed record StoredPicture(string Name, string Label, int Width, int Height, long Bytes,
    DateTimeOffset Added, string AddedBy, DateTimeOffset? UnusedSince);

public abstract record PictureResult
{
    public sealed record Added(StoredPicture Picture) : PictureResult;
    /// Message: one plain sentence for the owner ("That file isn't a PNG, JPEG or WebP picture.").
    public sealed record Refused(string Message) : PictureResult;
    public sealed record TooLarge : PictureResult;
}

/// One folder of redrawn pictures plus index.json beside them. Safe to call from any thread.
public sealed class PictureStore(string folder, TimeProvider time, ILogger? log = null)
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxSide = 3840;
    public long MaxPixels { get; init; } = 50_000_000;     // tests lower it
    public static bool IsName(string? name);              // ^[0-9a-f]{32}\.(png|jpg|webp)\z
    public Task<PictureResult> AddAsync(Stream source, string? label, string addedBy, CancellationToken ct);
    public bool Exists(string name);
    public StoredPicture? Get(string name);
    public IReadOnlyList<StoredPicture> List();            // newest first
    /// The file and its content type, or null for an unknown or badly formed name.
    public (string Path, string ContentType)? Open(string name);
    public bool Delete(string name);                       // false when unknown
    /// Records which names are in use now (clears UnusedSince for those, sets it to now for the rest that have none).
    public void MarkUse(IReadOnlySet<string> inUse);
}
```
- `LauncherPaths`: `SplashImages => Path.Combine(Splash, "images")`, `Theme => Path.Combine(Data, "theme")`, `ThemeImages => Path.Combine(Theme, "images")`.
- Part files go to `<folder>/.incoming/<guid>.part` (`FileMode.CreateNew`), deleted in a `finally` whatever happens. The index is a `JsonStateFile`. The redrawn file is written to a temp name and moved into place, then the index row is added.
- Label cleaning: file name only (no folders), control characters removed, trimmed, cut to 120 characters, `"Picture"` when empty.
- Messages: wrong type → "That file isn't a PNG, JPEG or WebP picture."; damaged → "That picture couldn't be read; it may be damaged."; too many pixels → "That picture is too big to use (more than 50 megapixels)."

- [ ] **Step 1: Add the packages** and build once (`dotnet build Launcher`), so the tests can draw pictures.
- [ ] **Step 2: Write `TestPictures`** (test helper, SkiaSharp): `byte[] Png(int w, int h)`, `Jpeg(int w, int h)`, `Webp(int w, int h)`, `Gif()` (the 35-byte 1×1 GIF89a as a literal), and `JpegWithExif(int w, int h)`: a `Jpeg` with an APP1 segment inserted right after the `FF D8` start marker, holding `"Exif\0\0"` and the ASCII text `"GPSLatitude 51.5"`.
- [ ] **Step 3: Write the failing tests** in `PictureStoreTests` (temp folder, `FakeTimeProvider`):
  - `Png_jpeg_and_webp_are_kept_redrawn` (Theory over the three): `Added`; the name matches `IsName` with the right extension; `Open` gives that file with `image/png` / `image/jpeg` / `image/webp`; the stored bytes differ from the upload's; `Width`/`Height` right.
  - `Wrong_content_is_refused_and_nothing_kept` (Theory): `"<html><script>alert(1)</script></html>"` bytes, `"<svg xmlns='http://www.w3.org/2000/svg'/>"` bytes, `Gif()`, and the first half of a `Jpeg(200, 200)` → `Refused`; afterwards the folder holds no picture and `.incoming` is empty.
  - `Exif_is_gone_after_redrawing`: `JpegWithExif` → the stored file contains neither `"Exif"` nor `"GPSLatitude"`.
  - `Big_pictures_are_scaled_to_3840`: `Png(5000, 1000)` → stored 3840 × 768. `Png(800, 600)` stays 800 × 600 (never scaled up).
  - `Too_many_pixels_is_refused_before_decoding`: store with `MaxPixels = 100`, `Png(20, 20)` → `Refused` with the megapixels message.
  - `Oversize_upload_leaves_nothing`: a stream of `MaxUploadBytes + 1` bytes → `TooLarge`; `.incoming` empty, index empty.
  - `Label_is_cleaned`: label `"..\\..\\evil\u0007name.png"` → `"evilname.png"`; a 300-character label → 120 characters; null → `"Picture"`.
  - `Names_are_checked`: `Open("../index.json")`, `Open("ABC…")`, `Open(name + "\n")` → null; `Delete` of an unknown name → false.
  - `Index_survives_a_restart`: add, then a new `PictureStore` on the same folder lists it with the same label and `AddedBy`.
  - `MarkUse_sets_and_clears_unused_since`: two pictures; `MarkUse({a})` → b has `UnusedSince` = now, a has null; advance a day, `MarkUse({a, b})` → both null.
- [ ] **Step 4: Run, see them fail** (build error: no `PictureStore`).
- [ ] **Step 5: Implement `PictureStore`.** Open with `SKCodec.Create(stream, out result)`; accept only `SKEncodedImageFormat.Png`, `Jpeg`, `Webp`; check `codec.Info.Width * Height` against `MaxPixels` before `GetPixels`; apply `codec.EncodedOrigin` when drawing; scale with `SKSamplingOptions(SKCubicResampler.Mitchell)`; encode with `SKImage.Encode(format, 90)` (PNG ignores quality).
- [ ] **Step 6: Run, all pass.**
- [ ] **Step 7: Commit** — `"Pictures: uploads are checked by their contents and redrawn under a new name"`.

---

### Task 2: Pictures in designs, and the clean-up

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Splash/SplashPictures.cs`, `Launcher/DreamGrid.Launcher/Splash/SplashPictureCleanup.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register the splash `PictureStore` on `paths.SplashImages`; `SplashStore` gets `PictureExists = pictures.Exists`; register and host the clean-up)
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashPicturesTests.cs`

**Interfaces:**
- Consumes: `PictureStore` (Task 1), `SplashStore.Draft()/Published()`, `SplashDesign`.
- Produces:
```csharp
public static class SplashPictures
{
    /// Every picture name a design refers to: page background, block backgrounds, Picture content, place pictures.
    public static IReadOnlySet<string> UsedBy(SplashDesign? design);
    /// Names the draft or the published design uses (what may not be deleted, and what the clean-up keeps).
    public static IReadOnlySet<string> InUse(SplashStore store);
}
public sealed class SplashPictureCleanup(PictureStore pictures, SplashStore store, AuditLog audit, TimeProvider time,
    ILogger<SplashPictureCleanup> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan KeepUnused = TimeSpan.FromDays(30);
    public Task RunOnceAsync(CancellationToken ct);   // MarkUse(InUse), then delete those unused for KeepUnused or more
}
```
- The DI registration distinguishes the two stores by a keyed service: `AddKeyedSingleton("splash", …)` and `AddKeyedSingleton("theme", …)`; consumers use `[FromKeyedServices("splash")]`.
- The clean-up copies `SignUpCleanup`'s loop exactly (first run after 5 minutes, then hourly, errors logged and the loop goes on). Each removal is audited as `splash-image-deleted` by `"DreamGrid"` with result `"unused for 30 days"`.

- [ ] **Step 1: Write the failing tests:**
  - `UsedBy_finds_every_kind_of_reference`: a design with a page background, a block background, a Picture block and a place picture → exactly those four names; `UsedBy(null)` → empty.
  - `Designs_with_stored_pictures_can_be_saved`: with the store wired as in `Program.cs`, a design whose Picture block names an added picture saves; one naming a made-up (well-formed) name gets `Invalid`.
  - `InUse_counts_draft_and_published`: a name only the draft uses and a name only the published design uses are both in `InUse`; a name neither uses is not.
  - `Cleanup_removes_pictures_unused_for_30_days`: add a picture, `RunOnceAsync` (marks it unused), advance 30 days, `RunOnceAsync` → gone, audited.
  - `Cleanup_spares_a_picture_used_again`: add, run (unused), advance 20 days, save a draft using it, run, advance 20 days, run → still there.
  - `Cleanup_keeps_published_pictures`: published uses it; advance 60 days with runs → still there.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement** `SplashPictures`, `SplashPictureCleanup` and the registrations.
- [ ] **Step 4: Run, pass.**
- [ ] **Step 5: Commit** — `"Splash: designs can name stored pictures, and pictures nothing uses go after 30 days"`.

---

### Task 3: Picture routes (upload, list, delete, public)

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/SplashApi.cs`
- Modify: `Launcher/DreamGrid.Launcher.Tests/AccessTests.cs` (allow-list: `route /welcome/images/{name}`)
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashPictureApiTests.cs`

**Interfaces:**
- Consumes: `PictureStore` ("splash"), `SplashPictures.InUse`, `SplashStore.Published()`, `AuditLog`.
- Produces:

| Route | Who | Body | Answer |
|---|---|---|---|
| `GET /api/splash/pictures` | staff | — | `{ pictures: [{ name, label, width, height, bytes, added, addedBy, inUse }] }` |
| `POST /api/splash/pictures?label=…` | staff | the raw file (`application/octet-stream`) | 200 `StoredPicture`; 400 `{ problem }`; 413 |
| `GET /api/splash/pictures/{name}` | staff | — | the file (draft pictures too) |
| `DELETE /api/splash/pictures/{name}` | staff | — | 204; 409 `{ problem: "The page still uses that picture." }`; 404 |
| `GET /welcome/images/{name}` | anyone | — | the file only when `SplashPictures.UsedBy(published)` holds it, else 404 |

- The upload is the raw body (no multipart), so the editor sends `fetch(url, { method: 'POST', body: file })`. The size limit is lifted for this endpoint only (`IHttpMaxRequestBodySizeFeature`, set to `MaxUploadBytes + 1`), and `PictureStore` stops at `MaxUploadBytes`, so both Kestrel and the store refuse a bigger body.
- The antiforgery header is checked **before** any byte of the body is read.
- `/welcome/images/{name}`: `Cache-Control: public, max-age=31536000, immutable`. It is already under `/welcome`, so `AdminNetworkFilter` lets it through.

- [ ] **Step 1: Write the failing tests** (`LauncherFactory`, staff clients and the dashboard token as in `SplashApiTests`):
  - `Owner_and_admin_can_upload_and_list` (Theory) → 200, listed with its label and `inUse: false`.
  - `Supervisor_and_anonymous_cannot_upload` → 403 / not 200; nothing in `Data/splash/images`.
  - `Upload_without_token_reads_nothing`: no header → 400 and the folder has no part file.
  - `Wrong_type_answers_problem`: SVG bytes → 400 with `"That file isn't a PNG, JPEG or WebP picture."`.
  - `Api_oversize_upload_is_413`: a body of `MaxUploadBytes + 1` → 413; nothing kept.
  - `Picture_in_use_cannot_be_deleted`: upload, save a draft using it → DELETE 409; discard the draft → DELETE 204; the upload and the deletion are audited (`splash-image-uploaded`, `splash-image-deleted`).
  - `Draft_only_picture_is_not_public`: upload, draft uses it → `/welcome/images/{name}` 404 anonymously, `/api/splash/pictures/{name}` 200 for staff; publish → public 200 with `image/png` and the immutable cache header.
  - `Public_route_refuses_bad_names`: `/welcome/images/..%2Fdraft.json`, `/welcome/images/settings.json` → 404.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement the routes in `SplashApi.Map`.**
- [ ] **Step 4: Run, pass; run `AccessTests`.**
- [ ] **Step 5: Commit** — `"Splash: staff upload, list and delete pictures; residents see only the published page's"`.

---

### Task 4: A region's map tile as a picture

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridRegions.cs` (`GridRegion(Guid Id, string Name, int X, int Y)`; query `SELECT uuid, regionName, locX, locY FROM regions`; X = locX / 256, Y = locY / 256)
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/SplashApi.cs`, `Launcher/DreamGrid.Launcher.IntegrationTests/GridRegionsTests.cs` (insert `locX = 256000, locY = 256512`, expect X 1000, Y 1002), the fakes and tests that build `GridRegion`
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashPictureApiTests.cs`

**Interfaces:**
- Consumes: `IGridRegions`, the `RegionsApi.RobustMapClient` named `HttpClient`, `PictureStore.AddAsync`.
- Produces: `POST /api/splash/pictures/from-region/{id:guid}` (staff, antiforgery) → 200 `StoredPicture` with label `"Map of {region name}"`; 404 `{ problem }` when the region isn't in Robust's list or Robust has no tile for it; 503 `{ problem }` when the region list is unavailable. It fetches `http://127.0.0.1:{RobustPublicPort}/map-1-{X}-{Y}-objects.jpg` exactly as `RegionsApi`'s tile proxy does, then puts the bytes through `AddAsync` (so the tile is redrawn and named like any upload). A "refresh picture" in 12c's editor is the same call again.
- `GET /api/splash` gains `regions: [{ id, name }]` (from `IGridRegions`; empty when unavailable), which 12c's Places picker uses.

- [ ] **Step 1: Write the failing tests** (replace the `RobustMapClient` handler through `LauncherFactory.ExtraServices` with `services.AddHttpClient(RegionsApi.RobustMapClient).ConfigurePrimaryHttpMessageHandler(() => fake)`, the fake answering a `TestPictures.Jpeg(256, 256)` for the expected URL):
  - `Region_tile_becomes_a_picture`: `Regions.Result` holds region (1000, 1002) → 200, label `"Map of Sandbox"`, and the fake saw `/map-1-1000-1002-objects.jpg`.
  - `Unknown_region_is_404`, `Missing_tile_is_404` (fake answers 200 `text/plain` with an empty body, as Robust does), `Region_list_unavailable_is_503`.
  - `Splash_api_lists_regions`: `GET /api/splash` includes the fake's regions by id and name.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, pass. Build the integration tests** (`dotnet build Launcher/DreamGrid.Launcher.IntegrationTests`); they run only where MariaDB and OpenSim are set up (CI).
- [ ] **Step 5: Commit** — `"Splash: a region's map tile can be copied in as a picture"`.

---

### Task 5: Drawing pictures on the page

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Splash/SplashCss.cs`, `Launcher/DreamGrid.Launcher/Pages/Shared/_SplashBlock.cshtml`, `Launcher/DreamGrid.Launcher/wwwroot/css/splash.css`
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashCssTests.cs`, `Launcher/DreamGrid.Launcher.Tests/WelcomePageTests.cs`

**Interfaces:**
- Consumes: `SplashCss.Build(design, pictureUrl)` (12a), `SplashSnapshot.PictureUrl` (`/welcome/images/{name}`).
- Produces:
  - `SplashCss.PictureClassOf(SplashBlock b) => ClassOf(b) + "-pic"` and `SplashCss.PlaceClassOf(SplashBlock b, int index) => ClassOf(b) + "-place-" + index`.
  - `Build` writes, for a Picture block, a rule for `.{PictureClassOf}` with the picture's fit (the same mapping as backgrounds, focus 50 % 50 %, no darkening); for each place with a picture, a rule for `.{PlaceClassOf}`.
  - `_SplashBlock`: Picture → `<div class="splash-picture {PictureClassOf}" role="img" aria-label="{caption or 'Picture'}"></div>` and the caption under it when set; Places → each place with a picture gets `<div class="splash-place-picture {PlaceClassOf}" role="img" aria-label="{region name}"></div>` above its name.
  - `splash.css`: `.splash-picture { width: 100%; height: 100%; }` (shorter by the caption's line when there is one: the block is a column flex box), `.splash-place-picture { aspect-ratio: 1; width: 100%; }`, places laid out as a wrapping row.

- [ ] **Step 1: Write the failing tests:**
  - `SplashCssTests.Picture_block_gets_its_own_rule`: `.b-1-pic{background-image:url("/x/{name}");background-size:contain` for a Fit picture.
  - `SplashCssTests.Place_pictures_get_rules`: two places, the second with a picture → only `.b-1-place-1{` is written.
  - `WelcomePageTests.Published_picture_shows`: upload (through the store), publish a design with a Picture block captioned "Our beach" → the page has `.splash-picture.b-1-pic[role=img][aria-label='Our beach']`, the stylesheet names `/welcome/images/{name}`, and that address answers 200.
  - `WelcomePageTests.Page_background_picture_shows`: published page background picture → `body.splash{…background-image:url("/welcome/images/{name}")` in `page-1.css`.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, pass; run `InlineScriptScanTests`.**
- [ ] **Step 5: Commit** — `"Splash: pictures, block backgrounds and place pictures show on the page"`.

---

### Task 6: The grid's logo

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Theme/ThemeStore.cs`
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/SplashApi.cs` (theme routes), `Launcher/DreamGrid.Launcher/Splash/SplashSnapshot.cs` (`SplashView` gains `string? Logo`), `Launcher/DreamGrid.Launcher/Pages/Shared/_SplashBlock.cshtml`, `Launcher/DreamGrid.Launcher/Security/AdminNetworkFilter.cs` (`"/theme"`), `Launcher/DreamGrid.Launcher/Program.cs`, `Launcher/DreamGrid.Launcher.Tests/AccessTests.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/ThemeLogoTests.cs`

**Interfaces:**
- Consumes: `PictureStore` ("theme", on `paths.ThemeImages`).
- Produces:
```csharp
namespace DreamGrid.Launcher.Core.Theme;
/// The grid-wide look (spec §4.2: the logo is the grid's one logo, for the splash page now and other pages later).
/// Data/theme/theme.json holds the current logo's picture name.
public sealed class ThemeStore(LauncherPaths paths, ILogger<ThemeStore>? log = null)
{
    public string? Logo();                 // null when none, or when the file named is gone
    public void SetLogo(string? name);     // the old logo's file is deleted from the theme store by the caller
}
```

| Route | Who | Answer |
|---|---|---|
| `POST /api/theme/logo?label=…` | owner and admins, antiforgery, raw body | 200 `StoredPicture`; 400; 413 — replaces the old logo and deletes its file |
| `DELETE /api/theme/logo` | owner and admins, antiforgery | 204 |
| `GET /theme/images/{name}` | anyone | the current logo only (else 404), immutable cache |

- Audited as `theme-logo-set` (target: the name) and `theme-logo-removed`. 404 where there is no guest side, like `/api/splash`.
- A GridName block with `ShowLogo` and a logo set draws `<img class="splash-logo" src="/theme/images/{name}" alt="{grid name}">` before the name; with no logo set it draws nothing for the logo. `splash.css`: `.splash-logo { max-height: 100%; max-width: 100%; }` (with the name beside it in a flex row).
- `SplashSnapshot` reads the logo when it builds (so it's cached a minute too) and `Invalidate` is called after a logo change.

- [ ] **Step 1: Write the failing tests:**
  - `Owner_sets_logo_and_page_shows_it`: upload a logo, publish a GridName block with `ShowLogo` → `/welcome` has `img.splash-logo[src='/theme/images/{name}'][alt='Avalon']`, and that address answers 200 anonymously.
  - `Replacing_the_logo_removes_the_old_file`: set twice → the first name 404s and its file is gone from `Data/theme/images`.
  - `Removing_the_logo`: DELETE → 204, page has no `img.splash-logo`, the address 404s.
  - `Only_the_current_logo_is_public`: a made-up name → 404.
  - `Supervisor_cannot_set_logo` → 403.
  - `Logo_changes_are_audited`.
  - `Theme_images_reachable_with_admin_network_list` (as 12a's `Welcome_reachable_with_admin_network_list`).
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, pass. Then run the full suite once** (`dotnet test Launcher/DreamGrid.Launcher.Tests`): 0 failed.
- [ ] **Step 5: Commit** — `"Theme: the grid's logo, shown on the splash page"`.

---

### Task 7: Packages, the VM and Firestorm

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.7: pictures done, and what Firestorm showed)

- [ ] **Step 1:** `gh workflow run release.yml --ref <branch>`; the Linux and Windows packages build and the smoke tests pass on Debian 12, Debian 13 and Windows. Check that the Linux tarball holds `runtimes/linux-x64/native/libSkiaSharp.so` and the Windows one `runtimes/win-x64/native/libSkiaSharp.dll`.
- [ ] **Step 2:** Install on the Debian 12 test VM (memory note "Debian 12 test VM") and check that the launcher starts and `/welcome` still answers (SkiaSharp's native library loads on a clean Debian). Uploading and publishing pictures by hand waits for plan 12c's editor; the Firestorm check of pictures and the logo is done then.
- [ ] **Step 3:** Under 2.7 in the work list, mark pictures built (12b) and note that their Firestorm check comes with 12c.
- [ ] **Step 4: Commit** — `"Worklist: splash pictures are built"`.
