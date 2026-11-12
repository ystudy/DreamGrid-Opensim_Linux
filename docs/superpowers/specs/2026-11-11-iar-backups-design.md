# Inventory backups (IAR): design

Worklist item 2.11 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`). Agreed with the owner on
2026-11-11. The starter avatars (2.1) come next, as a separate design, and use the staff load described here.

Throughout, **(owner)** marks what the owner decided, and **(our call)** marks what was settled while writing this. The
owner can overturn any (our call) when reading.

## 1. Why

Residents need to **back up their inventory** and **take it to other grids** (owner). Wifi let a resident upload an IAR
into their own inventory and nothing more: there was no download and no staff side, and the upload ran inside the web
request, with no size limit, no progress, and a temp file that was never deleted
(`addon-modules/21Wifi/WebApp/Services.Inventory.cs`, `InventoryUploadRequest`). GridGate replaces that before Wifi is
removed (Phase 3).

Two things matter as much as the feature itself:

- **Creators are protected.** A resident can't use an IAR to carry someone else's build off the grid (owner).
- **It can't be used to troll the grid.** No filling the disk, no flooding it with junk, no eating the bandwidth (owner).

## 2. Goals

1. A resident can **download** (save) their inventory as an IAR from their account pages.
2. A resident can **upload** (load) an IAR into their own inventory.
3. **Download includes only full-permission items** (owner): an item goes in only when the resident can copy, modify
   and transfer it. Everything else stays on the grid, and the resident is told what was left out and why.
4. **Upload assumes the items are the resident's** (owner), unless they match something taken down (banned).
5. **Four switches** (owner): upload and download, each grid-wide and per resident.
6. **Limits on everything, as settings** (owner): sizes, counts per day, jobs at once, and bandwidth. The owner can
   raise them for the whole grid, and (our call) for one resident.
7. **The owner and admins work without restrictions** (owner): they can save or load any avatar's inventory, whatever
   the permissions or limits, even when that resident's switches are off. "They are the police on the grid." Supervisors
   and other staff do **not** have this power (owner).
8. **Every use of those powers is documented clearly and in detail** (owner). See section 7.

## 3. Non-goals

- Browsing or editing inventory on the website (Wifi's move, delete and new-folder actions). Residents do that in the
  viewer.
- IARs of part of an inventory chosen by the resident. A download is the whole inventory (our call; a folder picker can
  come later if asked for).
- Region Server machines. IAR runs where the guest side runs, on the grid machine (as with sign-up and the account
  pages).
- Starter avatars. They are the next design.

## 4. Anti-goals (failure even if it works)

- A no-copy or no-transfer item leaves the grid inside a resident's IAR, **including inside an object's contents** (see
  6.3).
- A taken-down item comes back through an upload.
- A resident can stall the grid, fill its disk, or use up its upload bandwidth.
- An IAR can be fetched by someone other than the resident it belongs to, or from a guessable address.
- An owner or admin action on someone's inventory that isn't in the audit log, or is in it without a reason.

## 5. The resident's side

### 5.1 Where

On the resident's own pages (`/guest/...`, under GuestPolicy, the TermsGate and the resident's language). One page,
`/guest/inventory` (our call on the address), with two parts, **Back up my inventory** and **Bring in inventory**. Each
part shows only when its two switches allow it. When both are off, the page and its link are gone (404, like
`UploadPagesOnly`).

A resident **named** in an open takedown case (`PersonOnCase.Named`, the person the notice is about) can't download or
upload while the case is open (our call). They see "Inventory backups are not available on your account right now", and
no case details. **Copy holders** (`CopyHolder`, often innocent buyers) keep their backups: the taken-down copy is
already gone from their inventory. This is a new check of its own, not `AccountHolds`, which also holds copy holders and
holds everyone when the case files can't be read. If the case files can't be read, resident **uploads** are refused
("try again later"), because the banned check can't run. Downloads go ahead (our call).

### 5.2 Download

1. The resident presses **Back up my inventory**. The job is refused, with a plain message, if a limit is reached
   (section 8) or no region is running to do the work.
2. The job runs in the background with progress on the page (the OAR job pattern): **waiting**, **saving**, **ready**.
3. When it's ready, the page shows a **Download** button, and the resident gets an email if email is set up.
4. The page lists **what was left out**, with each item's name, folder and reason (for example "no transfer", "no copy",
   or "contains a no-transfer item"). Then nobody thinks their backup is broken.
5. The link works **only for that resident, while signed in**, and for a limited time (a setting, 24 hours to start). It
   is never a guessable path: an opaque random token, checked against the signed-in principal and session stamp, so a
   suspended or signed-out resident can't use it.
6. Downloads are **throttled** to the per-download bandwidth setting, and resumable (range requests, as the OAR library
   does).
7. The file is **deleted** when the link expires. One kept file per resident: a new backup replaces the old one.

### 5.3 Upload

1. The resident picks a file. It uploads with a progress bar (the OAR library's streamed upload, not form binding). The
   size cap is enforced **while streaming**, so an oversized file is cut off, not stored.
2. The file is checked before anything loads:
   - it really is an IAR (a gzip tar with `archive.xml`, and inventory entries);
   - its **unpacked** size and item count are within the limits, which stops a small file that unpacks huge;
   - **banned content is removed** (6.4).
3. It loads in the background with progress, into a **new folder** in the resident's inventory, for example
   `Imported 2026-11-11` (owner agreed). It never merges into or overwrites existing folders. If that name is taken, add
   a number.
4. The resident sees a summary: items loaded, and how many were left out because they matched removed content. Those
   left out are counted without being named, and the takedown case is never named.
5. The uploaded file and anything unpacked from it are deleted once the load finishes or fails.

### 5.4 Words

All resident text and the email go through the resident languages (plan 16a), with new texts in the six languages.

## 6. How the work is done

### 6.1 OpenSim commands

IARs are made and read by OpenSim's region-side `save iar` / `load iar` console commands
(`OpenSim/Region/CoreModules/Avatar/Inventory/Archiver/InventoryArchiverModule.cs`). The launcher runs them on a region
console, the way `GridController.RunOarJobAsync` runs `save oar` / `load oar`:

- Use any running region program on the grid machine. It takes that program's job slot, so an OAR job on it waits (or
  is refused) meanwhile, and the reverse. If none is running, refuse with "try again later". Which region to pick is
  the builder's call: the least busy one is a reasonable rule.
- `save iar` for a resident: `--perm=CMT` (copy, modify, transfer), the whole inventory (`/`), written to the IAR
  staging folder. For staff saves (7.2): no `--perm`.
- To work on other grids, save with `--home=<this grid's hypergrid address>` so creators are recorded with their home
  grid (the builder checks what `--home` and `-c|--creators` really do in this OpenSim and picks what keeps creator
  names on the other grid).
- Completion is detected the way `SaveOarAsync` does it: following the region's log for the "Saved archive with N items
  ... Skipped M items" line, or an error. The skipped count and names feed the "left out" list (6.3 has the details).
- `load iar` into the new folder's path. Check how this OpenSim handles a target folder that doesn't exist yet, and
  whether it needs the avatar's password (the builder confirms; current code shows no password argument).

### 6.2 Files

A new owner-only folder, for example `<Data>/iars` with `.incoming`, `.saving`, `ready/` and `staff/` (our call on the
names), created with `LauncherPaths.CreateOwnerOnly`, because IARs hold residents' inventory. Files are named with
random IDs, never with avatar names. Staging is cleared at start-up, as `OarLibrary.ClearStaging` does. Expired files
are swept on a timer.

### 6.3 The full-rights rule, done properly

OpenSim's `--perm` checks only each inventory item's own `CurrentPermissions`
(`InventoryArchiveWriteRequest.CanUserArchiveObject`). That's not enough:

- An **object** can be full permission while holding no-copy or no-transfer items in its contents, or prims with lower
  permissions. A **coalesced** object (several taken at once) can do the same.

The rule (owner) is that nobody takes someone's build unless they have full rights to it, so a resident's download must
also leave out any object whose contents or parts aren't full permission for the owner. The builder chooses how: a
small DreamGrid patch to the IAR writer that checks object contents and parts when a permission filter is set, or a
check of the finished file by the launcher before it's offered. Either way, **tests pin it**: a full-permission box
holding a no-transfer item is left out.

The "left out" list needs item names and reasons. The writer already logs each skipped item ("Insufficient permissions,
skipping inventory item ..."). The builder either reads those lines from the log or has the patch write a small list
next to the IAR. Reading a list file is the safer choice of the two.

### 6.4 Banned content on upload

"Banned" means **an item taken down in any takedown case, open or closed, that hasn't been put back** (`TakedownStore`,
plan 15) (owner). A removed item can never be uploaded again. Two fingerprints:

- the **asset ID** (`CaseItem.AssetId`);
- the **content hash**: FSAssets' own SHA-256 of the asset data (`robust.fsassets.hash`, read with
  `IInventoryStore.AssetHashAsync`). The case's `EvidenceSha256` is the hash of the stored (usually gzipped) file, so
  it is not the one to compare.

Before loading, the launcher reads the IAR's asset files, hashes each one the way FSAssets does (`Util.SHA256Hash` of
the data), and compares IDs and hashes with the banned set. An inventory item is removed when its asset is banned, or
when it's an object holding a banned asset. Banned assets are removed too. The cleaned IAR is what gets loaded. Each
match adds an event to the matching case ("came back in an upload from <resident>, removed") and an audit line. This
applies to **closed cases too, without reopening them** (owner). Today `TakedownActions` refuses any change to a closed
case, so this needs its own narrow write path that can only append this kind of event. The case list shows that a
closed case has had a match since it closed.

### 6.5 Robust and region safety

- One IAR job at a time per resident, and a grid-wide cap on IAR jobs at once (section 8).
- A job keeps going if the resident leaves the page. A launcher restart cancels it cleanly and deletes its files.
- A load into a resident who is logged in is allowed (our call). The viewer shows the new folder after a relog or an
  inventory refresh, and the summary says so.

## 7. The owner and admins

### 7.1 Who

Only the **owner** and **admins** (`AdminPolicy`) (owner). Supervisors can't see these actions.

### 7.2 What

On the resident's People page (`/people/{id}` or `/people/{id}/inventory`):

- **Save inventory (IAR):** the whole inventory, **no permission filter, no limits**, even with the resident's switches
  off or the account held.
- **Load inventory (IAR):** any IAR into a chosen folder of that avatar, with no limits. The file comes **either from a
  browser upload with no size limit** (streamed, like the OAR library's upload, with the cap lifted only for this
  owner/admin endpoint), **or picked from the staff evidence saves** in `<Data>/iars/staff/` (owner). The uploaded file
  still has to be a real IAR, and it's deleted after the load. This is also how the starter
  avatars' model accounts are filled (next design). Banned matches are **reported, not removed** (our call): the
  police can load what they need to, but they see the warning before confirming.
- The four **switches** for that resident, and their **own limits** (raise or lower them, or "use the grid's").

Staff saves are **evidence** (our call): kept in `<Data>/iars/staff/` and downloadable by the owner and admins only. They
aren't deleted by the resident sweep, and they're removed only by an owner or admin, with a reason. A staff save tied to
a takedown case (open or closed) puts an `AccountHolds` hold on the account while it is kept, so the account can't be
deleted under it. That hold **only stops deletion**: it doesn't affect the resident's own backups, which follow 5.1
only. (Our call; the owner can say instead that staff just download it like a resident.)

### 7.3 Documented clearly and in detail (owner)

Every owner or admin IAR action needs a **typed reason** (required, at least a sentence) and, optionally, a **case
number**. The audit log gets one entry per step: asked, started, finished or failed, downloaded, deleted. Each entry
holds:

- who (name, ID, role), the address they used, and the time;
- whose inventory, and whether it was a save or a load;
- the reason and the case number;
- the file name, its size, and its SHA-256;
- the item and folder counts, which folder was loaded into, and for loads, the banned matches;
- the result.

The resident's People page shows these entries in its history. The existing one-line `AuditEntry` holds the short form,
and the full detail goes in a record kept with the file, or in a detail field (the builder's call). Either way, nothing
in section 7 happens without a full record.

Resident actions are audited too (role `guest`): asked, refused (and why), finished, downloaded, uploaded, items left
out, banned matches.

## 8. Switches and limits

All on a new owner-only settings page, **Inventory backups** (under `/security`, like Bots). Per-resident overrides
live on the People page. A per-resident value replaces the grid's value, and "use the grid's" clears it.

| Setting | Starts at (our call) |
|---|---|
| Downloads allowed (grid) | **Off.** The owner turns it on after setting the limits. |
| Uploads allowed (grid) | **Off**, same reason. |
| Largest upload | 500 MB |
| Largest unpacked size of an upload | 4 × the largest upload |
| Most items in one upload | 20,000 |
| Largest download | 2 GB. Bigger inventories are refused, with a note to ask staff. |
| Downloads per resident per day | 2 |
| Uploads per resident per day | 2 |
| IAR jobs at once, whole grid | 1 |
| Download speed per download | 2 MB/s |
| Download link lasts | 24 hours |
| Keep free disk space | Refuse new jobs when less than 5 GB is free |

The per-resident switches start **on**: when the grid switch is on, everyone may, unless they've been switched off
(our call, which matches "switched off per guest").

When a limit stops someone, they see a plain message naming the limit ("You can back up twice a day. Try again
tomorrow."), and the refusal is audited. The daily counts use the rolling-window pattern (`ResetLimits`). Counts are
per account, so signing in from other addresses doesn't help.

## 9. What's left to the builder

- The exact addresses, page layout and folder names.
- How the left-out list is produced (6.3) and how full detail is stored (7.3).
- Which region runs a job (6.1), and the `--home` / `--creators` flags.
- Tests: the permission rule including object contents (6.3), banned removal by ID and by hash (6.4), every limit and
  switch, a guessed or other resident's download link refused, and staff saves ignoring the rules but always audited.
- The Debian 12 VM check for the owner, written into the worklist: download as a resident, look at the left-out list,
  upload that IAR on a second grid (or a second account), upload a file holding a taken-down item, hit each limit once,
  and do a staff save and load with a reason.
