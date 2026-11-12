# Remove Wifi, Diva and jOpenSim — design

Date: 2026-11-12. Work list Phase 3 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`). Why Wifi is being removed is explained on the wiki page "Why Wifi was replaced, and what GridGate leaves out".

## Intent

GridGate now does everything from Wifi that the grid uses, so the old Diva Wifi add-on, the other Diva add-ons and the jOpenSim modules leave the tree completely. Two reasons:

- **The first GridGate release waits for this.** No release goes out while Wifi and Diva are in the tree.
- **Folder renames come next.** They should start from a clean tree, so this change only deletes and edits. It moves and renames nothing.

## Facts that shape the design

- **Nobody else has an install** (owner, 2026-11-12). No release has ever been made. The only install is the owner's Debian 12 test VM, plus the owner's own development checkouts.
- **The VM's database needs nothing.** Robust there uses core `OpenSim.Data.MySQL.dll` (`/var/lib/dreamgrid/runtime/opensim/Robust.HG.ini`, line 289), so Wifi's `GridUserStore` migration never ran and there is no `GridUser.TOS` column to move or drop.
- **Diva code runs on the VM today.** Its regions load `Diva.AddinExample` (it writes `Names.csv` in the runtime folder) and `WhoGotWhat`. Their DLLs and INIs are in `/var/lib/dreamgrid/runtime/opensim`.
- **Upgrades already remove old files.** `setup/debian/install.sh` replaces `/opt/dreamgrid` whole. The Windows installer's `[InstallDelete]` clears `{app}\bin`. `RuntimeStager` deletes and re-copies the runtime folder whenever the program changes. So installing a build without Diva removes Diva from an install, and no upgrade code is needed.
- **Developer `bin/` folders are the exception.** `bin/` is OpenSim's build output. A checkout that was built before this change keeps about 20 untracked `Diva.*` DLLs there. The owner's shared checkout has them. Anything that runs or packages from such a `bin/` would still pick them up.

## Goals

Remove from the tree:

1. **The Diva projects**, from `DreamGrid.sln` and from disk: `addon-modules/00Data` (Diva.Data, .MySQL, .SQLite), `00DivaInterfaces`, `01DivaUtils`, `02AddinExample`, `1DivaOpenSimServices`, `20WifiScriptEngine`, `21Wifi`, `ProcessorTest`.
2. **`WhoGotWhat`** (`addon-modules/WhoGotWhat`), from the solution and from disk (owner, 2026-11-12: "remove it too, if we need that we will write something better"). It is Diva Canto's sample region module, a "who got what" logger. Something in-world, such as a giver, posts an avatar, a region and a prim name to `/diva/WhoGotWhat` on the region's HTTP port, and the module appends a dated line to a CSV file. Its INI defines a password (`MachineID=abc123Stupidpassword`), but the post handler reads it and never checks it, so anyone who can reach the region can write to the file. Two tracked INIs switch it on (`Enabled = True`).
3. **jOpenSim**: `addon-modules/jOpenSimSearch` (from the solution) and `addon-modules/jOpenSimProfile` (not in the solution).
4. **Files in `bin/` and at the top level** that belong only to these:
   - `bin/WifiPages-Black`, `-Modern`, `-White`, `-Custom`, and the top-level `WifiPages-Black`, `-Modern`, `-White`;
   - `bin/config-addon-opensim/Wifi.ini.proto`, `AddinExample.ini`, `WhoGotWhat.ini`;
   - `bin/{de,en,es,fr,pt}/Diva.Wifi.resources.dll`, `bin/Diva.Wifi.pot`, `bin/Diva.Wifi.ProcessorTest.exe.config`, `bin/DivaUploads/`;
   - `bin/AddinExample.html`, `bin/WhoGotWhat.html`, `bin/data/AddinExample.html`, `bin/data/WhoGotWhat.html`;
   - `bin/WhoGotWhat.ini`, another copy with `Enabled = True` and the sample password, and `bin/WhoGotWhat.csv`, which holds only a header row;
   - `bin/config-include/GridCommon-GridServer-JOpensim.ini`, and `bin/config-include/Proto/Joomla/`, the jOpenSim/Joomla template; nothing in the launcher selects it.
5. **Lines in the config templates:**
   - `jOpensimURL` (the constant, and the comments about an "Apache Port for Jopensim");
   - the commented `data_services=…/JOpensim/…registersearch.php` line with its hard-coded LAN address;
   - the commented `SearchURL`, `DestinationGuide`, `welcome` and `economy` lines that point at `/JOpensim/`;
   - the "Grid Name for Wifi to show" comments;
   - the "Used by JOpensim" and "needed for JOpensim profile pictures" comments;
   - the `GroupsServerURI` line in `Robust.HG.ini.proto` `[Groups]`, which is built from `jOpensimURL`.

   This covers `bin/Robust.HG.ini.proto`, `bin/config-include/Proto/*/OpenSim.ini` and `bin/OpenSim.ConsoleClient.ini`. In the console client, `user=Wifi Admin` becomes a neutral placeholder.
6. **The `register=` and `password=` defaults in `Robust.HG.ini.proto` become empty.** The launcher writes GridGate's pages into both, but only where this computer runs the staff pages (`AdminRule.HasStaffPages`, `GridConfigWriter.cs` around line 63). Everywhere else the proto's default is what viewers get, and the proto can't express GridGate's address. So the honest default is no link at all, rather than a dead `/wifi/…` one.
7. **Launcher code:**
   - `GridConfigWriter` stops writing `[WifiService] Enabled = false`. `SetSmartStartAndWifi` becomes `SetSmartStart`.
   - `GridConfigWriterTests` changes to match: `SmartStart_disabled_and_Wifi_disabled_in_both` loses its Wifi assert. `Register_no_longer_points_at_wifi` stays as a guard, but is renamed and asserts the value `register` should have, not the absence of `/wifi/`, so the test file doesn't trip the CI check.
   - The test snapshots in `Launcher/DreamGrid.Launcher.Tests/Snapshots/*.OpenSim.ini` are regenerated.
   - In `LegacyCommandApiTests`, the test avatar "Diva Admin" (`divapw`, `DivaId`) gets another name. It's only a name.
   - `RegionSettingsTests`: the comment about jOpenSimSearch is updated.
8. **Smoke test** (`tests/DreamGrid.Smoke/SmokeRun.cs`): the `[WifiService]` stub and its comment go.
9. **Repo files:** the Wifi lines in `.gitignore`. The Wifi wording in comments in `.github/workflows/dreamgrid-ci.yml` and `.gitleaks.toml` becomes "old INI files". The secret-scan rules themselves stay.
10. **`bin/data/About this folder.txt`** says the folder is served by Wifi. It's reworded or deleted. The rest of `bin/data` stays: `avataranimations.xml` is core OpenSim data.

### The leftover guard

So that an old developer `bin/` can't bring Diva back, `RuntimeStager` and `setup/package.ps1` skip one short list of retired file names: `Diva.*`, `Wifi.*`, `WhoGotWhat.*`, `jOpenSim*`, `WifiPages-*`, `DivaUploads`. This replaces today's `Wifi.ini` and `Wifi.bak` entries, and `Gloebit.dll` stays excluded as it is. *My call.* The list lives in one place per file, with a comment saying why.

The package script **fails** if it finds a retired file in `bin/`, rather than skipping it quietly: a package built from a dirty folder should be noticed. The stager **skips** them, so a developer's grid still starts.

### The CI check

A CI step searches all tracked files, case-insensitively, for `wifi`, `diva` and `jopensim`, and fails on any match outside an allow-list. *My call.* The allow-list:

- **`CONTRIBUTORS.txt`**: Diva (Crista Lopes) is credited as an OpenSim core developer, and that credit stays.
- **`OpenSim/`**: upstream OpenSim code, such as the old `diva_groups_*` table names in core migrations and a comment in `SnapshotStore.cs`. It's left as upstream wrote it, so OpenSim updates still cherry-pick cleanly.
- **`docs/superpowers/`**: designs and plans are history and aren't rewritten. That includes the work list and this design.
- **`docs/jopensim-notes.md` and `docs/opensim-search-notes.md`**: reference notes. They gain one line saying the modules were removed.
- **The two leftover-guard files** (`RuntimeStager.cs`, `setup/package.ps1`) and their tests.
- **The CI step itself.**
- **Binary files**: third-party DLLs such as `Mono.Addins.dll` match by accident. The search ignores binary files.

The CI step goes in `.github/workflows/dreamgrid-ci.yml` next to the secret scan, as a quick job.

## Non-goals

- **No renames or moves.** The folder renames are the next change.
- **No upgrade code** in the installers or `install.sh`. They already replace the program folders.
- **No database migration.** There is no `GridUser.TOS` column on the only install. GridGate's Terms acceptances are already in its own file.
- **No changes to upstream OpenSim code** (`OpenSim/`), even where it mentions Diva.
- **No rewriting of history documents** under `docs/superpowers/`.
- **No new features.** The in-world broadcast message and the logins-off switch, found missing from GridGate on 2026-11-12, are separate work.
- **No in-world Search.** It stays empty, as it is today. Bringing it back is on the work list's Later list.

## Anti-goals (a failure even if the tree is clean)

- **The grid stops building, starting or working.** Robust and the regions must start, logins must work, and groups, profiles and estates must keep working.
- **Something core OpenSim needs is deleted.** For example `bin/data/avataranimations.xml`, `Mono.Addins.dll`, `OpenSimSearch`, `Gloebit`, or any non-Diva add-on.
- **Diva's credit for OpenSim is removed** from `CONTRIBUTORS.txt`.
- **A Diva file comes back** from a developer's old `bin/` into a runtime folder or a package.

## How it's checked

1. **Build and test.** `dotnet build DreamGrid.sln` and all launcher unit tests pass. The integration tests and the smoke run pass in CI on Windows, Debian 12 and Debian 13. Run `release.yml` for the branch, since PR CI builds the Linux package only when `setup/` changes, and this change does touch `setup/package.ps1`.
2. **The new CI check passes,** and fails when a test match is added to a non-allowed file.
3. **The leftover guard is tested.**
   - **Stager:** a test like `RuntimeStagerTests` puts a `Diva.Wifi.dll` and a `WhoGotWhat.ini` in the program folder and checks that neither reaches the runtime folder.
   - **Package script:** checked by hand. Run `setup/package.ps1` against a `bin/` holding a retired file and see it fail.
4. **On the VM** (`root@192.168.86.37`), install the branch's package.
   - No `Diva.*`, `WhoGotWhat*` or `Wifi.*` file is left under `/opt/dreamgrid` or `/var/lib/dreamgrid/runtime/opensim`.
   - The region logs no longer show `Plugin Loaded: Diva.AddinExample` or WhoGotWhat.
   - Robust and every region start. `/simstatus/` answers, and the launcher log says "The grid is running."
5. **The owner's check in Firestorm:** log in, edit a profile and see it saved, open a group, change an estate setting.

## Docs to update

- **The work list:** tick Phase 3's items, and mark the upgrade and database items as not needed, with the reason (no installs).
- **`docs/jopensim-notes.md` and `docs/opensim-search-notes.md`:** one line each saying the modules were removed in this change.
- **The wiki,** after this merges: the "What happens to Wifi now" section of "Why Wifi was replaced" (no upgrade or database step was needed), and "GridGate and the OpenSim changes behind it", which still says Wifi is in the tree.

## Left to the builder

- **The order of the deletions,** and whether to delete `bin/Robust.HG.ini`. It's a tracked, stale copy of a generated file: the launcher writes its own into the runtime folder. If nothing reads it, delete it; otherwise edit only the matching lines.
- **What the `[Groups]` section of `Robust.HG.ini.proto` keeps** once the jOpenSim `GroupsServerURI` goes. Robust's core groups service must still load; the smoke run and the VM check prove it.
- **The exact allow-list syntax** for the CI step.
- **The test avatar's new name.**
