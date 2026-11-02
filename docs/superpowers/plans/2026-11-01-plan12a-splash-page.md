# Plan 12a — Splash page: the stored design and the public page — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `/welcome` shows the owner's published splash design (or a built-in default), with live counts, at any viewer window size, and staff (owner and admins) can save a draft, publish it and discard it through a small API.

**Architecture:** A splash design is a JSON document (page settings + blocks) checked by `SplashRules` and kept by `SplashStore` as a draft and a published copy under `Data/splash/`. The public page is drawn from the published copy (or `SplashDefaults`) plus a 60-second cached status snapshot; all layout lives in a generated stylesheet `/welcome/page-{version}.css`, so there are no inline styles and no script.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages and minimal APIs, MySqlConnector, xUnit v3 with `LauncherFactory`, AngleSharp for HTML assertions.

**Spec:** `docs/superpowers/specs/2026-11-01-viewer-splash-design.md`

**This is the first of three plans** (spec §8):
- **12a (this plan):** design model, checks, store, whole-grid region count, status snapshot, stylesheet builder, default page, public `/welcome`, the draft/publish API, audit.
- **12b:** pictures: upload, re-encoding, `/welcome/images/{name}`, the picture list and clean-up, the grid logo (`Data/theme/logo`), copying a region's map tile.
- **12c:** the block editor at `/splash` (shelf, canvas, drag, resize, snap, guides, preview sizes, undo, autosave) on top of 12a's API and 12b's pictures.

In 12a no pictures exist yet: every picture reference fails the check, and the model's picture fields stay empty.

## Global Constraints

- Security headers unchanged (`Security/SecurityHeaders.cs`): `script-src 'self'`, `style-src 'self'`, `img-src 'self' data:`. No `<script>` without `src`, no `on*=`, no `style=`, no `<style>` in any `.cshtml` (`InlineScriptScanTests` must keep passing).
- `frame-ancestors 'none'` and `X-Frame-Options: DENY` stay.
- The public page shows counts only: never avatar names, region owners, IP addresses or settings.
- Who can change the design: owner and admins (AdminPolicy). Supervisors, guests and anonymous callers can't.
- `/welcome` reachable when `Security.AdminNetworks` is set (add it to `AdminNetworkFilter.PublicPaths`).
- On a Region Server or Connected Server (`!AdminRule.HasStaffPages(s)`), `/welcome` shows the default page and the API is 404.
- Files written with `AtomicFile`/`JsonStateFile`, UTF-8 without BOM, in the owner-only data folder.
- Public page and stylesheet: `Cache-Control: public, max-age=60`; the server keeps the built view 60 seconds.
- Text fields stored as `{ "en": "…" }`; only the `en` key is accepted until languages (1.5).
- Works the same on Windows and Debian 12 and 13. No new NuGet package in 12a.
- Comments and names follow the surrounding code: plain-English `<summary>` docs that say what and why, records for data, `sealed` classes.

## Review Focus

1. **A design saved for a different design size.** The owner changes the design width from 1280 to 1920 with blocks already placed: blocks inside the old size must still pass the check (positions are only checked against the current size), and the stylesheet must use the new size. Test: `Check_uses_current_design_size` in Task 1.
2. **Two staff editing at once.** The second save, based on an older draft version, must be refused with a conflict and must not overwrite. Test: `Stale_draft_save_is_a_conflict` in Task 2, and `Api_stale_save_returns_409` in Task 6.
3. **The database is down while viewers log in.** `/welcome` must still return 200 with "Status unavailable" and must not wait more than the 3-second timeout per 60-second window. Test: `Welcome_shows_unavailable_when_database_down` in Task 5.
4. **A damaged or hand-edited `published.json`.** A file that fails `SplashRules.Check` on load must not be drawn: show the default page and log one warning. Test: `Invalid_published_file_shows_default` in Task 5.
5. **Text that looks like HTML.** A news item containing `<script>` or `<img onerror>` must come out as text. Test: `Block_text_is_not_html` in Task 5.

---

## File map

Core (`Launcher/DreamGrid.Launcher.Core/`):
- Create `Splash/SplashDesign.cs`: the records and enums of the design.
- Create `Splash/SplashRules.cs`: the check.
- Create `Splash/SplashDefaults.cs`: the default design.
- Create `Splash/SplashStore.cs`: draft and published files.
- Create `Splash/SplashCss.cs`: design → stylesheet.
- Create `Grid/GridRegions.cs`: whole-grid region list from Robust's `regions` table.
- Modify `LauncherPaths.cs`: `Splash` folder.

Web (`Launcher/DreamGrid.Launcher/`):
- Create `Splash/SplashSnapshot.cs`: the cached view (design + status).
- Create `Endpoints/SplashApi.cs`: the draft/publish API and the stylesheet route.
- Create `Pages/Shared/_SplashLayout.cshtml`, `Pages/Shared/_SplashBlock.cshtml`.
- Replace `Pages/Welcome.cshtml`; create `Pages/Welcome.cshtml.cs`.
- Create `wwwroot/css/splash.css`: the fixed rules every splash page shares.
- Modify `Program.cs` (registrations, routes), `Security/AdminNetworkFilter.cs` (`/welcome`).

Tests (`Launcher/DreamGrid.Launcher.Tests/`):
- Create `SplashRulesTests.cs`, `SplashStoreTests.cs`, `SplashCssTests.cs`, `WelcomePageTests.cs`, `SplashApiTests.cs`.
- Modify `FakeGridController.cs` (add `FakeGridRegions`), `LauncherFactory.cs` (register it).

Test command for the whole plan: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~Splash|FullyQualifiedName~WelcomePage"`. The full suite takes about 9 minutes; run it once at the end.

---

### Task 1: The design model and its check

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Splash/SplashDesign.cs`, `Launcher/DreamGrid.Launcher.Core/Splash/SplashRules.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashRulesTests.cs`

**Interfaces:**
- Produces (namespace `DreamGrid.Launcher.Core.Splash`):
```csharp
public enum SplashFit { Fill, Fit, Stretch, Tile, Centre }
public enum SplashBlockKind { Text, Picture, GridName, Status, News, Places, Links }
public enum SplashAlign { Left, Centre, Right }
public enum SplashFontFamily { Sans, Serif, Monospace }

public sealed record SplashFont(int Size, string Colour, bool Bold, SplashAlign Align, SplashFontFamily Family);
public sealed record SplashPicture(string Name, SplashFit Fit);              // Name: a picture-store name (plan 12b)
public sealed record SplashNewsItem(DateOnly Date, IReadOnlyDictionary<string, string> Text, bool Hidden);
public sealed record SplashPlace(Guid RegionId, string RegionName, IReadOnlyDictionary<string, string> Line, string? Picture);
public sealed record SplashStatusLines(bool Online, bool People, bool Regions);
public sealed record SplashLinks(bool SignUp, bool ForgotPassword, bool Terms, bool Privacy, bool FrontPage);

/// One record for every kind; a kind uses only its own fields and the rest stay null (the check refuses others).
public sealed record SplashContent(
    IReadOnlyDictionary<string, string>? Text = null,      // Text block (safe Markdown)
    IReadOnlyDictionary<string, string>? Caption = null,   // Picture block
    SplashPicture? Picture = null,                         // Picture block
    bool ShowName = true, bool ShowLogo = false,           // GridName block
    SplashStatusLines? Status = null,                      // Status block
    IReadOnlyDictionary<string, string>? Heading = null,   // News block
    IReadOnlyList<SplashNewsItem>? News = null,            // News block
    IReadOnlyList<SplashPlace>? Places = null,             // Places block
    SplashLinks? Links = null);                            // Links block

public sealed record SplashBlock(int Id, SplashBlockKind Kind, int X, int Y, int W, int H, int Layer,
    string Colour, int SeeThrough, SplashPicture? Background, bool Rounded, int Padding, SplashFont Font, SplashContent Content);

public sealed record SplashBackground(string Colour, SplashPicture? Picture, int FocusX, int FocusY, int Darken);

public sealed record SplashDesign(int Width, int Height, SplashBackground Background, bool Snap, int SnapSize,
    IReadOnlyList<SplashBlock> Blocks);

public static class SplashRules
{
    /// The problems with the design, as plain sentences naming the block id; empty when it may be saved.
    public static IReadOnlyList<string> Check(SplashDesign d, Func<string, bool> pictureExists);
}
```

Limits (spec §5; the numbers marked **(my call)** there are fixed here):
- `Width` 640–3840, `Height` 400–2160. `SnapSize` ∈ {8, 16, 32, 64}.
- At most 100 blocks; `Id` positive and unique; `Layer` 0–999.
- Each block inside the page: `X ≥ 0`, `Y ≥ 0`, `W ≥ 16`, `H ≥ 16`, `X + W ≤ Width`, `Y + H ≤ Height`.
- Colours match `^#[0-9a-fA-F]{6}$`. `SeeThrough` 0–100. `Darken` 0–80. `FocusX`, `FocusY` 0–100. `Padding` 0–64. `Font.Size` 8–200.
- Text dictionaries: only the key `en`. Lengths: block text 4000, caption 200, heading 120, news item 2000, place line 200, region name 64.
- At most 20 news items; at most 8 places.
- A block's `Content` may only set the fields of its own kind (for example a `Status` block with `News` set is refused); the kind's required field must be set (`Text` for Text, `Picture` for Picture, `Status` for Status, `News` for News (may be empty), `Places` for Places, `Links` for Links).
- Every picture name (block background, page background, Picture content, place picture) must match `^[0-9a-f]{32}\.(png|jpg|webp)$` and `pictureExists(name)` must be true.

- [ ] **Step 1: Write the failing tests** in `SplashRulesTests.cs`. Use a helper `Valid()` that returns a 1280 × 800 design with one block of each kind, every field inside its limits, and no pictures.

```csharp
[Fact] public void Valid_design_passes() => Assert.Empty(SplashRules.Check(Valid(), _ => false));

[Theory]
[InlineData(639, 800)] [InlineData(3841, 800)] [InlineData(1280, 399)] [InlineData(1280, 2161)]
public void Design_size_out_of_bounds_is_refused(int w, int h) =>
    Assert.NotEmpty(SplashRules.Check(Valid() with { Width = w, Height = h }, _ => false));

[Fact] public void Check_uses_current_design_size()
{
    // A block at x=1500 is outside a 1280-wide page but inside a 1920-wide one.
    var block = Valid().Blocks[0] with { X = 1500, W = 100 };
    var d = Valid() with { Blocks = [block] };
    Assert.NotEmpty(SplashRules.Check(d, _ => false));
    Assert.Empty(SplashRules.Check(d with { Width = 1920 }, _ => false));
}

[Fact] public void Block_outside_page_is_refused()      // X+W > Width, Y+H > Height, X < 0, W < 16: four asserts
[Fact] public void More_than_100_blocks_is_refused()
[Fact] public void Duplicate_block_ids_are_refused()
[Theory] [InlineData("red")] [InlineData("#fff")] [InlineData("#12345g")] [InlineData("#1234567")]
public void Bad_colour_is_refused(string colour)
[Fact] public void Field_of_another_kind_is_refused()   // a Status block with News = [] set
[Fact] public void Missing_required_field_is_refused()  // a Text block with Text = null
[Fact] public void Language_other_than_en_is_refused()  // Text = { ["de"] = "Hallo" }
[Fact] public void Overlong_text_is_refused()           // 4001 chars refused, 4000 accepted
[Fact] public void Too_many_news_items_or_places_is_refused() // 21 news, 9 places
[Fact] public void Picture_must_exist_and_be_well_named()
{
    var name = new string('a', 32) + ".png";
    var d = Valid() with { Background = Valid().Background with { Picture = new SplashPicture(name, SplashFit.Fill) } };
    Assert.NotEmpty(SplashRules.Check(d, _ => false));
    Assert.Empty(SplashRules.Check(d, n => n == name));
    var bad = d with { Background = d.Background with { Picture = new SplashPicture("../settings.json", SplashFit.Fill) } };
    Assert.NotEmpty(SplashRules.Check(bad, _ => true));
}
```

- [ ] **Step 2: Run them and see them fail** (`SplashRules` doesn't exist): `dotnet test Launcher/DreamGrid.Launcher.Tests --filter FullyQualifiedName~SplashRulesTests` → build error.
- [ ] **Step 3: Write the records in `SplashDesign.cs` and `SplashRules.Check` in `SplashRules.cs`** with the limits above as named `const`s on `SplashRules` (`MaxBlocks = 100`, `MaxNews = 20`, `MaxPlaces = 8`, …) so 12c's editor can show them.
- [ ] **Step 4: Run the tests again; all pass.**
- [ ] **Step 5: Commit** — `git commit -m "Splash: the design model and its check"` (with the Co-Authored-By line).

---

### Task 2: The store (draft, published, versions)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Splash/SplashStore.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (add `public string Splash => Path.Combine(Data, "splash");`)
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashStoreTests.cs`

**Interfaces:**
- Consumes: `SplashDesign`, `SplashRules.Check` (Task 1).
- Produces:
```csharp
public sealed record SplashDocument(SplashDesign Design, int Version, string SavedBy, DateTimeOffset Saved);

public abstract record SplashSaveResult
{
    public sealed record Saved(SplashDocument Draft) : SplashSaveResult;
    public sealed record Conflict(int CurrentVersion) : SplashSaveResult;
    public sealed record Invalid(IReadOnlyList<string> Problems) : SplashSaveResult;
}

/// Data/splash/draft.json and Data/splash/published.json (JsonStateFile each).
public sealed class SplashStore(LauncherPaths paths, TimeProvider time, ILogger<SplashStore>? log = null)
{
    public Func<string, bool> PictureExists { get; init; } = _ => false;   // 12b sets this
    public SplashDocument? Draft();
    /// Null when nothing was published, or when the file fails SplashRules.Check (one warning per process).
    public SplashDocument? Published();
    /// basedOnVersion: the draft version the editor started from (0 = no draft yet).
    public SplashSaveResult SaveDraft(SplashDesign design, int basedOnVersion, string savedBy);
    /// Copies the draft to published with the next published version; null when there is no draft.
    public SplashDocument? Publish(string publishedBy);
    /// Deletes the draft; the next Draft() is null (the editor then starts from Published() or the default).
    public void Discard();
}
```

Versions: the draft version goes up by one on each save, starting at 1. The published version is its own counter, starting at 1 and going up by one on each publish, kept across discards (it is in `published.json`). One `Lock` around `SaveDraft`/`Publish`/`Discard`, so the version check and the write happen together.

- [ ] **Step 1: Write the failing tests** with a temp data folder and a `FakeTimeProvider`:
  - `Save_then_load_round_trips`: save a design based on 0, `Draft()` returns it with version 1, `SavedBy` and the fake time.
  - `Stale_draft_save_is_a_conflict`: save based on 0 (v1), save based on 1 (v2), save based on 1 again → `Conflict(2)`, and `Draft()!.Version == 2` with the second design.
  - `Invalid_design_is_not_saved`: a design that fails the check → `Invalid` with problems, and `Draft()` unchanged.
  - `Publish_copies_draft_and_counts_versions`: publish twice → published versions 1 then 2; `Publish` with no draft → null.
  - `Discard_keeps_published`: discard → `Draft()` null, `Published()` unchanged.
  - `Invalid_published_file_is_ignored`: write `published.json` by hand with a block at `X = -5` → `Published()` is null.
  - `Files_are_utf8_without_bom`: the first three bytes of `draft.json` are not `EF BB BF`.
- [ ] **Step 2: Run, see them fail** (build error).
- [ ] **Step 3: Implement `SplashStore`** on two `JsonStateFile<…>` instances (`JsonStateFile` needs a non-null `empty()`, so wrap the document, for example `sealed record SplashFile(SplashDocument? Document)`). Create `paths.Splash` on first save.
- [ ] **Step 4: Run, all pass.**
- [ ] **Step 5: Commit** — `"Splash: the draft and published design are kept under Data/splash"`.

---

### Task 3: Whole-grid regions and the default design

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/GridRegions.cs`, `Launcher/DreamGrid.Launcher.Core/Splash/SplashDefaults.cs`
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs` (add `FakeGridRegions` next to `FakeOnlineUsers`), `Launcher/DreamGrid.Launcher.Tests/LauncherFactory.cs` (property `Regions`, registration next to `IOnlineUsers`), `Launcher/DreamGrid.Launcher/Program.cs` (register the real one next to `IOnlineUsers`, line ~275)
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashRulesTests.cs` (default design), plus Task 5's page tests use the fake.

**Interfaces:**
- Produces:
```csharp
namespace DreamGrid.Launcher.Core.Grid;
public sealed record GridRegion(Guid Id, string Name);
public sealed record GridRegionList(IReadOnlyList<GridRegion> Regions, bool Available)
{ public static readonly GridRegionList Unavailable = new([], false); }
public interface IGridRegions { Task<GridRegionList> ListAsync(CancellationToken ct); }
/// Robust's regions table: a region is listed while it is registered with Robust, on any machine of the grid.
public sealed class GridRegions(Func<LauncherSettings> settings, ILogger<GridRegions>? log = null) : IGridRegions

namespace DreamGrid.Launcher.Core.Splash;
public static class SplashDefaults
{
    /// The built-in page (spec §4.6): grid name, status line, links, over #1a2a3a, 1280 × 800.
    public static SplashDesign Design();
}
```
- `GridRegions` copies `OnlineUsers` exactly in shape: same connection (`MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, 3)`), query `SELECT uuid, regionName FROM regions ORDER BY regionName`, the same two `catch` clauses and the warn-once logging, GUIDs read as either `Guid` or text.
- `FakeGridRegions { public GridRegionList Result { get; set; } = new([], true); }`.

- [ ] **Step 1: Write the failing test** `Default_design_passes_the_check`: `Assert.Empty(SplashRules.Check(SplashDefaults.Design(), _ => false))`, and it has exactly a `GridName`, a `Status` (all three lines) and a `Links` block (sign up, forgot password, Terms, privacy).
- [ ] **Step 2: Run, see it fail.**
- [ ] **Step 3: Write `SplashDefaults.Design()`, `GridRegions`, `FakeGridRegions` and the registrations.** `GridRegions` has no unit test of its own (like `OnlineUsers`, it needs a real database); it is covered by the integration tests' database only if one exists there — check `Launcher/DreamGrid.Launcher.IntegrationTests` for an `OnlineUsers` test and add a matching one beside it if there is.
- [ ] **Step 4: Run, pass; build the whole solution** (`dotnet build Launcher`) so the registration compiles.
- [ ] **Step 5: Commit** — `"Splash: the built-in default page, and the whole grid's regions from Robust"`.

---

### Task 4: The stylesheet

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Splash/SplashCss.cs`, `Launcher/DreamGrid.Launcher/wwwroot/css/splash.css`
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashCssTests.cs`

**Interfaces:**
- Consumes: `SplashDesign` (Task 1).
- Produces:
```csharp
public static class SplashCss
{
    /// The page box's narrowest width in CSS px (spec §6): where the smallest text would be 12 px, never below half the
    /// design width, never above the design width.
    public static int MinWidth(SplashDesign d);
    /// The page box's widest width: 1.5 × design width.
    public static int MaxWidth(SplashDesign d);
    /// The stylesheet for one design; pictureUrl maps a picture name to its URL (12b; 12a passes n => "/welcome/images/" + n).
    public static string Build(SplashDesign d, Func<string, string> pictureUrl);
    /// The class a block's element carries: "b-" + id.
    public static string ClassOf(SplashBlock b);
}
```

What `Build` writes (the exact CSS is the implementer's, these are the rules the tests pin):
- `.splash-page` gets `width: clamp({MinWidth}px, 100vw, {MaxWidth}px)`, `aspect-ratio: {Width} / {Height}`, `container-type: inline-size`.
- Each block: `left`, `top`, `width`, `height` as percentages of the design (`X / Width * 100` and so on, invariant culture, at most 4 decimals); `z-index` = `Layer`; the background colour as `rgba(r, g, b, a)` with `a = 1 - SeeThrough / 100`; `border-radius` only when `Rounded`.
- Font size and padding are written twice: first in `px` at design size (`Size`), then in `cqw` (`Size / Width * 100`). A browser without container units keeps the `px` line (spec §6's fallback).
- Background fit: Fill → `background-size: cover`, Fit → `contain`, Stretch → `100% 100%`, Tile → `auto` with `repeat`, Centre → `auto` with `no-repeat` and `center`. Focus → `background-position: {FocusX}% {FocusY}%`. Darken → a `linear-gradient(rgba(0,0,0,{Darken/100}), rgba(0,0,0,{Darken/100}))` layered over the picture.
- The page background also goes on `body.splash`, so space outside the page box is filled.
- Font families map to fixed stacks: Sans → `system-ui, sans-serif`, Serif → `Georgia, serif`, Monospace → `ui-monospace, monospace`.
- Only checked values go in; the builder never copies a string from the design except colours (already checked against the colour pattern) and picture URLs from `pictureUrl`.
- `wwwroot/css/splash.css` holds what every splash page shares (`body.splash { margin: 0; overflow-x: auto; }`, `.splash-page { position: relative; margin: 0 auto; }`, `.splash-block { position: absolute; overflow: hidden; box-sizing: border-box; }`, list resets for news, places and links).

- [ ] **Step 1: Write the failing tests:**
```csharp
[Fact] public void Min_width_is_where_smallest_text_is_12px()
{   // smallest font 16 at 1280 wide → 12/16 × 1280 = 960
    Assert.Equal(960, SplashCss.MinWidth(WithFonts(1280, 16, 40)));
}
[Fact] public void Min_width_never_below_half() => Assert.Equal(640, SplashCss.MinWidth(WithFonts(1280, 48)));
[Fact] public void Min_width_never_above_design() => Assert.Equal(1280, SplashCss.MinWidth(WithFonts(1280, 10)));
[Fact] public void Max_width_is_one_and_a_half() => Assert.Equal(1920, SplashCss.MaxWidth(WithFonts(1280, 16)));
[Fact] public void Block_position_is_a_percentage()
{   // block 1 at x=320, y=200, w=640, h=400 on 1280 × 800
    var css = SplashCss.Build(OneBlock(320, 200, 640, 400), n => "/x/" + n);
    Assert.Contains(".b-1{left:25%;top:25%;width:50%;height:50%", css.Replace(" ", ""));
}
[Fact] public void Font_size_has_px_then_cqw()        // Size 32 at 1280 → "font-size:32px" before "font-size:2.5cqw"
[Fact] public void See_through_becomes_alpha()        // #ff0000, SeeThrough 25 → "rgba(255,0,0,0.75)"
[Theory] [InlineData(SplashFit.Fill, "cover")] [InlineData(SplashFit.Fit, "contain")] [InlineData(SplashFit.Stretch, "100% 100%")]
public void Background_fit_maps_to_size(SplashFit fit, string size)
[Fact] public void Numbers_use_invariant_culture()    // run Build under de-DE: no "25,5%" in the output
[Fact] public void Page_box_has_clamp_and_container() // "clamp(960px, 100vw, 1920px)", "aspect-ratio:1280/800", "container-type:inline-size"
```
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement `SplashCss`** with a `StringBuilder` and `CultureInfo.InvariantCulture`; write `splash.css`.
- [ ] **Step 4: Run, pass.**
- [ ] **Step 5: Commit** — `"Splash: each design gets its own stylesheet, shrinking to a readable size and then scrolling"`.

---

### Task 5: The public page `/welcome`

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Splash/SplashSnapshot.cs`, `Launcher/DreamGrid.Launcher/Pages/Welcome.cshtml.cs`, `Launcher/DreamGrid.Launcher/Pages/Shared/_SplashLayout.cshtml`, `Launcher/DreamGrid.Launcher/Pages/Shared/_SplashBlock.cshtml`
- Replace: `Launcher/DreamGrid.Launcher/Pages/Welcome.cshtml`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register `SplashStore` and `SplashSnapshot` as singletons; the stylesheet route), `Launcher/DreamGrid.Launcher/Security/AdminNetworkFilter.cs:33` (add `"/welcome"`)
- Test: `Launcher/DreamGrid.Launcher.Tests/WelcomePageTests.cs`

**Interfaces:**
- Consumes: `SplashStore.Published()`, `SplashDefaults.Design()`, `SplashCss.Build/ClassOf`, `IGridController.GetStatus()`, `IOnlineUsers`, `IGridRegions`, `LegalPages.Get`, `SignUpRules.IsOpen`, `AdminRule.HasStaffPages`, `LegalMarkdown.ToHtml`.
- Produces:
```csharp
namespace DreamGrid.Launcher.Splash;
/// Null counts mean "unavailable".
public sealed record SplashStatus(bool GridOnline, int? PeopleOnline, int? RegionsRunning);
public sealed record SplashView(SplashDesign Design, int Version, bool IsDefault, SplashStatus Status, string Css);
public sealed class SplashSnapshot(SplashStore store, IGridController grid, IOnlineUsers online, IGridRegions regions,
    Func<LauncherSettings> settings, TimeProvider time)
{
    /// Built at most once per 60 seconds (by the injected clock); concurrent callers share one build.
    public Task<SplashView> CurrentAsync(CancellationToken ct);
    /// Drops the cached view (called after a publish).
    public void Invalidate();
}
```
- `Version` is the published version, or 0 for the default page. `IsDefault` is true when nothing is published, the published file fails the check, or `!AdminRule.HasStaffPages(settings())`.
- `GridOnline`: `grid.GetStatus().Robust.State == ProcessState.Running` (check the enum's member name in `Processes/`).
- Route `GET /welcome/page-{version:int}.css`, anonymous: 200 with `text/css` and `Cache-Control: public, max-age=60` when `version` equals the current view's version, else 404.
- `/welcome` itself: anonymous (already in the `AllowAnonymousToPage` list), `Layout = "_SplashLayout"`, `Cache-Control: public, max-age=60` set in `OnGet`.
- `_SplashLayout`: `<!DOCTYPE html>`, `<html lang="en">`, title = grid name, `<link>` to `/css/splash.css` and to `/welcome/page-{Version}.css`, `<body class="splash">`. No Bootstrap, no `theme.js`, no scripts at all.
- `_SplashBlock` draws one block as `<div class="splash-block b-{id}">` by kind:
  - Text: `LegalMarkdown.ToHtml(Text["en"])` as raw HTML (it already disables HTML and unsafe links).
  - GridName: the grid name from settings when `ShowName` (the logo arrives in 12b).
  - Status: "Online" or "Offline"; "{n} people online" or "Status unavailable"; "{n} regions" or "Status unavailable" — each line only when its flag is on. "1 person online", "1 region" for one.
  - News: the heading, then visible items newest date first, each date as `d MMMM yyyy` (invariant English until 1.5) and its text through `LegalMarkdown.ToHtml`.
  - Places: each place's region name and line as text (pictures in 12b).
  - Links: Sign up `/signup` only when `SignUpRules.IsOpen(s)`; Forgot password `/guest/forgot-password` only when `AdminRule.HasStaffPages(s)`; Terms `/terms` and Privacy `/privacy` only when `LegalPages.Get(s, …)` has a text; Front page `/home` when `HasStaffPages`.
  - Picture: nothing in 12a (no pictures can pass the check yet).

- [ ] **Step 1: Write the failing tests** in `WelcomePageTests.cs` (anonymous client from `LauncherFactory`, settings seeded with a finished setup like `FrontPageTests.Factory`):
  - `Welcome_shows_default_before_any_publish`: 200; the grid name; "Online"/"Offline"; `<link href="/welcome/page-0.css">`; no `<script`.
  - `Welcome_shows_published_design`: save and publish a design with a Text block "Party at Sandbox" through `SplashStore` from the factory's services → the text is on the page and the stylesheet link is `page-1.css`.
  - `Welcome_has_no_admin_data`: `OnlineUsers.Result` lists "Secret Person" → the page says "1 person online" and doesn't contain "Secret Person".
  - `Welcome_shows_unavailable_when_database_down`: `OnlineUsers.Result = OnlineList.Unavailable`, `Regions.Result = GridRegionList.Unavailable` → 200 and "Status unavailable".
  - `Regions_count_the_whole_grid`: `Regions.Result` with 3 regions while `Grid` reports 1 local region → "3 regions".
  - `Invalid_published_file_shows_default`: write a bad `published.json` → the default page, and one warning in `Logs`.
  - `Block_text_is_not_html`: a news item `<script>alert(1)</script><img src=x onerror=alert(1)>` → no `<script` and no `onerror` element/attribute in the parsed page (AngleSharp), the text shown escaped.
  - `Welcome_is_cached_for_a_minute`: publish design A, GET, publish design B without `Invalidate`, GET → still A; `Time.Advance(TimeSpan.FromSeconds(61))`, GET → B. Both responses have `Cache-Control: public, max-age=60`.
  - `Stylesheet_route_serves_current_version_only`: `page-1.css` → 200 `text/css`, `page-2.css` → 404.
  - `Welcome_reachable_with_admin_network_list`: seed `Security.AdminNetworks = ["10.0.0.0/8"]` and a remote address outside it (as the existing `AdminNetworkFilter` tests fake it) → `/welcome` 200, `/welcome/page-0.css` 200.
  - `Region_server_shows_default`: seed a Region Server plan (as other `GuestSideOnly` tests do) → default page, no Links entry for sign up.
  - `Links_follow_what_is_open`: sign-up closed → no `/signup` link; Terms written → `/terms` link; no privacy text → no `/privacy` link.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement** `SplashSnapshot` (a `SemaphoreSlim(1)` around the rebuild; the counts fetched in parallel), `Welcome.cshtml(.cs)`, the two partials, the CSS route in `Program.cs`, and `"/welcome"` in `PublicPaths`. Draft-only content never reaches this page.
- [ ] **Step 4: Run, pass; also run `InlineScriptScanTests` and `AdminNetworkFilter`'s tests.**
- [ ] **Step 5: Commit** — `"Splash: /welcome shows the published design with live counts, cached for a minute"`.

---

### Task 6: The draft and publish API, with audit

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Endpoints/SplashApi.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (map it beside the other `*Api.Map…` calls)
- Test: `Launcher/DreamGrid.Launcher.Tests/SplashApiTests.cs`

**Interfaces:**
- Consumes: `SplashStore`, `SplashSnapshot.Invalidate`, `AuditLog`, `AuditActor.Of`, `IAntiforgery` (header `RequestVerificationToken`, as `StatusApi`'s `/api/grid/{action}` does).
- Produces (all `RequireAuthorization(AuthSchemes.AdminPolicy)`, all 404 when `!AdminRule.HasStaffPages(s)`; JSON in the `JsonSerializerDefaults.Web` shape of the records from Tasks 1–2):

| Route | Body | Answer |
|---|---|---|
| `GET /api/splash` | — | `{ draft: SplashDocument?, published: SplashDocument?, defaultDesign: SplashDesign, limits: {…SplashRules consts} }` |
| `PUT /api/splash/draft?basedOn={int}` | `SplashDesign` | 200 `SplashDocument`; 409 `{ currentVersion }`; 400 `{ problems: [...] }` |
| `POST /api/splash/publish` | — | 200 `SplashDocument`; 409 when there is no draft |
| `POST /api/splash/discard` | — | 204 |

- The design body is capped at 512 KB (`IHttpMaxRequestBodySizeFeature`) **(my call; 100 blocks of the largest text fit well inside)**.
- Audit (spec §4.8): `splash-published` (target `"version {n}"`) and `splash-draft-discarded`, with result `"ok"`. Draft saves aren't written by this endpoint (`AuditMiddleware` logs every staff non-GET already).
- `SavedBy` is the actor's avatar name from `AuditActor.Of(User).Name`.

- [ ] **Step 1: Write the failing tests** (staff clients through `/__test/signin?role=…`, as `FrontPageTests.StaffAsync`; fetch an antiforgery token as the existing `StatusApi` tests do):
  - `Owner_and_admin_can_save_and_publish` (Theory over `"owner"`, `"admin"`).
  - `Supervisor_and_anonymous_are_refused`: supervisor → 403 on every route; anonymous → not 200 (redirect or 401, as other AdminPolicy APIs answer); nothing written to `Data/splash`.
  - `Api_stale_save_returns_409`.
  - `Invalid_design_returns_problems`: a block with colour `"red"` → 400 whose `problems` names block 1.
  - `Publish_shows_on_welcome_at_once`: publish → the next `/welcome` GET shows it without advancing the clock (`Invalidate` was called).
  - `Publish_and_discard_are_audited`: `audit.log` has `splash-published` with `version 1`, and `splash-draft-discarded`.
  - `Missing_antiforgery_token_is_refused`: PUT without the header → 400, nothing saved.
  - `Oversize_body_is_refused`: a 600 KB body → 413 (or 400), nothing saved.
  - `Api_is_404_on_a_region_server`.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement `SplashApi.Map(WebApplication app)`** following `StatusApi`'s style.
- [ ] **Step 4: Run, pass. Then run the full suite once** (`dotnet test Launcher/DreamGrid.Launcher.Tests`, about 9 minutes): 0 failed.
- [ ] **Step 5: Commit** — `"Splash: staff save, publish and discard the design through /api/splash"`.

---

### Task 7: By hand in Firestorm, and the work list

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.7: note 12a done and what the Firestorm check showed)

- [ ] **Step 1:** Install the branch's package on the Debian 12 test VM (see the memory note "Debian 12 test VM" for how). Publish a design through `/api/splash` (a short `curl` with a staff cookie, or wait for 12c's editor) with a Text block, a Status block and a Links block.
- [ ] **Step 2:** In Firestorm 7.x on Windows, pick the grid and check, writing the answers in the work list under 2.7:
  - the page shows behind the login boxes (so `frame-ancestors 'none'` and `X-Frame-Options: DENY` don't block it);
  - text shrinks with the window and stops at the readable size, then the page scrolls (so container units work in its browser);
  - what clicking the Terms link does (opens outside or inside);
  - the counts change within about a minute of someone logging in.
- [ ] **Step 3: Commit** — `"Worklist: the splash page shows in Firestorm"`.
