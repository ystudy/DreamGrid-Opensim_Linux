# Inventory backups (IAR)

Residents can back up their inventory as an IAR file and bring inventory in from another grid. The owner and admins can save
or load any avatar's inventory. This page is for the grid owner.

## Turning it on

Open **Guest pages → Inventory backups** (`/security/iar`). Both switches start **off**. Set the limits first, then switch on
downloads, uploads or both. Each change is in the audit log as `iar-settings`.

To switch one resident off, or to give them more (or less) than the grid's limits, open their page under **People** and use the
**Inventory backups** panel. A box left empty uses the grid's value.

## What residents get

Residents find **Inventory backups** on their account pages (`/guest/inventory`) when the grid's switch is on and theirs isn't off.

- **Back up my inventory.** The backup runs in the background. When it's ready the page shows a **Download** button, and the
  resident gets an email if email is set up. The link only works for that resident, while they're signed in, until it expires.
  The file is then deleted. A new backup replaces the old one.
- **Only full-rights items leave the grid.** An item goes in the file only if the resident can copy, modify and transfer it. An
  object must also have every part, and everything in its contents (objects inside objects too), full rights for them. The page
  lists everything left out, with its folder and the reason.
- **Bring in inventory.** The resident uploads an IAR. It goes into a new folder, `Imported <date>`, so nothing they have changes.
  Anything matching an item taken down in a takedown case, open or closed, is left out, and the case gets a note. A closed case
  stays closed.

A resident named in an open takedown case sees "not available" until the case is closed. People who only held a copy keep their
backups.

## The limits and what each one stops

| Setting | Starts at | Stops |
|---|---|---|
| Largest upload | 500 MB | filling the disk with one file |
| Largest unpacked size | 4 × the largest upload | a small file that unpacks into something huge |
| Most items in one upload | 20,000 | flooding an inventory with junk |
| Largest download | 2 GB | one inventory using all the disk; bigger ones are refused, with a note to ask staff |
| Downloads / uploads per resident per day | 2 / 2 | doing it over and over |
| Backups and uploads at once, whole grid | 1 | jobs queued to slow the grid down |
| Download speed for each download | 2048 KB a second | one download using all of the grid's upload bandwidth |
| A download link lasts | 24 hours | old files left lying around |
| Refuse new jobs below this free disk space | 5 GB | the disk filling up |

The day's count is used when a job starts. It's given back when the grid caused the failure (no region running, a region
stopped, the launcher restarted, the browser went away mid-upload). It isn't given back when the file was bad, or when a
backup came out over the download size limit (each try is a whole save on a region).

Backups and uploads need a running region on this computer: OpenSim does the work there. While a backup runs, that region's
program can't run an OAR job, and the other way round.

## The owner's and admins' powers

On a person's page, **Inventory archives** (`/people/{id}/iar`) is for the owner and admins only. Supervisors can't see it.

- **Save the whole inventory**, whatever the permissions, the resident's switches or the limits. The file is kept on the server
  as evidence, for the owner and admins only. With a case number it holds the account against delete while it's kept.
- **Load an archive** into a named folder, from an upload with no size limit or from a kept save. If the archive matches
  taken-down content, the page lists the matches and asks you to confirm. It doesn't remove them, and the record lists them.
- **Download or delete a kept save.** Deleting needs its own reason. The record stays.

Every step needs a typed reason, and a case number is optional. The record on that page shows each step in full: who, from
which address, when, why, the case, the file with its size and SHA-256, the item count, the folder, the taken-down matches and
the result. The audit log has one line per step (`iar-staff-save`, `iar-staff-load`, `iar-staff-fetched`, `iar-staff-deleted`).

## Where the files are

Everything is under `<data>/iars`, readable only by the launcher: `.incoming` and `.saving` (emptied at start-up), `ready` (residents'
backups, swept every 10 minutes) and `staff` (kept saves and `records.jsonl`, never swept). Files are named by random ids, never by
avatar names.
