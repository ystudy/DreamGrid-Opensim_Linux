# Hypergrid links and estates — design (work list 2.9 and 2.10)

The owner skipped the questions ("use your best judgement", 2026-11-05), so every choice below that isn't from the work list is marked **(my call)** for the owner to check.

## Why

Wifi let staff link regions on other grids onto this grid's map (`linkregion*`) and edit estates (`estatelist`, `estateeditform`). GridGate replaces both, with the same safety rules as the other admin pages: every change is checked again on the post, audited, and written the way OpenSim writes it.

## 2.9 Hypergrid links

### What it is
A hypergrid link is a row in Robust's `regions` table with the `Hyperlink` flag (512): a region on another grid shown on this grid's map, so residents can teleport there from the map or a landmark. Robust makes the row itself (`link-region` on its console): it asks the other grid's gatekeeper for the region, fetches its map tile and stores the row.

### Who and where
- The owner, admins and supervisors (owner, 2026-11-03). The page is `/hypergrid`, in its own folder with `StaffPolicy`.
- Only where this computer runs Robust (the guest side) — 404 elsewhere, like Groups. When hypergrid is off, the page still opens, says hypergrid is off and where to turn it on, and offers no Add; existing links are still listed and can be removed **(my call)**.
- A **Hypergrid** menu item for staff, next to Groups.

### The page
- A list of links: the name the other grid gave, its address, map location (x, y in regions), and **Check** and **Remove** buttons.
- **Add a link**: one address box and optional X and Y.
  - Address forms accepted: `grid.example.org:8002`, `http://grid.example.org:8002/`, `https://…`, each optionally followed by a region name, as `grid.example.org:8002:Region Name` or `http://grid.example.org:8002/ Region Name`. No region name means the other grid's default region.
  - Blank X and Y: the launcher picks a free cell on a row two above the grid's top region, starting at the grid's left-most column **(my call)**; that keeps links within 4096 regions of the grid, which old viewers need.
- Removing a link deletes its row (only a row with the Hyperlink flag, matched by UUID), the same as OpenSim's `unlink-region` does, but without its by-name lookup that can match the wrong row **(my call)**.

### [S] Checks before Robust is asked
1. The address: `http` or `https` only; a host; a port 1–65535 (default 80 / 443); no user name or password, query or fragment; a path only of `/`. Region name: at most 64 characters, no control characters, no double quotes. Anything else is refused in plain words.
2. Not this grid's own address (Robust refuses that too, but the launcher says it plainly).
3. The link test: the launcher fetches `<address>/get_grid_info` itself with
   - a 10-second timeout and at most 64 KB read,
   - **no redirects followed**,
   - a connection that refuses any address that is loopback, private (RFC 1918, CGNAT 100.64/10, IPv6 ULA), link-local (including 169.254.169.254), multicast or unspecified, checked on the address actually connected to, so a DNS answer that changes can't sneak through.
   It must answer 200 with grid info XML; the grid name it gives is shown. Only then does Robust get `link-region`.
4. Not covered: Robust then makes its own requests to the other grid. Those can't be limited from the launcher. Between the check and Robust's request, a DNS change could point it somewhere else; the check makes that much harder but not impossible. Written down here so nobody thinks otherwise.

**Check** on an existing link runs the same test (3) and says whether the other grid answers.

### Audit
`hyperlink-added` (`<name> <address> <x>,<y>`), `hyperlink-removed`, `hyperlink-refused` is not written (a refused address is the person's typo, not an action).

## 2.10 Estates

### What it is
Estates live in the regions' database (`opensim`: `estate_settings`, `estate_map`, `estate_managers`, …). Every region on this computer is in one. The grid's own regions are in `"<World name> Estate"`, owned by the grid owner. The launcher already makes an estate per region owner (`"<First Last> Estate"`, `RegionRights`) and moves their regions in and out of it.

### Who and where
The owner and admins, `/estates` (AdminPolicy by the folder rule), only with a guest side (`GuestSideOnly`, like Groups) **(my call: supervisors don't manage estates, the same as groups)**. An **Estates** menu item next to Groups.

### The pages
- `/estates`: every estate with its name, owner, number of regions and number of managers.
- `/estates/estate?id=N`:
  - **Name** — rename. Unique, 1–64 characters, words with single spaces (the console rule already used for estate names).
  - **Owner** — change by avatar name (a resident of this grid).
  - **Managers** — list, add by avatar name, remove. At most 10 **(my call: the viewer's own limit)**. The owner can't be a manager.
  - **Regions** — this computer's regions in it; **Move a region here** picks from this computer's regions in other estates. Regions joined from other computers aren't in this database and aren't shown (said on the page).
  - **Delete** — only for an estate with no regions, after a ticked box.
- `/estates` also has **New estate** (name and owner).

### Protected estates **(my call)**
- **The grid's estate** (`"<World name> Estate"`): can't be renamed (OpenSim puts new regions in the estate of that name, so a rename would quietly make a second one) and its owner is always the grid owner. Managers can be changed. Can't be deleted.
- **A region owner's estate** (`RegionRights` knows it): its owner can't be changed here — the launcher moves that person's regions in and out of it. Name and managers can be changed. It can be deleted when empty (the launcher makes a new one if that person is given a region again; that code already handles a missing estate).
- **A region with an owner** (`RegionRights.Of(region)` is set): can't be moved to another estate here; the page says to change who owns it on the region's page.

### How the changes are made
- Rename, owner, managers, delete: written straight to the `opensim` tables (as opensimuser, one transaction each, the way OpenSim's `MySQLEstateStore` writes them).
- A running region keeps its estate in memory and would write its stale copy back the next time someone edits the estate in the viewer. So after each change the launcher tells every running region program to read the estate again: a new OpenSim console command, **`estate reload <estate-id>`** (`Region/Application/OpenSim.cs`), which, for every region in that program in that estate, reloads the estate from the database and sends the new details to the viewers there. A program that isn't running reads the database when it starts. A program that doesn't answer is logged and the page says "It shows in the viewer after <region> restarts."
- New estate: the existing `estate create` on any running region program (OpenSim allocates the ID). With no region running, the page says to start one.
- Moving a region: the existing `estate link region`, which needs the region running.

### Audit
`estate-created`, `estate-renamed`, `estate-owner-changed`, `estate-manager-added`, `estate-manager-removed`, `estate-region-moved`, `estate-deleted`, target `"<id> <name> …"`.

## Not in this work
Estate access lists, bans, groups and the estate's own options (sun, public access, voice): those stay in the viewer's Estate window, where managers already set them.
