# Wifi, Diva and jOpenSim removal log

Date: 2026-11-12. Design: [remove-wifi-diva-design](superpowers/specs/2026-11-12-remove-wifi-diva-design.md).

Everything removed here is still in commit `9f84ef76` and can be restored with `git checkout 9f84ef76 -- <path>`.

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
