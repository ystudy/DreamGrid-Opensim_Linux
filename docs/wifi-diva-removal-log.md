# Wifi, Diva and jOpenSim removal log

Date: 2026-11-12. Design: [remove-wifi-diva-design](superpowers/specs/2026-11-12-remove-wifi-diva-design.md).

Everything removed here is still in commit `9f84ef76` and can be restored with `git checkout 9f84ef76 -- <path>`.
For example: `git checkout 9f84ef76 -- addon-modules/21Wifi`.

## Projects

Folders under `addon-modules/`. File counts are from `git ls-tree -r --name-only 9f84ef76 -- <folder>`.

| Folder | Files | What it was | Why it went |
| --- | --- | --- | --- |
| `00Data` | 44 | Diva.Data, Diva.Data.MySQL, Diva.Data.SQLite: Diva's database layer | Replaced core OpenSim's database provider for Wifi; nothing else used it |
| `00DivaInterfaces` | 10 | Diva.Interfaces: shared interfaces for the Diva add-ons | Only Wifi and the other Diva add-ons used them |
| `01DivaUtils` | 11 | Diva.Utils: helper code for the Diva add-ons | Only Wifi and the other Diva add-ons used it |
| `02AddinExample` | 7 | Diva.AddinExample: sample add-in | Example for the Diva add-on system; unused |
| `1DivaOpenSimServices` | 10 | Diva.OpenSimServices: Diva service glue for OpenSim | Only Wifi used it |
| `20WifiScriptEngine` | 4 | Diva.Wifi.ScriptEngine: template engine for Wifi pages | Only Wifi used it |
| `21Wifi` | 327 | Diva.Wifi: the Wifi web front end for the grid | Replaced by GridGate; put the session id in links, had no CSRF protection, ran inside Robust |
| `ProcessorTest` | 5 | Diva.Wifi.ProcessorTest: tests for Wifi's page processor | Tested Wifi only |
| `WhoGotWhat` | 9 | Diva's sample module | Unauthenticated form appending to a CSV; its password was never checked |
| `jOpenSimSearch` | 3 | jOpenSimSearch.Modules: search module for jOpenSim | Talked to jOpenSim's PHP site, which has been end of life since 2020 |
| `jOpenSimProfile` | 1 | jOpenSim profile module | One file, not built |

## Guard

A checkout built before this change keeps old Diva build output in `bin/`. To stop that output reaching a package:

- `Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs` (`IsRetiredFile`, `IsRetiredDirectory`) names the retired items. Files: names starting with `Diva.`, `Wifi.`, `WhoGotWhat.` or `jOpenSim`. Folders: `DivaUploads`, and names starting with `WifiPages`. Matching ignores case.
- `RuntimeStager` skips them when staging.
- `setup/package.ps1` throws before building if `bin/` holds any of them.

## Files in bin/ and at the top level

Counts are from `git ls-tree -r --name-only 9f84ef76 -- <path>`.

| Path | Files | Why it went |
| --- | --- | --- |
| `WifiPages-Black`, `WifiPages-Modern`, `WifiPages-White` (top level) | 33, 34, 33 | Page templates for Wifi; sources of the copies Wifi's build put in `bin/` |
| `bin/WifiPages-Black`, `bin/WifiPages-Modern`, `bin/WifiPages-White` | 242, 250, 239 | Build output copies of the same templates |
| `bin/WifiPages-Custom` | 1 | Placeholder for custom Wifi templates |
| `bin/DivaUploads` | 1 | Wifi's upload folder |
| `bin/config-include/Proto/Joomla` | 1 | Joomla template for jOpenSim; nothing selects it (`git grep -i Joomla -- Launcher setup tests` is empty) |
| `bin/config-addon-opensim/Wifi.ini.proto`, `AddinExample.ini`, `WhoGotWhat.ini` | 3 | Config for Wifi and the Diva sample add-ons |
| `bin/{de,en,es,fr,pt}/Diva.Wifi.resources.dll` | 5 | Wifi translations |
| `bin/Diva.Wifi.pot` | 1 | Wifi translation template |
| `bin/Diva.Wifi.ProcessorTest.exe.config` | 1 | Config for the removed test program |
| `bin/AddinExample.html`, `bin/data/AddinExample.html`, `bin/AddinExample.ini` | 3 | Pages and config of the sample add-in |
| `bin/WhoGotWhat.html`, `bin/WhoGotWhat.ini`, `bin/WhoGotWhat.csv`, `bin/data/WhoGotWhat.html` | 4 | Page, config and data of WhoGotWhat |
| `bin/config-include/GridCommon-GridServer-JOpensim.ini` | 1 | jOpenSim grid config; nothing includes it |
| `bin/Robust.HG.ini` | 1 | Stale generated copy. Decision: deleted. `git grep -n -i -E "Joomla\|GridServer-JOpensim" -- Launcher setup tests` found nothing, and `git grep -n "Robust.HG.ini\b" -- Launcher setup tests` found only the runtime path (`GridConfigWriter`), `GridController`'s `-inifile` argument and tests that use the staged copy. Task 4 therefore does not edit it; the tracked `bin/Robust.HG.ini.example` and `.proto` stay |

Also changed:

- `bin/data/About this folder.txt`: removed the sentence saying Wifi serves the folder. The rest is still true, so the file stays. Other files in `bin/data` are untouched.
- `.gitignore`: removed the `WifiPages*` entries, `/bin/Diva.Wifi.ProcessorTest` and the two live Wifi config lines. `bin/config-addon-opensim/Wifi.ini` and `bin/config-addon-robust/Wifi.ini` were then no longer ignored (only `bin/Wifi.ini` was, by `bin/*.ini`), so one generic line, `bin/config-addon-*/*.ini`, now covers them. Checked with `git check-ignore -v` in the shared checkout before the edit (Wifi.ini ignored by the removed line 141, `bin/Wifi.ini` by `bin/*.ini`) and in this worktree after it.

Also removed from `.gitleaksignore`: the entry for `bin/Robust.HG.ini:...:922`, dead now that the file is deleted. The `2c0fcab6` history entry stays, and its comment now says the tree no longer has the file.
