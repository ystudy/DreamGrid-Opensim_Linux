# Plan 12c — Splash page: the block editor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The owner and admins build the viewer splash page at `/splash`: they pick blocks from a shelf, place them anywhere (snap optional), resize and arrange them, set the page and block settings, choose pictures, check the page at viewer sizes, and publish.

**Architecture:** The server stays the one place a design is drawn. The editor autosaves the design as the draft through the existing `/api/splash` API. After each save it reloads a staff-only stylesheet of the draft (`SplashCss.Build`, the same code the public page uses) and the draft's block HTML (the same `_SplashBlock` partial). While a block is being dragged or resized, JavaScript sets its position on the element's style object; the next stylesheet replaces that. The pure editing rules (snap, bounds, guides, layers, undo, minimum width) live in `splash-editor-core.js`, which has no DOM and is tested from xUnit through Jint. `splash-editor.js` holds the DOM, pointer and keyboard work and the API calls.

**Tech Stack:** .NET 10, Razor Pages, minimal APIs, vanilla JS (no library, as `map.js`), xUnit v3 with `LauncherFactory` and AngleSharp. **Jint** runs the core script in tests (BSD-2-Clause, test project only).

**Spec:** `docs/superpowers/specs/2026-11-01-viewer-splash-design.md` (§4.1 editor, §4.2 blocks, §4.3 page settings, §4.4 picture list, §6 shrink). Builds on plans 12a (PR 24) and 12b (PR 25), both merged into V7.

## Global Constraints

- `/splash` is under a new `/Splash` page folder. That folder is **AdminPolicy** (owner and admins, not supervisors) by the `Program.cs` folder rule. It has `GuestSideOnly`, so it is 404 on a Region Server or Connected Server, matching `/api/splash`.
- Menu item **"Splash page"** at `/splash`, shown to admins (owner included) where `hasStaffPages`, placed after "Logs".
- Security headers unchanged: no inline script, no `style=`, no `<style>`. The `InlineScriptScanTests` scan must pass. Live positions are set only through `element.style` in JS. No page frames another, since `frame-ancestors 'none'` stays.
- The design size defaults to **1280 × 800**, changeable within `SplashRules.MinWidth..MaxWidth` × `MinHeight..MaxHeight`. Positions are whole design pixels.
- Snap: **off by default**. Sizes come from `SplashRules.SnapSizes` (8, 16, 32, 64). Guide lines appear when an edge or centre lies within **4 design px** of another block's edge or centre, or of the page centre, and the block then lines up exactly.
- Keyboard: arrow keys move the selected block **1 px**, or **10 px** with Shift. Delete removes it. Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes. Keys are ignored while focus is in a form field.
- Blocks stay inside the page and are at least `SplashRules.MinBlockSize` (16) on each side. At most `SplashRules.MaxBlocks` (100): the shelf greys out at the limit.
- Preview sizes: **1024 × 640**, **1280 × 800** and **1920 × 1080**. The page box width is `clamp(SplashCss.MinWidth, viewer width, SplashCss.MaxWidth)`, and the editor shows "Shrinks to {min} px wide, then scrolls."
- Autosave runs **1 s** after the last change, plus a **Save** button. A 409 from the save shows *"Someone else changed this page. Reload to see their version."* and stops autosave until reload. A 400 shows the server's problems as a list.
- Publish answers *"Published. Viewers see it within a minute."* Discard draft asks with `confirm.js` first, then reloads.
- Undo and redo hold up to **100** steps and last for one editing visit only.
- The `/api/splash` routes keep their shape. New staff routes go under the same group (AdminPolicy, guest side, `RequestVerificationToken` on changes).
- Editor wording is plain and short, in the style of the Legal page.

## Review Focus

1. **A drag that ends outside the page or the window.** The block must end inside the page, at least 16 px each side, never NaN. Tests: `Move_keeps_block_inside_the_page` and `Resize_never_goes_below_the_minimum` in Task 3.
2. **Undo after an autosave.** Undo goes back one edit, and the next autosave saves the undone design; it never resurrects the redo branch after a new edit. Test: `New_edit_clears_redo` in Task 3.
3. **Two tabs open on the same draft.** The second tab's save gets 409 and stops autosaving instead of looping. Its message is fixed in the Global Constraints and checked by hand in Task 4. The server side is already tested (`Api_stale_save_returns_409`).
4. **The editor's minimum width disagreeing with the public page.** The preview's shrink point must equal `SplashCss.MinWidth` for the same design. Test: `Core_min_width_matches_server` in Task 3.
5. **Draft pictures and draft CSS reaching anonymous callers.** `/api/splash/draft.css`, `/splash/preview` and the block HTML handler are staff-only. Test: `Draft_routes_are_staff_only` in Task 1.

---

## File map

Core (`Launcher/DreamGrid.Launcher.Core/`):
- Modify `Splash/SplashCss.cs`: `Build` takes the page selector.

Web (`Launcher/DreamGrid.Launcher/`):
- Modify `Splash/SplashSnapshot.cs`: `WorkingAsync`, the draft view, never cached.
- Modify `Endpoints/SplashApi.cs`: `GET /api/splash/draft.css`.
- Modify `Pages/Shared/_SplashLayout.cshtml`: the stylesheet URL comes from ViewData.
- Create `Pages/Shared/_SplashBlocks.cshtml`: the blocks of one view (used by Welcome, the preview and the editor).
- Create `Pages/Splash/Index.cshtml(.cs)` (`/splash`, plus a `Blocks` handler) and `Pages/Splash/Preview.cshtml(.cs)` (`/splash/preview`).
- Modify `Pages/Welcome.cshtml` to use `_SplashBlocks`, `Pages/Shared/_Layout.cshtml` for the menu item, and `Program.cs` for `GuestSideOnly` on `/Splash`.
- Create `wwwroot/js/splash-editor-core.js`, `wwwroot/js/splash-editor.js` and `wwwroot/css/splash-editor.css`.

Tests (`Launcher/DreamGrid.Launcher.Tests/`): `SplashDraftViewTests.cs`, `SplashEditorPageTests.cs`, `SplashEditorCoreTests.cs` (Jint), plus additions to `SplashCssTests.cs`, `AccessTests.cs` and `LayoutTests.cs`. The csproj gets `Jint`, the newest stable 4.x on nuget.org at build time.

Plan test command: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~Splash|FullyQualifiedName~WelcomePage|FullyQualifiedName~Access|FullyQualifiedName~Layout|FullyQualifiedName~InlineScript"`. Run the full suite (about 9 minutes) once at the end.

---

### Task 1: The draft, drawn by the server

**Files:**
- Modify: `Core/Splash/SplashCss.cs`, `Splash/SplashSnapshot.cs`, `Endpoints/SplashApi.cs`, `Pages/Shared/_SplashLayout.cshtml`, `Pages/Welcome.cshtml(.cs)`, `Program.cs`
- Create: `Pages/Shared/_SplashBlocks.cshtml`, `Pages/Splash/Preview.cshtml(.cs)`
- Test: `SplashCssTests.cs`, `SplashDraftViewTests.cs`, `AccessTests.cs`

**Interfaces:**
- Produces:
```csharp
// SplashCss
public const string PublicPage = "body.splash";
public static string Build(SplashDesign d, Func<string, string> pictureUrl, string pageSelector = PublicPage);
// SplashSnapshot
/// The draft, or the published design, or the built-in one: what the editor opens with. Live status; never cached.
/// Pictures point at the staff route; the page background is drawn on body.splash and .splash-canvas.
public Task<SplashView> WorkingAsync(CancellationToken ct);
public static string StaffPictureUrl(string name) => "/api/splash/pictures/" + name;
public const string WorkingPageSelector = "body.splash, .splash-canvas";
```
- Produces: `SplashView.Version` for the working view is `store.DraftVersion()`. `_SplashLayout` reads `ViewData["SplashCss"]` (a URL); Welcome sets `/welcome/page-{v}.css`. `_SplashBlocks` takes a `SplashView` plus `LauncherSettings` as `SplashBlocksModel(SplashView View, LauncherSettings Settings)`, and wraps each block in nothing extra.

- [ ] **Step 1: Write the failing tests**
  - `SplashCssTests.Page_selector_is_used_for_the_background`: `Build(d, n => "/x/" + n, "body.splash, .splash-canvas")` starts with `"body.splash, .splash-canvas{"`. Plus `Default_selector_is_body_splash` (unchanged output for existing callers).
  - `SplashDraftViewTests.Working_view_is_the_draft_when_there_is_one`: save a draft with text "Draft" (via the API, as in `SplashApiTests`), then GET `/splash/preview` as owner. The body contains "Draft" and a link `href="/api/splash/draft.css?v={draftVersion}"`.
  - `Working_view_falls_back_to_published_then_default`: with no draft, the preview shows the published text. With neither, it shows the grid name "Avalon".
  - `Draft_css_uses_staff_picture_urls`: a draft with a background picture (upload with `TestPictures.Png` as in `SplashPictureApiTests`) → `GET /api/splash/draft.css` has `url("/api/splash/pictures/{name}")`, `Content-Type` text/css, and `Cache-Control: no-store`.
  - `Draft_routes_are_staff_only`: anonymous and supervisor get no 200 from `/api/splash/draft.css` and `/splash/preview`. Owner and admin get 200.
  - `Draft_routes_are_404_on_a_region_server`: settings for a Region Server (as `SplashApiTests` does for `/api/splash`) → 404 for both.
  - `Welcome_unchanged`: the existing `WelcomePageTests` still pass (run them).
  - `AccessTests`: add `/splash/preview` to whatever list the AdminPolicy pages are checked against (follow the file's pattern for `/advanced`).

- [ ] **Step 2: Run them.** Expected: FAIL (no `pageSelector` parameter, `/splash/preview` 404).

- [ ] **Step 3: Implement.** `Preview.cshtml` uses `Layout = "_SplashLayout"`, sets `ViewData["SplashCss"]`, and renders `_SplashBlocks` inside `<div class="splash-page">`. Its model sends `Cache-Control: no-store`. Register `GuestSideOnly` for the `/Splash` folder in `Program.cs`. AdminPolicy comes from the folder rule already, so don't add `/Splash/` to `ownFolders`. `draft.css` goes in the `/api/splash` group: `Results.Text(SplashCss.Build(view.Design, StaffPictureUrl, WorkingPageSelector), "text/css")`.

- [ ] **Step 4: Run the plan test command.** Expected: PASS.

- [ ] **Step 5: Commit** — `Splash: the draft is drawn for staff, with a preview page`.

---

### Task 2: The editor page

**Files:**
- Create: `Pages/Splash/Index.cshtml(.cs)`, `wwwroot/css/splash-editor.css`
- Modify: `Pages/Shared/_Layout.cshtml`
- Test: `SplashEditorPageTests.cs`, `LayoutTests.cs`, `AccessTests.cs`

**Interfaces:**
- Consumes: `SplashSnapshot.WorkingAsync`, `_SplashBlocks`.
- Produces (the markup contract `splash-editor.js` relies on; keep these ids and attributes):
  - `<main id="splash-editor" data-api="/api/splash" data-blocks="/splash?handler=Blocks" data-version="{draftVersion}">` holding a form with `@Html.AntiForgeryToken()`.
  - `#splash-shelf` with one `button.shelf-item[data-kind]` per `SplashBlockKind` (in enum order), labelled: Text, Picture, Grid name and logo, Live status, News, Places to go, Links.
  - `#splash-toolbar`: buttons `#undo`, `#redo`, `#forward`, `#back`, `#duplicate`, `#delete`, `#snap` (a checkbox switch), `#snap-size` (a select of `SplashRules.SnapSizes`), `#save`, `#publish`, `#discard` (with `data-confirm` for `confirm.js`), the preview buttons `button.preview-size[data-w][data-h]` for the three sizes plus `#preview-off`, a link `#preview-tab` to `/splash/preview` (`target="_blank" rel="noopener"`), and status text `#save-state` (`role="status"`).
  - `#splash-stage` > `div.splash-canvas` > `div.splash-page#canvas-page` holding the working view's blocks (server-rendered through `_SplashBlocks`), and `<link id="draft-css" rel="stylesheet" href="/api/splash/draft.css?v={version}">` in the `Head` section along with `/css/splash.css` and `/css/splash-editor.css`.
  - `#splash-settings`, empty and filled by JS. `#splash-pictures`, the picture list panel (Task 6).
  - The scripts, by `src` only: `/js/confirm.js`, `/js/splash-editor-core.js`, `/js/splash-editor.js`.
  - `OnGetBlocks()` returns `Partial("_SplashBlocks", …)` for the working view, with `Cache-Control: no-store`.
  - `_SplashBlocks` adds `data-block-id="{Id}"` to each block's outer div. This is harmless on `/welcome` and is how the editor finds a block.

- [ ] **Step 1: Write the failing tests** (`SplashEditorPageTests`):
  - `Owner_and_admin_open_the_editor` (theory): 200, `#splash-shelf .shelf-item` count is 7 with the labels above in order, and `#canvas-page [data-block-id]` count equals the working design's blocks (default design: count them through `SplashDefaults.Design().Blocks.Count`).
  - `Supervisor_resident_and_anonymous_are_refused`: no 200 (follow how `AccessTests` asserts refusals: a redirect to sign-in or 403).
  - `Editor_is_404_on_a_region_server`.
  - `Blocks_handler_returns_the_draft_blocks`: after saving a draft with text "Hello there", `GET /splash?handler=Blocks` contains "Hello there" and `data-block-id="1"`.
  - `Editor_scripts_are_files`: every `<script>` has `src` and the three above are present. The `InlineScriptScanTests` scan picks up the new files by itself.
  - `LayoutTests.Admin_menu_has_splash_page` (owner and admin see `a[href="/splash"]` with text "Splash page"), `Supervisor_menu_has_no_splash_page`, and `Region_server_menu_has_no_splash_page`.

- [ ] **Step 2: Run them.** Expected: FAIL (404, no menu item).

- [ ] **Step 3: Implement the page, menu item and `splash-editor.css`.** Use three columns on wide screens (shelf about 12rem, stage, settings about 20rem) and stack them under `lg`. The stage scrolls. `.splash-canvas` is `position: relative` with `overflow: hidden`. The selected block gets an outline with eight `.handle[data-h=n|s|e|w|ne|nw|se|sw]` spans, which JS adds. `.guide-x` and `.guide-y` are 1 px lines. A preview frame `.viewer-frame` scrolls, with `transform-origin: 0 0`.

- [ ] **Step 4: Run the plan test command.** Expected: PASS.

- [ ] **Step 5: Commit** — `Splash: the editor page, its shelf and the menu item`.

---

### Task 3: The editing rules (`splash-editor-core.js`), tested through Jint

**Files:**
- Create: `wwwroot/js/splash-editor-core.js`
- Modify: `DreamGrid.Launcher.Tests.csproj` (Jint)
- Test: `SplashEditorCoreTests.cs`

**Interfaces:**
- Produces: one global `SplashEditorCore`, with no DOM and no `fetch`. A design and its blocks are plain objects in the API's JSON shape (camelCase, enums as names, as `SplashDesign` serialises with `JsonSerializerDefaults.Web`).
```js
SplashEditorCore = {
  snap(value, size, on),                       // nearest multiple of size when on, else Math.round(value)
  move(block, dx, dy, design, opt),            // new block (copy); opt = { snap, snapSize, others: [blocks], tolerance: 4 }
                                               // returns { block, guides: { x: [px], y: [px] } }
  resize(block, handle, dx, dy, design, opt),  // handle "n"|"s"|"e"|"w"|"ne"|"nw"|"se"|"sw"; same return as move
  nudge(block, key, shift, design),            // "ArrowLeft"/"ArrowRight"/"ArrowUp"/"ArrowDown"; 1 or 10 px
  addBlock(design, kind, x, y, defaults),      // new design with a block of kind centred on (x, y); id = max id + 1;
                                               // layer = max layer + 1; defaults = the content/font for each kind (below)
  duplicate(design, id),                       // copy at +16,+16 (kept inside), new id, on top
  remove(design, id),
  forward(design, id), back(design, id),       // swap layers with the next block above/below; layers stay within 1..SplashRules.MaxLayer
  minWidth(design),                            // equals SplashCss.MinWidth
  maxWidth(design),                            // design.width * 3 / 2, rounded down
  previewWidth(design, viewerW),               // clamp(minWidth, viewerW, maxWidth)
  History(limit)                               // { push(design), undo() -> design|null, redo() -> design|null, canUndo, canRedo }
};
```
- `defaults` (also in this file, as `SplashEditorCore.defaults[kind]`): a 400 × 120 block (Picture 320 × 240, Places 640 × 240, News 400 × 300), colour `#000000`, see-through 50, padding 12, font 24 `#ffffff` Sans Left. Content per kind:
  - Text: `{ text: { en: "Your text here" } }`.
  - Picture: `{ picture: null }`. The rules refuse a picture block without a picture, so the picture is chosen in the settings before the first save. Until then, autosave skips the save and `#save-state` says "Choose a picture for the new picture block."
  - GridName: `{ showName: true, showLogo: false }`.
  - Status: all three lines.
  - News: heading "News" and no items.
  - Places: `[]`.
  - Links: all five on.
  If 12a's rules refuse any of these defaults, follow the rules and ledger a ruling.

- [ ] **Step 1: Write the failing tests.** The test class loads the file into a `Jint.Engine` once per test and calls functions through `engine.Evaluate(...)` with JSON in and JSON out. Test names:
  - `Snap_rounds_to_the_grid_only_when_on`: `snap(37, 16, true)` is 32, `snap(41, 16, true)` is 48, `snap(37.6, 16, false)` is 38.
  - `Move_keeps_block_inside_the_page`: a 100 × 100 block moved by (−5000, +5000) on 1280 × 800 lands at (0, 700).
  - `Move_with_snap_lands_on_the_grid`.
  - `Move_lines_up_with_a_near_edge`: another block's left edge at 300. Moving to x 297 lands on 300 with `guides.x` containing 300. Moving to x 290 doesn't (beyond 4).
  - `Move_lines_up_with_the_page_centre`: a 200-wide block at x 538 snaps to 540, the guide at 640.
  - `Resize_never_goes_below_the_minimum`: "nw" by (+5000, +5000) leaves W and H at 16 with the right and bottom edges unchanged.
  - `Resize_never_leaves_the_page`: "se" by (+5000, +5000) ends at the page edge.
  - `Nudge_moves_one_or_ten`, `Nudge_stops_at_the_edge`.
  - `Add_block_gets_next_id_and_top_layer`, `Add_block_is_kept_inside_when_dropped_at_the_edge`.
  - `Duplicate_offsets_and_goes_on_top`.
  - `Forward_and_back_swap_with_the_neighbour`, `Back_at_the_bottom_changes_nothing`.
  - `History_undo_redo` and `New_edit_clears_redo`: push A, B, C; undo → B; push D; redo → null.
  - `History_drops_the_oldest_beyond_the_limit`: limit 100 and 105 pushes → 99 undos available, not 104.
  - `Core_min_width_matches_server` (theory over 4 designs: no text blocks, a 12 px font, a 24 px font, a 200 px font): JS `minWidth` equals `SplashCss.MinWidth` for the same design serialised with the web JSON options. Also `previewWidth(d, 1024)` equals `Math.Clamp(1024, SplashCss.MinWidth(d), SplashCss.MaxWidth(d))`.
  - `Every_default_block_passes_the_rules`: for every kind except Picture, `addBlock` on the default design → deserialise → `SplashRules.Check(d, _ => true)` is empty.

- [ ] **Step 2: Run them.** Expected: FAIL (the file is missing).

- [ ] **Step 3: Implement `splash-editor-core.js`.** Plain functions, no modules (`var SplashEditorCore = (function () { … })();`). Never mutate the arguments. Guides: compare the moving block's left, centre and right (and top, middle, bottom) with every other block's same three and with the page centre. Take the nearest match within the tolerance on each axis, and apply snap only when no guide matched.

- [ ] **Step 4: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~SplashEditorCore"`. Expected: PASS.

- [ ] **Step 5: Commit** — `Splash editor: the editing rules, tested through Jint`.

---

### Task 4: The canvas (`splash-editor.js`, part 1)

**Files:**
- Create: `wwwroot/js/splash-editor.js`
- Test: by hand (Step 3); markup contract already pinned by Task 2.

**Interfaces:**
- Consumes: Task 2's markup, Task 3's `SplashEditorCore`, and `/api/splash` (`GET` for design, version and limits; `PUT /draft?basedOn=`; `POST /publish`; `POST /discard`).
- Produces for Tasks 5 and 6, inside the same file's closure:
  - `state` = `{ design, version, selected, limits, regions }`.
  - `change(newDesign)` pushes history, redraws positions and schedules the autosave.
  - `select(id|null)` fires `renderSettings()`, which Task 5 fills.

- [ ] **Step 1: Write it.**
  - On load, `GET /api/splash` and set `state.design = draft?.design ?? published?.design ?? defaultDesign`, with `version` = `draftVersion`. A 401 goes to the sign-in page, as `map.js` does.
  - Size the canvas to the stage width, keeping the design's shape. Scale = canvas px ÷ design px. Set `#canvas-page`'s `style.width` in px.
  - Pointer events with `setPointerCapture` follow `map.js`: drag a shelf item onto the canvas (or click it, which adds at the page centre), drag a block to move it, drag a handle to resize. During a drag, set the block's `style.left/top/width/height` in % and draw the guides. On pointerup, call `change()`.
  - Keyboard: as in the Global Constraints. The toolbar buttons are enabled only when something is selected (undo and redo follow `History`).
  - Autosave and Save: `PUT` the design. On 200, set `version` from the response, set `#draft-css` href to `/api/splash/draft.css?v={version}`, and once it loads, clear the blocks' inline positions. Then `GET data-blocks` and replace `#canvas-page`'s blocks, since the content may have changed. Show `#save-state` as "Saving…", "Saved", the 409 message or the 400 problems.
  - Publish shows the message from the Global Constraints, or the server's `problem` text. Discard uses `confirm.js`'s `data-confirm`, then `POST /discard` and a reload.
  - Preview: a size button wraps the canvas in `.viewer-frame` of that size in px, scaled down with `style.transform` to fit the stage. The page width inside is `previewWidth(design, w)`, and it scrolls when the frame is narrower. The status text "Shrinks to {minWidth} px wide, then scrolls." sits under it. Editing is off while previewing.

- [ ] **Step 2: Run the plan test command.** Expected: PASS (scan and page tests unchanged).

- [ ] **Step 3: Check it by hand in the browser pane.** Start a local `LauncherFactory` host on Kestrel if that's simple (.NET 10 `UseKestrel()`), signed in through `/__test/signin?role=owner`. If it isn't simple, use a scratch harness in the scratchpad (never committed) that serves the page HTML, `/api/splash` JSON and the scripts. Check:
  - drop each kind;
  - move, resize from each handle, and the guides at the page centre;
  - snap on and off;
  - nudge, Shift-nudge and Delete;
  - undo and redo;
  - autosave with the "Saved" text;
  - a second tab saving → the 409 message;
  - the three preview sizes;
  - publish.
  Ledger what was checked and how.

- [ ] **Step 4: Commit** — `Splash editor: place, move, resize, arrange, snap, undo, autosave, preview`.

---

### Task 5: The settings panel (`splash-editor.js`, part 2)

**Files:**
- Modify: `wwwroot/js/splash-editor.js`, `wwwroot/css/splash-editor.css`
- Test: `SplashEditorCoreTests.cs` (one helper below) plus by hand.

**Interfaces:**
- Consumes: `state`, `change()`, `select()`, and the `limits` from `GET /api/splash`.
- Produces: `SplashEditorCore.setPath(obj, "content.font.size", value)`, which returns a copy with that one field changed (dotted path; numbers index arrays). Add it to the core and test it: `Set_path_copies_and_changes_one_field` (the original is unchanged, and an array element such as `content.news.0.hidden` works).

- [ ] **Step 1: Write the `setPath` test, watch it fail, implement it, and watch it pass** (same filter as Task 3).

- [ ] **Step 2: Build the panel.** Inputs are built with `document.createElement`, never by writing HTML from owner text. Each input calls `change(setPath(...))`. Number inputs carry `min` and `max` from `limits`, and text inputs carry `maxlength` from `limits`.
  - **Nothing selected, the page settings:**
    - background colour;
    - background picture with fit (Fill, Fit, Stretch, Tile, Centre);
    - focus point: a click on a small copy of the picture sets `focusX`/`focusY` in %;
    - darkening slider 0–`maxDarken`;
    - design width and height;
    - snap and snap size, mirrored in the toolbar.
  - **Any block:**
    - X, Y, W and H, as numbers;
    - colour and see-through 0–100;
    - block picture with its fit, or none;
    - rounded corners;
    - padding 0–`maxPadding`;
    - font size, colour, bold, alignment and family (shown for every kind except Picture without a caption).
  - **By kind:**
    - Text: a textarea, with a one-line hint "Markdown: **bold**, *italic*, [link](https://…)".
    - Picture: the picture, its fit and a caption.
    - Grid name and logo: show the name and/or the logo, with a link to the logo controls (Task 6).
    - Live status: three checkboxes.
    - News: a heading, then a list of items, each with a date, a text, a Hide checkbox, up and down, and remove. "Add news" is disabled at `maxNews`.
    - Places to go: a region picker from `state.regions` (name only; refused once `maxPlaces` are chosen), each place with a line of text, a picture (choose an uploaded one, or "Use the map tile", which is Task 6), up and down, and remove.
    - Links: five checkboxes. A note says sign-up and forgot password show only while those pages are open.

- [ ] **Step 3: Run the plan test command, then check by hand** as in Task 4: change every field of every kind and see the canvas follow after the save. Push each text to its maxlength, and add news up to the limit and places up to 8.

- [ ] **Step 4: Commit** — `Splash editor: page and block settings`.

---

### Task 6: Pictures and the logo in the editor (`splash-editor.js`, part 3)

**Files:**
- Modify: `wwwroot/js/splash-editor.js`, `Pages/Splash/Index.cshtml` (the `#splash-pictures` panel markup), `wwwroot/css/splash-editor.css`
- Test: `SplashEditorPageTests.cs` and by hand.

**Interfaces:**
- Consumes these 12b routes:
  - `GET /api/splash/pictures` returns `{ pictures: [{ name, label, width, height, bytes, added, addedBy, inUse }] }`.
  - `POST /api/splash/pictures?label=` takes the raw file as the body. It answers 200 with the picture, 400 `{ problem }` or 413.
  - `DELETE /api/splash/pictures/{name}` answers 409 when the picture is in use.
  - `POST /api/splash/pictures/from-region/{id}`.
  - `POST /api/theme/logo?label=` and `DELETE /api/theme/logo`.
  - `GET /api/splash/pictures/{name}` serves a thumbnail as `<img>`.
- Check each route's exact answer in `SplashApi.cs` before using it. The list above is from plan 12b.

- [ ] **Step 1: Write the failing test:** `Editor_has_the_picture_panel`, asserting `#splash-pictures` holds `input#picture-file[type=file][accept="image/png,image/jpeg,image/webp"]`, `#picture-list` and `#logo-file`, plus `#logo-remove` shown only when a logo is set (`ThemeStore.SetLogo` in the test). Run it. Expected: FAIL.

- [ ] **Step 2: Add the markup and run it.** Expected: PASS.

- [ ] **Step 3: Write the JS.**
  - Upload sends the raw file with `XMLHttpRequest`, shows progress the way `upload.js` does, and shows the server's message on 400 and "That file is bigger than 10 MB." on 413.
  - The list shows a thumbnail, the label, the size, and which blocks use it (worked out from `state.design`, plus "on the published page" from `inUse`). Delete is disabled while the picture is in use, and a 409 shows "This picture is still used on the page."
  - Every picture chooser in Task 5's panel opens a small picker from this list.
  - "Use the map tile" and "Refresh picture" on a place call `from-region` and set the place's picture.
  - Logo upload and remove reload the blocks after the call, so a grid-name block shows the change.

- [ ] **Step 4: Run the plan test command, then check by hand:**
  - upload a PNG, a JPEG and a WebP;
  - a renamed text file → its message;
  - set the page background and the focus point;
  - a block picture;
  - a picture block with a caption;
  - a place's map tile;
  - delete an unused picture, then try one in use;
  - the logo on and off.

- [ ] **Step 5: Commit** — `Splash editor: pictures, map tiles and the logo`.

---

### Task 7: Finish

- [ ] **Step 1: Run the full suite** to a file in the workspace and read its tail. Expected: 0 failed.
- [ ] **Step 2: Update the worklist.** Item 2.7: the editor is built, and what is left is the owner's checks. Commit `Worklist: the splash page editor is built`.
- [ ] **Step 3: Put it on the VM.** Run `gh workflow run release.yml --ref feature/splash-editor`, download the package, and upgrade the VM as the memory note says. Check that `/splash` answers (a redirect to sign-in when not signed in) and `/welcome` answers 200.
- [ ] **Step 4: The owner's checks**, listed for them in the final message:
  - sign in;
  - build a page, with a picture background, a status block, news and places;
  - preview at the three sizes;
  - publish;
  - in Firestorm: the page and its pictures and logo show, it shrinks and then scrolls in a small window, and the numbers update within about a minute;
  - note what Firestorm does with the links.
