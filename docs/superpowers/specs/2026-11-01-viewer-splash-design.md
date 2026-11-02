# Viewer splash page: design

This is work list item 2.7 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`). It replaces Wifi's `splash` and `welcome` pages. It builds on audiences and security (`2026-10-30-audiences-security-design.md`: staff roles, policies, the audit log, the security headers) and on the Legal pages (`/security/legal`, `Pages/Legal.cshtml`), whose saving and public serving it copies.

Marks: **(owner)** means the grid owner decided it in the design conversation. **(my call)** means the designer settled a detail the owner didn't speak to; the owner can overturn any of them.

## 1. Why

When a resident picks the grid in Firestorm, the viewer shows the page named by `welcome=` in Robust's `[GridInfoService]` behind its login boxes. `GridConfigWriter` already points `welcome` at the launcher's `/welcome` (`GridConfigWriter.cs:58`), but `/welcome` is a 7-line stub ("Welcome to {GridName}") drawn with the **staff** layout. The grid has no way to greet people, show that it's up, or tell them what's on.

The owner doesn't want a page we design. "It should be the owner that creates the page that they want. We just offer the options of what can be." **(owner)** The website gets a splash page section where the owner (or an admin) builds the page from blocks: pick blocks, place them anywhere, resize them, set the background picture and how it fits.

**Success:** the owner opens the splash page editor, uploads a picture of their best region, drops a grid name, a status block and a news block onto it, drags and resizes them until it looks right, checks it at a small and a large viewer window size, and publishes. The next time anyone picks the grid in Firestorm they see that page, with live numbers, readable at their window size.

## 2. Goals, non-goals, anti-goals

**Goals**
- A **block editor** for the splash page on the staff side **(owner)**:
  - **Pick** blocks from a shelf.
  - **Place anywhere**: a block goes exactly where it's dropped, at any size, and may overlap the background or other blocks **(owner)**.
  - **Snap to grid** as an option the owner turns on and off **(owner: "place anywhere with snap grid options too")**.
  - **Resize** blocks by dragging their edges and corners **(owner)**.
  - **Arrange**: move blocks, bring a block forward or send it back, delete it.
- An **adjustable background picture** **(owner: "adjustable size background images")**: fill (crop edges), fit (show all), stretch, tile, or centred at actual size; a focus point that stays in view when cropping; a darkening slider so text reads over it **(my call: the focus point and darkening, shown in the mockup and not objected to)**.
- Each block has its own background colour, see-through level and optional picture **(my call, shown to the owner)**.
- The blocks on the shelf (§4.2): text, picture, grid name and logo, live status, news, places to go, links.
- **Shrink to fit, then scroll** **(owner)**: in a viewer window smaller than the page as designed, the whole page shrinks "to an extent"; once it would stop being readable, it stops shrinking and the resident scrolls.
- **Who can change it** **(owner)**: the owner and admins. Supervisors can't. An owner who wants someone to look after the page makes them an admin. No new setting and no per-person rights.
- A simple default page until someone builds one (§4.6) **(my call)**.

**Non-goals**
- **Events** on the splash page, and in-viewer search. Both wait for the search project on the work list's Later list (owner, 2026-11-01: a full "post an ad and search" built into the grid). The block list is built so an "Upcoming events" block can be added then.
- **Per-person rights.** Discussed and dropped by the owner as overkill.
- **Languages** (1.5). Text blocks are English only now, stored so a later language can be added per block (§5).
- Several saved designs, schedules ("show the party page on Saturday"), or a different page per viewer.
- A page built from owner HTML, CSS or script. The work list (2.13) rules out custom HTML skins.

**Anti-goals**
- Anything from the admin side on the public page: no avatar names of who's online, no region owners, no IP addresses, no settings.
- A page that is unreadable or cut off in a small viewer window.
- A half-finished edit showing to residents while someone is still working on it.
- An upload that turns into a way to serve arbitrary files from the grid (HTML, SVG with script, files of any size).
- A splash page that breaks the site's security headers (no inline script, no inline style).
- A page that's slow to show in the viewer because it waits on the database every time.

## 3. Constraints

- **Security headers stay as they are** (`Security/SecurityHeaders.cs`): `script-src 'self'`, `style-src 'self'`, `img-src 'self' data:`. The `InlineScriptScanTests` scan forbids `<script>` without `src`, `on*=` handlers, `style=` attributes and `<style>` blocks in page sources. So block positions, sizes and colours can't be written as inline styles; see §6.
- `frame-ancestors 'none'` and `X-Frame-Options: DENY` stay. Firestorm loads `welcome=` as a top-level page in its web view, not inside a frame, so they don't get in the way. This should be checked in Firestorm (§9).
- **Public reach:** `/welcome` must be added to `AdminNetworkFilter.PublicPaths` (`Security/AdminNetworkFilter.cs:33`). Today it isn't, so when the owner limits staff pages to some networks, residents get a 404 in the viewer. `SetupGate.AlwaysOpen` already lets it through.
- Where it shows: `GridConfigWriter` writes `welcome` on every machine. The editor and the built page belong to the machine that runs the grid service (the guest side). On a Region Server or Connected Server, `/welcome` keeps showing the default page (§4.6) **(my call)**, the same way `GuestSideOnly` keeps `/home` and `/legal` to the guest side.
- No new library unless needed. Bootstrap 5.3.3 is vendored; `wwwroot/js` is hand-written vanilla JS (`map.js` already does pointer-event dragging and resizing). The editor should be vanilla JS too **(my call)**. Image handling may need one library (§4.4); prefer one with an MIT or similar licence that works the same on Windows and Debian 12 and 13.
- Works on Windows and on Debian 12 and 13, like everything else.

## 4. What gets built

### 4.1 The editor (`/splash`, staff side)

- Lives under a new `/Splash` page folder. By the folder rule in `Program.cs` that is **AdminPolicy**: owner and admins, not supervisors, which is the owner's rule.
- A menu item "Splash page" next to the other grid pages **(my call)**.
- Layout: the block shelf on one side, the page canvas in the middle, the settings of the selected block (or the page, when nothing is selected) on the other side.
- **The design size** **(my call)**: the owner designs on a canvas of a fixed design size, 1280 × 800 by default (a common Firestorm window), changeable in the page settings. Positions and sizes are stored in design pixels and turned into proportions when shown (§6).
- **Placing:** drag from the shelf onto the canvas; drag a block to move it; drag its edges and corners to resize; arrow keys nudge the selected block (1 px, 10 px with Shift) **(my call: keyboard access)**. Buttons for bring forward, send back, duplicate and delete.
- **Snap to grid:** a switch, off by default **(my call)**, with a grid size choice (for example 8, 16, 32 or 64 design pixels) **(my call)**. While dragging, guide lines show when a block's edge or centre lines up with another block or with the page centre **(my call)**.
- **Preview sizes:** buttons to see the page at small (1024 × 640), medium (1280 × 800) and large (1920 × 1080) viewer windows **(my call)**, showing exactly what §6's shrink-and-scroll does.
- **Draft and publish** **(my call)**: changes save as a draft (automatically, or with Save). Residents keep seeing the published page until someone clicks **Publish**. "Discard draft" goes back to the published page. If two staff members edit at once, the later save is refused with a "someone else changed this; reload" message rather than silently overwriting it **(my call)**.
- Undo and redo inside one editing visit **(my call)**.

### 4.2 The blocks

Every block has a position, size, layer order, background colour, see-through level (0–100 %), an optional background picture (with the same fit choices as the page), rounded corners on or off, and padding **(my call for corners and padding)**.

| Block | What the owner sets | What residents see |
|---|---|---|
| **Text** | Text (plain paragraphs, bold, italic, links; the same safe Markdown as the Legal pages, `Guests/LegalMarkdown.cs`), font size, colour, alignment | The text |
| **Picture** | An uploaded picture, its fit choice, optional caption | The picture |
| **Grid name and logo** | Show name, logo or both; font size and colour. The logo is the **grid's one logo**, uploaded here but stored as a grid-wide picture (`Data/theme/logo`, through the same picture pipeline) so the front page and guest pages can use it later (work list 2.13's theme setting) **(my call)** | The grid name (from settings) and logo |
| **Live status** | Which lines to show: grid online or offline, people online now, regions running | For example "Online · 12 people · 40 regions" (§4.5) |
| **News** | A heading and one or more news items, each with a date and text (safe Markdown) | The newest items first; items can be hidden without deleting them **(my call)** |
| **Places to go** | Up to 8 regions picked from the whole grid's region list (§4.5), each with a picture (uploaded, or the region's map tile) and a line of text. A map tile is **copied into the picture store when picked**, so it goes through the same re-encoding and naming as an upload and no public route to Robust's map is needed; a "refresh picture" button copies it again **(my call)** | A row or grid of cards with the region name **(my call: cards don't teleport; see §9)** |
| **Links** | Which links to show: sign up, forgot password, Terms, privacy, the grid's front page | Links to those launcher pages. Sign up and forgot password show only when the grid is on the guest side and those pages are open (`AdminRule.HasStaffPages`, the sign-up switch) **(my call)** |

Text-carrying blocks (text, name, status, news, places, links) share font settings: a size in design pixels, a colour, bold, alignment, and a choice from a small list of web-safe fonts **(my call: no font uploads)**.

### 4.3 The page settings

Background colour; background picture with fit (fill, fit, stretch, tile, centred at actual size); focus point (click on the picture); darkening (0–80 %); design size; snap grid on/off and size.

### 4.4 Pictures

- Uploaded in the editor, used by the page background, picture blocks, block backgrounds, the logo and places cards.
- **[S]** Accepted types: PNG, JPEG and WebP, checked by the file's contents, not its name **(my call)**. No SVG, no GIF **(my call)**. Up to 10 MB per file **(my call)**.
- **[S]** **Re-encoded on upload** **(my call)**: the launcher decodes the picture and writes a fresh copy, which drops anything hidden in the file and EXIF data such as GPS position. A picture wider or taller than 3840 px is scaled down **(my call)**. This needs an image library (§3); which one is the builder's choice.
- **[S]** Stored in the owner-only data folder, `Data/splash/images/`, under a random name the launcher picks; the owner's file name is kept only as a label **(my call)**. Streamed in the way the OAR upload is (`Endpoints/LibraryApi.cs`: a `.incoming` part file, then a move), with the size limit raised for that one endpoint only.
- Served publicly at `/welcome/images/{random name}` with the right content type, `nosniff`, and long caching (the name changes when the picture changes) **(my call)**. Only pictures used by the **published** page are served publicly; draft pictures are served to staff only **(my call)**.
- The editor has a picture list: see what's uploaded, which blocks use each one, delete unused ones **(my call)**. Pictures no longer used by the draft or the published page are removed after 30 days **(my call)**.

### 4.5 Live status

- "Grid online" from `IGridController.GetStatus()`; people online from `IOnlineUsers`. Counts only, never names **(owner: "no admin data in it", from the work list)**.
- **"Regions running" covers the whole grid**, including regions on Region Servers **(my call)**. `GetStatus()` only knows this machine's regions, so the count (and the Places to go picker) reads Robust's region table in the database, the way `OnlineUsers` reads Presence: a region counts as running when it is registered with Robust right now. Same short timeout and "unavailable" path as `OnlineUsers`.
- "People online" counts residents in the grid's Presence, as `OnlineUsers` already does. Hypergrid visitors count if Presence lists them; the builder notes what it does **(my call)**.
- If the database can't be reached in time, the block shows "Status unavailable" rather than a wrong number or an error.

### 4.6 The default page

Until anyone publishes, and on a Region Server or Connected Server, `/welcome` shows a plain built-in page: the grid name, the live status line, and the links block, over a dark background **(my call)**. The editor opens with this same layout as the starting draft, so the owner starts from something rather than an empty canvas **(my call)**.

### 4.7 The public page (`/welcome`)

- Anonymous, with its own small layout: no staff menu, no Bootstrap JavaScript **(my call)**. It replaces today's stub, which uses the staff `_Layout`.
- **No JavaScript needed** to show the page **(my call)**. All layout is in a stylesheet (§6). Firestorm's built-in browser, other viewers and plain web browsers all see the same thing.
- **[S]** Read-only. Nothing on it posts anything.
- **Caching** **(my call)**: the page and its stylesheet are sent with `Cache-Control: public, max-age=60`, and the launcher keeps the built page in memory for 60 seconds. So the status numbers are at most about a minute old, a publish shows within a minute, and a busy login hour doesn't hit the database for every viewer.

### 4.8 Audit

Written with `AuditLog` the same way the Legal page does (`AuditActor.Of(User)`, an `AuditEntry`):
- `splash-published`, `splash-draft-discarded`, `splash-image-uploaded`, `splash-image-deleted`.
- Draft saves aren't audited one by one (they could be many per minute); `AuditMiddleware` already logs every staff POST **(my call)**.

## 5. Stored data

- The **draft** and the **published** design are each a JSON document: the page settings plus a list of blocks with their kind, position, size, layer, style and content. A version number and a saved-by and saved-at on each **(my call)**.
- Stored in `Data/splash/draft.json` and `Data/splash/published.json` rather than inside `settings.json` **(my call)**: a design with many news items is too big and too often saved to sit in the main settings file. Written the same safe way the settings file is (write a temporary file, then replace), UTF-8 without BOM.
- **[S]** On every save the server checks the document against the block rules: known block kinds and fields only, sizes and positions within bounds, at most 100 blocks **(my call)**, text lengths capped, pictures that exist, colours as `#rrggbb`. Anything else is refused. The public page is built only from checked data, never from the stored text as-is.
- Text fields are stored as `{ "en": "…" }` so that 1.5 can add languages without changing the format **(my call)**.

## 6. How "place anywhere", "shrink then scroll" and the security headers fit together

This is the main technical choice; the builder may do it differently if the behaviour is the same.

- **Positions are proportions.** A block at (x, y, w, h) in design pixels is shown at left = x / design width, top = y / design height, and so on, inside a page box that keeps the design's shape (its aspect ratio).
- **Shrinking.** The page box is as wide as the viewer window, but never wider than the design width × 1.5 **(my call)** and never narrower than a **minimum width** **(owner: shrink until not readable, then scroll)**. Below the minimum the page box stops shrinking and the window scrolls. Text sizes are in units of the page box's width (CSS container units, `cqw`), so text shrinks with the page.
- **The minimum width** is worked out from the design: the scale at which the smallest text on the page would be 12 px on screen **(my call)**, and never below 50 % of the design width **(my call)**. The editor shows the owner this point in the preview sizes.
- **Larger windows.** Above the design size the page box grows up to the 1.5 × cap and is centred; the page background fills the space around it **(my call)**.
- **No inline styles.** The launcher writes each published design's styles into a stylesheet, `/welcome/page-{version}.css`, one class per block (`.b-17 { left: …; }`). The page links to it. This keeps `style-src 'self'`. The editor, which needs live positions while dragging, sets them through JavaScript on the element's style object, which the policy allows (only `style=` attributes in the markup and `<style>` blocks are blocked); the scan test still passes because the page sources contain neither.
- **Assumption to check:** Firestorm 7.x's built-in browser supports CSS container units (they need Chromium 105 or newer). If a viewer's browser is too old, the page should still show at the design layout with scrolling, not break (§9).

## 7. Checks and tests

- The block rules check (§5): good documents pass; unknown kinds, out-of-bounds sizes, oversize text, bad colours, missing pictures, too many blocks are refused.
- Who can reach what: owner and admin can open and post to `/splash`; a supervisor, a resident and an anonymous caller get refused, and refused posts are audited.
- Publish flow: a draft change doesn't change `/welcome` until Publish; Discard returns to the published design; two editors don't overwrite each other.
- Pictures: a renamed HTML or SVG file is refused; an oversize file is refused; EXIF is gone after upload; draft-only pictures aren't served to anonymous callers; unused pictures are cleaned up.
- Public page: no admin data in it (no avatar names); `/welcome` is reachable with an admin network list set; caching headers are right; the page works with JavaScript off; the default page shows before any publish and on a Region Server.
- The existing inline-script and inline-style scan covers the new pages.
- By hand: a real Firestorm on Windows against a Debian 12 grid shows the published page, it shrinks and then scrolls in a small window, and the numbers update within about a minute.

## 8. What's left to the builder

- The exact look of the editor, within what's above.
- The image library (§4.4) and how pictures are resized.
- The exact CSS approach for §6, as long as the behaviour and the security headers hold.
- How the editor's undo, autosave interval and keyboard shortcuts work.
- Splitting this into one or more plans (for example: storage, checks and the public page first; then the editor; then pictures).

## 9. Open points to check during the work

- Firestorm (and, later, other viewers): does the splash web view load a page with `frame-ancestors 'none'` and `X-Frame-Options: DENY`? Does it support container units? What does it do with links on the splash page (open in an outside browser, or inside)?
- Could a "Places to go" card set the viewer's start location or teleport (for example a `secondlife:///app/...` link)? If Firestorm supports it from the login screen, the owner may want it; until then cards are pictures and names only.
