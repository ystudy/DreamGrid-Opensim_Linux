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

A checkout built before this change keeps old Diva build output in `bin/`. To stop that output reaching the staged runtime folder or a package:

- `Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs` (`IsRetiredFile`, `IsRetiredDirectory`) names the retired items. Files: names starting with `Diva.`, `Wifi.`, `WhoGotWhat.` or `jOpenSim`. Folders: `DivaUploads`, and names starting with `WifiPages`. Matching ignores case.
- `RuntimeStager` skips them when staging.
- `setup/package.ps1` throws before building if `bin/` holds any of them. Its scan skips the folders the package leaves out anyway (`assetcache`, `addin-db-*` and the rest of `Copy-Bin`'s list), so leftovers there do not stop a build.

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
| `bin/Names.csv`, `bin/CsvHelper.dll` | 2 | `Names.csv` was AddinExample's data file (header only); `CsvHelper.dll` was referenced only by AddinExample and WhoGotWhat (`git grep -i csvhelper` finds nothing else). CsvHelper is not in the guard: it is a generic third-party name |
| `bin/Robust.HG.ini` | 1 | Stale generated copy. Decision: deleted. `git grep -n -i -E "Joomla\|GridServer-JOpensim" -- Launcher setup tests` found nothing, and `git grep -n "Robust.HG.ini\b" -- Launcher setup tests` found only the runtime path (`GridConfigWriter`), `GridController`'s `-inifile` argument and tests that use the staged copy. Task 4 therefore does not edit it; the tracked `bin/Robust.HG.ini.example` and `.proto` stay |

Also changed:

- `bin/data/About this folder.txt`: rewritten in two sentences. It said which file types Wifi's file server handled, and nothing serves the folder over HTTP now; it says what the folder holds and that nothing serves it. Other files in `bin/data` are untouched.
- `.gitignore`: removed the `WifiPages*` entries, `/bin/Diva.Wifi.ProcessorTest` and the two live Wifi config lines. `bin/config-addon-opensim/Wifi.ini` and `bin/config-addon-robust/Wifi.ini` were then no longer ignored (only `bin/Wifi.ini` was, by `bin/*.ini`), so one generic line, `bin/config-addon-*/*.ini`, now covers them. Checked with `git check-ignore -v` in the shared checkout before the edit (Wifi.ini ignored by the removed line 141, `bin/Wifi.ini` by `bin/*.ini`) and in this worktree after it.

Also removed from `.gitleaksignore`: the entry for `bin/Robust.HG.ini:...:922`, dead now that the file is deleted. The `2c0fcab6` history entry stays, and its comment now says the tree no longer has the file.

## Config lines

Old lines, verbatim. `Robust.HG.ini.proto` is `bin/Robust.HG.ini.proto`; the three `OpenSim.ini` files are under `bin/config-include/Proto/`.

| File | Old line | Why |
| --- | --- | --- |
| `Robust.HG.ini.proto` | `;Apache Port for Jopensim and others, ` | Reworded to `;Apache port`; `ApachePort` itself stays (the region `OpenSim.ini` uses it for `MuteListURL`) |
| `Robust.HG.ini.proto` | `jOpensimURL= ` (and the lone `;` above it) | jOpenSim's URL constant; nothing else reads it once `[Groups]` stops |
| `Robust.HG.ini.proto` | `; set by Dreamgrid for jOpensim and others` | Reworded to `; set by Dreamgrid`; heads `UserProfilesServiceConnector`, which stays |
| `Robust.HG.ini.proto` | `;; needed for JOpensim profile pictures` | Reworded to `;; Serves textures to viewers over HTTP`; heads `GetTextureConnector`, which stays |
| `Robust.HG.ini.proto` | `;SearchURL = "${Const\|BaseURL}:${Const\|PublicPort}/JOpensim/index.php?option=com_opensim&view=inworldsearch&task=viewersearch&tmpl=component&"` | Commented jOpenSim search address |
| `Robust.HG.ini.proto` | `;DestinationGuide = "${Const\|BaseURL}:${Const\|PublicPort}/JOpensim//index.php?option=com_opensim&view=guide&tmpl=component"` | Commented jOpenSim guide address |
| `Robust.HG.ini.proto` | `;welcome = ${Const\|BaseURL}:${Const\|PublicPort}/JOpensim/index.php?option=com_opensim` | Commented jOpenSim welcome page |
| `Robust.HG.ini.proto` | `;economy = ${Const\|BaseURL}:${Const\|PublicPort}/JOpensim/components/com_opensim/` | Commented jOpenSim economy address |
| `Robust.HG.ini.proto` | `register=${Const\|BaseURL}:${Const\|PublicPort}/wifi/user/account/` | Now `register=`; the launcher sets the website's sign-up page when the grid has staff pages |
| `Robust.HG.ini.proto` | `password=${Const\|BaseURL}:${Const\|PublicPort}/wifi/forgotpassword` | Now `password=`; the launcher sets the website's forgot-password page when the grid has staff pages |
| `Robust.HG.ini.proto` | `GroupsServerURI = "${Const\|jOpensimURL}/index.php?option=com_opensim&view=interface"` | Only this line of `[Groups]`; it was built from the removed constant, and Robust loads the other Groups settings as before |
| `Robust.HG.ini.proto` | `;; For Joomla/jOpensim` | Comment above `[CapsService]`; nothing to describe |
| `Region/OpenSim.ini`, `Robust/OpenSim.ini` | `;# Grid Name for Wifi to show` | Comment above `GridName` |
| `Region/OpenSim.ini`, `Robust/OpenSim.ini` | `jOpensimURL=http://${Const\|BaseHostname}:${Const\|ApachePort}/JOpensim/index.php?option=com_opensim` (with its `; Automatically changed by Dreamworld - do not change this` heading and the `;` after it) | jOpenSim's URL constant |
| `Region/OpenSim.ini`, `Robust/OpenSim.ini` | `; data_services=http://192.168.2.100:80/JOpensim/components/com_opensim/registersearch.php` | Commented search registration with a hard-coded LAN address |
| `Region/OpenSim.ini`, `Robust/OpenSim.ini` | `; Used by JOpensim, from Groups` | Reworded to `; Keys for the XmlRpc groups service`; heads `XmlRpcServiceReadKey` and `XmlRpcServiceWriteKey`, which stay |
| `OsGrid/OpenSim.ini` | `;# Grid Name for Wifi to show` | Comment above `GridName` |
| `bin/OpenSim.ConsoleClient.ini` | `user=Wifi Admin` | Now `user=Console Admin` |

`bin/Robust.HG.ini` is gone (Task 3), so it has no edits here.

## Launcher code

- `GridConfigWriter.SetSmartStartAndWifi` is now `SetSmartStart` (private; both call sites changed) and no longer writes `[WifiService]`. The "not Wifi's" remark in the forgot-password comment is gone.
- `GridConfigWriterTests`: `SmartStart_disabled_and_Wifi_disabled_in_both` is `SmartStart_disabled_in_both`, without the `[WifiService]` assert. `Register_no_longer_points_at_wifi` is `Register_and_password_point_at_the_website`: `register` ends with `/signup` and `password` equals `WebsiteAddress.ForgotPassword`. New `Region_profiles_point_at_robust` checks `[UserProfiles] ProfileServiceURL` is not blank.
- Snapshots `{grid-asset,local,simple-public}.OpenSim.ini` regenerated (`DG_CAPTURE_SNAPSHOTS=1`, test class `GridConfigWriterTests`). Per file, the diff is exactly: the `GridName` comment, the `jOpensimURL` block, the commented `data_services` line, the reworded groups comment and the `[WifiService]` section. Nothing else changed.
- `tests/DreamGrid.Smoke/SmokeRun.cs`: the `[WifiService] Enabled = false` stub and its two comment lines are gone from the INI text the smoke run writes. The sandbox copy of `bin/` now skips the retired files and folders too (`RetiredFiles.cs` is linked into the smoke project), so a stale checkout cannot boot old Diva output.
- `GridConfigWriterTests.Region_profiles_point_at_robust` also asserts that the profile URL ends with `:${Const|PublicPort}` (the template keeps the constant) and that the region file's `[Const] PublicPort` equals the settings' Robust public port.

## Names and comments

- `LegacyCommandApiTests`: the test avatar "Diva Admin" is now "Dana Admin" (password `danapw`, field `DanaId`, local `dana`). The Guid and level 100 are unchanged. No other test file used these names.
- `RegionSettingsTests`: the comment about OpenSim start-up errors no longer mentions jOpenSimSearch, which is gone; it now names only the PID file error.
- `.github/workflows/dreamgrid-ci.yml`: "the Wifi secrets in it" is now "the old INI secrets in it".
- `.gitleaks.toml`: the comment now says "passwords OpenSim's INI files used to hold". No rule, regex or allow-list entry changed.
- `docs/jopensim-notes.md`, `docs/opensim-search-notes.md`: each has one added line saying the modules were removed on 2026-11-12. These notes stay as reference.

## CI check

`.github/scripts/check-retired-names.sh` runs in the `retired-names` job of `dreamgrid-ci.yml` (right after `secret-scan`). It has two passes. The content pass runs `git grep -n -I -i -E 'wifi|diva|jopensim'` over the tracked files, ignoring binary files, and prints `path:line:text` for each match. The path pass greps `git ls-files` for `wifi|diva|jopensim|whogotwhat` (case-insensitive), so a retired name in a file name is caught too. Both passes use the allow-list below (for the path pass the entries are matched as path prefixes). The script prints each pass's matches under its own heading and exits 1 if either finds anything, 0 when both are clean; any other `git grep` error code is passed through.

Allow-list (the only files and folders allowed to mention the names):

| Entry | Why |
|---|---|
| `CONTRIBUTORS.txt` | Credits Diva (Crista Lopes) as an OpenSim core developer; the line stays |
| `OpenSim/` | Upstream OpenSim source, not ours to edit |
| `docs/superpowers/` | Plans and specs that describe this removal |
| `docs/jopensim-notes.md`, `docs/opensim-search-notes.md` | Reference notes, kept on purpose |
| `docs/wifi-diva-removal-log.md` | This log |
| `Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs` | The list of retired files the stager skips; the full restage clears the runtime folder |
| `Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs` | Tests that list is applied |
| `setup/package.ps1` | The packaging guard that fails when a retired file or folder reaches a package |
| `.github/scripts/check-retired-names.sh` | The script itself holds the pattern |

Run it locally from any folder in the repo (Git Bash on Windows): `bash .github/scripts/check-retired-names.sh; echo rc=$?`. The workflow file has no match because its comment does not spell the names.

## Checked

Commit checked: `cfd5d9b3` (branch `chore/remove-wifi-diva`).

- **CI (PR 74):** `retired-names`, `secret-scan`, `windows`, `debian (12)` and `debian (13)` passed. When this was written, the `launcher-debian` and `launcher-windows` jobs were still running; read the PR for their result.
- **Release run 43330012867:** passed, including the install smoke tests on Debian 12 and 13 and Windows. Its `package-linux` artifact is the package installed below.
- **Debian 12 test VM (2026-11-12):** the package (`0.0.0-dev+cfd5d9b`, upgraded from `0.0.0-dev+bc15d3f`) was installed with `install.sh --from`. No region was frozen first. The service restarted at 15:49:36 local time (23:49:36Z).
  - No files left: the `find` for `Diva.*`, `Wifi.*`, `WhoGotWhat*`, `jOpenSim*` and `WifiPages*` under `/opt/dreamgrid` and `/var/lib/dreamgrid/runtime/opensim` printed nothing. `Names.csv` and `CsvHelper.dll` are absent from both folders.
  - The grid starts: `launcher.log` says "The grid is running." at 23:49:50Z, `/simstatus/` on 8004 answers `OK`, and the website's `/welcome` answers 200.
  - No Diva add-ins load: no line written after the restart (Robust and both regions) names Diva, WhoGotWhat, jOpenSim or Wifi. The "Plugin Loaded" lines after the restart list only OpenSim plugins, `OpenSim.Groups` and `OpenSimSearch` among them.
  - Groups still load: after the restart `robust.log` has no `[ERROR]` line, and the Groups and HGGroups connectors loaded.
  - Other lines after the restart, none caused by this change: both regions note an old pid file (the restart), and Test logs scripts whose assets are missing.
- **Not checked here (the owner's, in Firestorm):** log in, edit a profile and see it saved, open a group, and change an estate setting.
