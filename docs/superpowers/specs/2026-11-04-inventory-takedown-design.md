# Inventory takedown (worklist 2.5b) — design

Date: 2026-11-04. Worklist item 2.5, part b. Part a (people admin) is done. Part c (history for legal requests) gets its own design and builds on the hold made here.

The owner handed the details to me ("You have better knowledge of this. please work out the details."). Except where a line says it is the owner's, everything below is **my call**, and the owner checks it when reading.

## Why

A public grid gets notices about things in people's inventories: a copyright holder says a product is a stolen copy of theirs, or the police say an item is illegal. The owner's words: "an illigal item in inventory that has a takedown, It might have a history that the FBI needs", and "on public grids protection is an owners requirement." Today staff can't see or touch anyone's inventory from the website. The only way out is to delete the whole person, and that also destroys the evidence.

## Goals

- Staff open a person's inventory on the website, find the item a notice names, and take it out of their inventory.
- Each takedown is a **case** that keeps who sent the notice, what it said, which items were removed from whom, when, by which staff member and why.
- A printable page **for the sender** shows that the items were removed, without naming the resident.
- An item taken down by mistake (a counter-notice, or the wrong item) can be **put back** exactly as it was.
- Copies of the same thing in **other people's inventories** are found and can be taken down in the same case.
- While a case is open, every account it names is **on hold**: it can't be deleted (worklist: "so it can't be deleted while the takedown is open").
- The item's content is **kept as evidence**, even when everyone's copies are gone.

## Non-goals

- Objects already rezzed in regions. They aren't inventory, and a rezzed object can't be traced back to the item it came from. Staff return them in-world with the viewer's region tools. The case page says so.
- Changing or blanking the asset itself, so that every copy everywhere turns into a "removed" picture. It reaches too far (caches, other grids) for this step.
- Copies that left the grid by hypergrid. Another grid's inventory is out of reach.
- Exports and long-term history for legal requests: that is 2.5c.
- Sending anything to the notice sender automatically. Staff print or save the sender's page and send it themselves.
- Showing what an item looks like or contains (its texture, its notecard text, its script) on the website. See Anti-goals.

## Anti-goals

- **Destroying evidence.** A takedown takes an item out of reach; it never deletes the only copy of it.
- **The website showing or serving illegal content.** Staff work from names, types, creators and ids. The kept copy is a file only the launcher can read, and no page links to it.
- **Tipping off a suspect.** The resident is told about a copyright takedown so they can answer it, but not about an illegal-content case.
- **Leaking the resident to the sender.** The sender's page shows the items and dates, not who held them.
- Staff browsing people's inventories quietly. Opening someone's inventory is written to the audit log.

## Who may do it

- **The owner and admins.** Supervisors keep only what 2.5a gave them (looking people up, suspending), so they don't see inventories or takedowns.
- On **residents and suspended residents**, like the 2.5a tasks. Staff accounts are made residents on the Staff page first; the owner's inventory isn't touched here. Nobody uses this on their own inventory.
- These rules are added to `PeopleRules` as a new task, so the greyed icon reasons work as they do for the other tasks.

## Where it lives

- **Person's page** (`/people/{id}`): a new **Inventory** task (a box icon in the same sprite), and a line "Takedowns: N cases (M open)", linking to them. The count helps staff spot a repeat offender and decide on a suspension.
- **Inventory page** (`/people/{id}/inventory`): the person's folders, one folder at a time, with a breadcrumb back to My Inventory. Each folder lists its subfolders and its items. A search box finds items by part of their name anywhere in the inventory, Trash included. Results are paged (50 at a time). Each item row shows name, type (texture, object, notecard, script and so on, in words), creator, created date, and a checkbox. A button **Take down selected** goes on.
- **Takedowns list** (`/people/takedowns`, linked from `/people`): every case, open ones first, newest first, with number, opened date, reason, sender, items, people, status.
- **A case** (`/people/takedowns/{number}`): everything below, with Put back per item, Find copies, Add a note, and Close.
- **For the sender** (`/people/takedowns/{number}/sender`): a plain printable page. It is still behind the staff sign-in; staff print it or save it as a PDF from the browser.

## Taking an item down

1. On the inventory page, staff tick one or more items and press **Take down selected**.
2. A form asks for the notice. It can also add the items to an open case instead of starting a new one.
   - **Reason**: Copyright (DMCA), Illegal content, Breaks the grid's rules, Other.
   - **Sent by**: name, organisation (optional), email or postal address (free text).
   - **Their reference** (optional), the **date the notice arrived**, and **What the notice says**: free text, for the part naming the item.
   - **Tell the resident**: ticked by default for Copyright and Rules, unticked and greyed with the reason for Illegal content.
3. The person must be **offline**, as for a rename: the viewer holds the inventory in memory while they are in-world. If they are in-world, the page says "Sign them out first: they are in-world." and offers Sign out everywhere.
4. On confirm, for each item:
   - its whole inventory row is saved in the case, every column as it was;
   - its asset is copied into the case's evidence folder (below);
   - the row is deleted from `robust.inventoryitems`, and the folder it was in gets its `version` raised by one. The version raise matters: viewers keep a cached copy of inventory per folder and fetch it again only when the version moves.
   The item is gone from their inventory at the next login. It is gone everywhere the grid's inventory service is asked: rezzing, giving, wearing.
5. A new case gets the next number, `TD-2026-001` (year, then a count that restarts each year).
6. If **Tell the resident** is ticked and they have an email address, they get one email. It names the items, the reason, the case number, and says to reply to the grid's address if they think it is a mistake. It doesn't name the sender, because the notice may contain personal details; staff pass a counter-notice on themselves.
7. Audit: `takedown-opened` (or `takedown-added`) with target `First Last: TD-2026-001`, and one `takedown-removed` per item with target `First Last: TD-2026-001 <item name>`. These match 2.5a's "Name:" form, so they show in the person's own history.

## Copies held by others

- On the case page, **Find copies** looks for items with the same asset id as a removed item, in every account's inventory. Copying an item in-world keeps its asset id; changing it makes a new asset, so a changed copy isn't found. The page says so.
- It lists each holder (name, item name, folder) with a checkbox, and **Take these down too** removes the ticked ones into the same case, by the same steps. Holders who are in-world, staff, or the owner are listed but can't be ticked, with the reason, as on the task icons.
- Each holder added becomes a person on the case and is held too. Each one is told, or not, by the case's Tell the resident choice.

## Evidence

- Each case has a folder under the launcher's state: `state/takedowns/TD-2026-001/`. It holds `case.json` (the record) and an `assets/` folder.
- For each removed item, the asset's stored file is copied from the FSAssets store to `assets/<asset id>`, byte for byte, with its SHA-256 in the record. For an object, the asset is the object itself; the textures and scripts inside it are separate assets and **are not followed**. The record says so. Those stay in the FSAssets store anyway, because removing an item never deletes assets.
- The folder is readable only by the launcher's own user (0700 on Linux). No web page reads or serves it. 2.5c decides how evidence is exported for a legal request.
- If the asset can't be found or copied, the takedown still goes ahead (taking it out of reach matters more), and the record and the case page say "content not kept: <why>".

## Putting an item back

- On an open case, each removed item has **Put back**, with a required "why" (for example "counter-notice received 12 Nov").
- The saved row goes back as it was, same item id and permissions, into its old folder if that folder still exists. Otherwise it goes into their Lost And Found folder, and if there is none, into My Inventory. That folder's version is raised.
- The person must be offline, as for taking it down. If they have been deleted, the item can't be put back and the button says why. (Held accounts can't be deleted while the case is open, so this happens only after it closes.)
- The resident is told if the case tells residents. Audit: `takedown-restored`.
- The evidence copy stays. The record keeps the whole history: removed, then put back, with who, when and why.

## The case record

Kept in `case.json` and shown on the case page, oldest event first:

- number, status (Open or Closed), reason, the notice (sender, organisation, address, reference, date received, the text), tell-resident choice
- each person on the case: principal id and their name at the time
- each item: item id, name, description, type, creator (name and id; a hypergrid creator's name from the creator field), holder, folder path at the time, asset id, the saved inventory row, the evidence file and its hash or why there isn't one
- each event, with time (UTC), staff name and id, and the why where there is one: opened, item removed, copies searched, item put back, note added, resident emailed, closed

**Add a note** puts a free-text line on the case, for a reply from the sender or a call with the police. Audit: `takedown-note`.

**Close** needs a short outcome ("removed for good", "counter-notice, put back", "withdrawn by sender"). A closed case is read-only and can't be reopened; a new notice gets a new case that can refer to it. Audit: `takedown-closed`. Cases are never deleted from the website.

## The sender's page

Shows the grid's name, the case number, the notice as received (sender, reference, date), and for each item its name, type, creator, and the date and time it was removed, or put back with the date. It ends with "Objects already placed in regions are not covered by this list." It doesn't show the holders' names, ids, folders, or anything staff wrote in notes.

## The hold

- `AccountHolds.IsHeld(principal)` is true while any open case names the principal. Delete already asks it (2.5a) and refuses with "This account is on hold."
- The person's page shows "On hold: takedown TD-2026-001", linking to the case.
- When the case closes, its hold ends. 2.5c will add its own reasons to hold through the same class.
- If the case files can't be read, `IsHeld` answers **true** for everyone and delete says the takedown records couldn't be read. It is better to refuse a delete than to destroy evidence.

## Security

- Everything stays behind `StaffPolicy`, plus an owner-or-admin check on each page and handler, with antiforgery tokens on every post, never a GET.
- Every query is bound SQL, as robustuser, like `AccountRemover`. Removing items runs in one transaction per person (the row deletes and the version raises).
- Free text (notice, notes, outcome) is length-limited (4000 characters for the notice text, 1000 for others) and HTML-encoded on display. It never goes into the audit log: the audit log gets names and case numbers only, through `LogText.Clean`.
- Opening a person's inventory page writes `inventory-viewed` with their name, once per page load. Searching and moving between folders count as page loads.

## Left to the builder

- Direct SQL on `robust` (`inventoryitems`, `inventoryfolders`, as `AccountRemover` uses them) is the expected way in. Robust's private inventory service is the alternative if direct SQL turns out to miss something.
- How to find an asset's stored file from `robust.fsassets` (its hash), and the FSAssets folder layout and compression. Copy the stored file as it is.
- The type names for each `assetType` and `invType`, and folder icons.
- How the case store is written (`JsonStateFile`/`AtomicFile` per case) and how case numbers are given out safely when two staff open cases at once.
- Index needs for Find copies (`inventoryitems.assetID`) on a large grid.
- Tests: the rules (who sees the icon, greyed reasons), take down and put back against a test database (row gone and back the same, folder version raised, Lost And Found fallback), find copies, the hold refusing delete, unreadable case files holding everyone, the sender page not containing holder names, and the resident email only when it should be sent.
- The owner's check on the VM, in Firestorm:
  - give a test resident an item, sign them out, and take it down; log in as them and see it gone
  - give a copy to a second resident, Find copies, and take that down too
  - try to delete the first resident and be refused because of the hold
  - put one item back, log in and see it back in the same folder
  - close the case, print the sender's page, and then delete works
