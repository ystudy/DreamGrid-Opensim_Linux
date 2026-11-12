# Remove Wifi, Diva and jOpenSim — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete every Wifi, Diva (including WhoGotWhat) and jOpenSim piece from the tree, with a guard so old build output can't bring them back, a CI check so they can't come back in git, and a log of everything removed.

**Architecture:** The work is deletes and small edits only, with no moves or renames. A new `RetiredFiles` helper in Launcher Core tells `RuntimeStager` which file and folder names to skip. `setup/package.ps1` keeps its own copy of the same patterns, and fails rather than skipping. A bash script run by a new CI job searches the tracked files for the retired names, with an allow-list.

**Tech Stack:** .NET 10 / C# (launcher, xUnit tests), PowerShell (package script), bash and GitHub Actions (CI), OpenSim INI templates.

**Spec:** `docs/superpowers/specs/2026-11-12-remove-wifi-diva-design.md`

## Global Constraints

- **Work folder:** `C:\Users\ystud\Desktop\CODE\DreamGrid-strip-wifi`, branch `chore/remove-wifi-diva`. Never work in the shared `DreamGrid-Linux` checkout. Run `git branch --show-current` before every commit.
- **Base commit, for the log and for restores:** `9f84ef76`, `main` when the branch was cut.
- **No renames or moves** of folders or files. Deletes and edits only.
- **Never change `OpenSim/`,** never change anything under `docs/superpowers/` except this plan, and never touch Diva's line in `CONTRIBUTORS.txt`.
- **Keep:** `bin/data/avataranimations.xml` and the rest of `bin/data` (except the two Diva HTML files and the reworded "About" file), `Mono.Addins.dll`, `OpenSimSearch`, `Gloebit`, `OpenSimBirds`, `OpenSimTide`, `dtl-paypal`. `Gloebit.dll` stays excluded from staging and packages, as it is today.
- **Retired names, the same in both guards:**
  - **Files** whose name starts with `Diva.`, `Wifi.`, `WhoGotWhat.` or `jOpenSim` (case-insensitive).
  - **Folders** named `DivaUploads`, or whose name starts with `WifiPages` (case-insensitive).
- **The CI check's pattern:** case-insensitive `wifi|diva|jopensim`, binary files ignored. The allow-list is exactly the one in Task 6.
- **Every task adds its section** to `docs/wifi-diva-removal-log.md` (created in Task 2), saying what went and why.
- **Commits** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A developer's old `bin/` still holds `Diva.*.dll`, `WhoGotWhat.dll` and `de/Diva.Wifi.resources.dll` from earlier builds.** Expect a grid run from that checkout to stage none of them. Covered by `Retired_files_never_reach_the_runtime` in Task 1.
2. **Robust's `[Groups]` section loses the jOpenSim `GroupsServerURI` line.** Expect Robust's core groups service to still load and groups to work in the viewer. Covered by the smoke run and the VM check in Task 7. Task 4 changes nothing else in `[Groups]`.
3. **Profiles after jOpenSimProfile is gone.** Expect regions to still point `[UserProfiles] ProfileServiceURL` at Robust. Covered by `Region_profiles_point_at_robust` in Task 4.
4. **A tracked file that mentions a retired name for a good reason.** Expect the CI check to pass for exactly the allow-listed files and fail for anything else. Covered by the planted-match step in Task 6.
5. **The VM's runtime folder after upgrade.** Expect Mono.Addins to re-register without Diva's add-ins, and no `Plugin Loaded: Diva.AddinExample` or WhoGotWhat in the region logs. Covered by the VM check in Task 7.

---

### Task 1: The leftover guard

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Runtime/RuntimeStager.cs:11-12, 66-72` (exclusion lists and `IsExcluded*`)
- Modify: `setup/package.ps1:71-72` and its `Copy-Bin` function
- Test: `Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs`

**Interfaces:**
- Produces:
  - `public static class RetiredFiles` (namespace `DreamGrid.Launcher.Core.Runtime`), with `public static bool IsRetiredFile(string name)` and `public static bool IsRetiredDirectory(string name)`. Both take a bare file or folder name and compare case-insensitively.
  - `RuntimeStager.ExcludedFiles` becomes `["Gloebit.dll"]`.

- [ ] **Step 1: Change the test fixture and write the failing test**

In the `RuntimeStagerTests` constructor:
- Replace the two `Wifi.ini` / `Wifi.ini.proto` lines with retired items in the program folder:
  - `Diva.Wifi.dll`, `diva.utils.dll` (lower case);
  - `WhoGotWhat.dll` and `WhoGotWhat.ini`, the second inside `config-addon-opensim`;
  - `jOpenSimSearch.Modules.dll`;
  - `de/Diva.Wifi.resources.dll`;
  - the folders `WifiPages-White/` (holding `header.html`) and `DivaUploads/` (holding `.keep`).
- Add two near-miss files that must still be copied: `Divan.dll` and `OpenSimSearch.Modules.dll`.

Remove the two `Wifi.ini` asserts from `First_stage_copies_and_skips_excluded_items` (Gloebit stays asserted). Add:

```csharp
[Fact]
public void Retired_files_never_reach_the_runtime()
{
    Assert.True(new RuntimeStager(_paths).StageIfNeeded());
    var rt = _paths.RuntimeOpenSim;
    foreach (var gone in new[] { "Diva.Wifi.dll", "diva.utils.dll", "WhoGotWhat.dll", "jOpenSimSearch.Modules.dll",
                 Path.Combine("config-addon-opensim", "WhoGotWhat.ini"), Path.Combine("de", "Diva.Wifi.resources.dll") })
        Assert.False(File.Exists(Path.Combine(rt, gone)), gone);
    Assert.False(Directory.Exists(Path.Combine(rt, "WifiPages-White")));
    Assert.False(Directory.Exists(Path.Combine(rt, "DivaUploads")));
    Assert.True(File.Exists(Path.Combine(rt, "Divan.dll")));
    Assert.True(File.Exists(Path.Combine(rt, "OpenSimSearch.Modules.dll")));
}
```

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release -- --filter-class DreamGrid.Launcher.Tests.RuntimeStagerTests`
Expected: `Retired_files_never_reach_the_runtime` FAILS (`Diva.Wifi.dll` is copied).

- [ ] **Step 3: Write `RetiredFiles` and wire it into `RuntimeStager`**

`IsRetiredFile` is true for names starting with `Diva.`, `Wifi.`, `WhoGotWhat.` or `jOpenSim`. `IsRetiredDirectory` is true for `DivaUploads` and for names starting with `WifiPages`. Both compare with `StringComparison.OrdinalIgnoreCase`. Give the class a doc comment: the Wifi, Diva and jOpenSim add-ons were removed (`docs/wifi-diva-removal-log.md`), and a checkout built before then still has their build output in `bin/`. In `RuntimeStager`, `IsExcludedFile` also returns true for `RetiredFiles.IsRetiredFile(name)`, and `IsExcludedDirectory` for `RetiredFiles.IsRetiredDirectory(name)`. Drop the Wifi comment and the two Wifi entries.

- [ ] **Step 4: Run the stager tests and see them pass**

Run the command from Step 2. Expected: every `RuntimeStagerTests` test passes.

- [ ] **Step 5: Make `package.ps1` refuse retired files**

Replace `$excludedFiles = @('Gloebit.dll', 'Wifi.ini', 'Wifi.bak')` and its comment with `$excludedFiles = @('Gloebit.dll')`. Then add the following before `dotnet build`, so a dirty folder fails in seconds rather than after the build:
- `$retiredFilePrefixes = @('Diva.', 'Wifi.', 'WhoGotWhat.', 'jOpenSim')` and `$retiredDirectories = @('DivaUploads')`, plus the `WifiPages` folder prefix, with a comment pointing at `RetiredFiles.cs`.
- A recursive scan of `bin/` that collects every retired file or folder (relative paths).

When the scan finds anything, `throw` a message that lists them and says: "bin/ holds build output from before Wifi and Diva were removed; delete these files and build again."

- [ ] **Step 6: Check the package script by hand**

Run `New-Item bin/Diva.Test.dll -ItemType File; ./setup/package.ps1 -Version 0.0.0-test -Rid linux-x64`. Expected: it stops before building, naming `Diva.Test.dll`. Until Task 3 deletes them, it also names the tracked `WifiPages-*` folders and `Diva.Wifi.resources.dll` files; that's expected. Then `Remove-Item bin/Diva.Test.dll`. If the script needs parameters other than these, read its `param()` block and use the smallest set that reaches the `bin/` step.

- [ ] **Step 7: Commit**

The removal log is created in Task 2, and its "Guard" section describes this task.

```bash
git add Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs Launcher/DreamGrid.Launcher.Core/Runtime/RuntimeStager.cs Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs setup/package.ps1
git commit -m "launcher: staging and packaging never take retired Wifi/Diva/jOpenSim files from bin/"
```

### Task 2: Delete the projects

**Files:**
- Modify: `DreamGrid.sln`
- Delete: `addon-modules/00Data`, `00DivaInterfaces`, `01DivaUtils`, `02AddinExample`, `1DivaOpenSimServices`, `20WifiScriptEngine`, `21Wifi`, `ProcessorTest`, `WhoGotWhat`, `jOpenSimSearch`, `jOpenSimProfile`
- Create: `docs/wifi-diva-removal-log.md`

**Interfaces:** none.

- [ ] **Step 1: Prove nothing else references them**

Run `git grep -n -i -E "Diva\.|21Wifi|WhoGotWhat|jOpenSim" -- '*.csproj' '*.props' '*.targets' '*.xml' ':!addon-modules/00Data' ':!addon-modules/00DivaInterfaces' ':!addon-modules/01DivaUtils' ':!addon-modules/02AddinExample' ':!addon-modules/1DivaOpenSimServices' ':!addon-modules/20WifiScriptEngine' ':!addon-modules/21Wifi' ':!addon-modules/ProcessorTest' ':!addon-modules/WhoGotWhat' ':!addon-modules/jOpenSimSearch' ':!addon-modules/jOpenSimProfile'`.
Expected: no output. If anything matches, stop and report it.

- [ ] **Step 2: Remove the projects from the solution**

Run `dotnet sln DreamGrid.sln remove` with each of these 12 `.csproj` files: `Diva.Data`, `Diva.Data.MySQL`, `Diva.Data.SQLite`, `Diva.Interfaces`, `Diva.Utils`, `Diva.AddinExample`, `Diva.OpenSimServices`, `Diva.Wifi.ScriptEngine`, `Diva.Wifi`, `Diva.Wifi.ProcessorTest`, `WhoGotWhat`, `jOpenSimSearch.Modules`. Then delete by hand the empty solution-folder entries this leaves (`00DivaInterfaces`, `01DivaUtils`, `1DivaOpenSimServices`, `20WifiScriptEngine`, `21Wifi`, `jOpenSimSearch`, and any for `00Data` or `WhoGotWhat`). Delete their `NestedProjects` lines too. `git grep -n -i -E "diva|wifi|jopensim|WhoGotWhat" DreamGrid.sln` must print nothing.

- [ ] **Step 3: Delete the folders**

`git rm -r -q` the eleven folders listed under **Files**.

- [ ] **Step 4: Build**

Run: `dotnet build DreamGrid.sln -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 5: Create the removal log**

Create `docs/wifi-diva-removal-log.md`. It should hold:
- **A short intro:** the date 2026-11-12, a link to the design, and that everything here is in commit `9f84ef76` and can be restored with `git checkout 9f84ef76 -- <path>`.
- **A "Projects" table:** folder, file count (`git ls-tree -r --name-only 9f84ef76 -- <folder> | wc -l`), what it was, and why it went. Take the reasons from the design's Goals 1–3 and from the wiki page.
- **A "Guard" section** describing Task 1.

- [ ] **Step 6: Commit**

```bash
git add -A DreamGrid.sln addon-modules docs/wifi-diva-removal-log.md
git commit -m "remove the Wifi, Diva, WhoGotWhat and jOpenSim projects"
```

### Task 3: Delete their files in `bin/` and at the top level

**Files:**
- Delete:
  - `bin/WifiPages-Black`, `bin/WifiPages-Modern`, `bin/WifiPages-White`, `bin/WifiPages-Custom`;
  - the top-level `WifiPages-Black`, `WifiPages-Modern`, `WifiPages-White`;
  - `bin/config-addon-opensim/Wifi.ini.proto`, `AddinExample.ini`, `WhoGotWhat.ini`;
  - `bin/{de,en,es,fr,pt}/Diva.Wifi.resources.dll`, `bin/Diva.Wifi.pot`, `bin/Diva.Wifi.ProcessorTest.exe.config`, `bin/DivaUploads/`;
  - `bin/AddinExample.html`, `bin/WhoGotWhat.html`, `bin/WhoGotWhat.ini`, `bin/WhoGotWhat.csv`, `bin/data/AddinExample.html`, `bin/data/WhoGotWhat.html`;
  - `bin/config-include/GridCommon-GridServer-JOpensim.ini`, `bin/config-include/Proto/Joomla/`.
- Modify: `bin/data/About this folder.txt`, `.gitignore`
- Modify: `docs/wifi-diva-removal-log.md`

**Interfaces:** none.

- [ ] **Step 1: Check that nothing selects the Joomla template or reads `bin/Robust.HG.ini`**

Run `git grep -n -i -E "Joomla|GridServer-JOpensim" -- Launcher setup tests`. Expected: nothing.

Run `git grep -n "Robust.HG.ini\b" -- Launcher setup tests | grep -v "\.proto"`. Expected: only the runtime path in `GridConfigWriter.cs` (`Path.Combine(rt, "Robust.HG.ini")`).

If both hold, also delete the tracked `bin/Robust.HG.ini`, since it's a stale generated copy. Otherwise keep it and only edit its matching lines in Task 4.

- [ ] **Step 2: Delete the files**

`git rm -r -q` everything listed under **Files → Delete** (and `bin/Robust.HG.ini` if Step 1 allowed it).

- [ ] **Step 3: Reword `bin/data/About this folder.txt`**

Remove the sentence saying the folder is served by Wifi. If nothing in the file is still true, delete the file instead. Keep every other file in `bin/data`.

- [ ] **Step 4: Trim `.gitignore`**

Delete lines 1–4 (`WifiPages/*`, `WifiPages-Custom/*`, `bin/WifiPages/*`, `bin/WifiPages-Custom/*`), line 138 (`/bin/Diva.Wifi.ProcessorTest`) and lines 140–142 (the live Wifi config entries and their comment). Then check, in the shared checkout `C:\Users\ystud\Desktop\CODE\DreamGrid-Linux`, that a leftover live config is still ignored: `git -C ../DreamGrid-Linux check-ignore -v bin/config-addon-opensim/Wifi.ini bin/Wifi.ini`. Expected: each is ignored by a wider rule (`bin/*.ini` or similar). If one is no longer ignored, keep a single generic line that covers it, worded without the retired names (for example `bin/config-addon-*/*.ini`), and note it in the log.

- [ ] **Step 5: Build and run the stager tests**

Run `dotnet build DreamGrid.sln -c Release`. Expected: `0 Error(s)`. Then run the stager test command from Task 1. Expected: all pass.

- [ ] **Step 6: Log and commit**

Add a "Files in bin/ and at the top level" section to the log: each folder with its file count, and each single file, with why it went.

```bash
git add -A bin WifiPages-Black WifiPages-Modern WifiPages-White .gitignore docs/wifi-diva-removal-log.md
git commit -m "remove Wifi, Diva and jOpenSim files from bin/ and the top level"
```

### Task 4: Config templates and the launcher's config writer

**Files:**
- Modify: `bin/Robust.HG.ini.proto` (lines around 47–53, 140, 160, 563, 568, 657, 662, 670, 676, 885, 927)
- Modify: `bin/config-include/Proto/Region/OpenSim.ini`, `Proto/Robust/OpenSim.ini`, `Proto/OsGrid/OpenSim.ini`, `bin/OpenSim.ConsoleClient.ini`
- Modify: `bin/Robust.HG.ini`, only if Task 3 kept it
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs:57, 60, 165, 309-315`
- Modify: `tests/DreamGrid.Smoke/SmokeRun.cs:123-125`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`, `Launcher/DreamGrid.Launcher.Tests/Snapshots/*.OpenSim.ini`

**Interfaces:**
- Produces: `GridConfigWriter.SetSmartStart(IniDocument doc, LauncherSettings s)` replaces `SetSmartStartAndWifi`, and is private.

- [ ] **Step 1: Change the tests first**

In `GridConfigWriterTests`:
- Rename `SmartStart_disabled_and_Wifi_disabled_in_both` to `SmartStart_disabled_in_both`, and delete its `WifiService` assert. An assert naming the section would itself trip Task 6's check. The regenerated snapshots in Step 6 prove the section is gone.
- Rename `Register_no_longer_points_at_wifi` to `Register_and_password_point_at_the_website`. Make it assert, for `Make()`, that `register` ends with `/signup` and `password` equals `WebsiteAddress.ForgotPassword(settings)`. Use the settings object the test built.
- Add `Region_profiles_point_at_robust`: `Write(Make())`, then `Assert.False(string.IsNullOrWhiteSpace(Region().Get("UserProfiles", "ProfileServiceURL")))`.

- [ ] **Step 2: Run them; they pass, as guards for the edits below**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release -- --filter-class DreamGrid.Launcher.Tests.GridConfigWriterTests`
Expected: all pass. These tests pin what must survive this task. The failing check for this task is the snapshot test: it fails after Step 4 until Step 6 regenerates the snapshots.

- [ ] **Step 3: Change `GridConfigWriter`**

- Rename `SetSmartStartAndWifi` to `SetSmartStart` at both call sites, and drop its `WifiService` line.
- Reword the comment at line 60 so it no longer says "not Wifi's".

- [ ] **Step 4: Edit the INI templates**

Take out of the templates:
- `jOpensimURL` and its "Apache Port for Jopensim" comments;
- the "set by Dreamgrid for jOpensim and others" and "needed for JOpensim profile pictures" comments;
- the commented `SearchURL`, `DestinationGuide`, `welcome` and `economy` lines that point at `/JOpensim/`;
- the "For Joomla/jOpensim" comment;
- in `[Groups]` of `Robust.HG.ini.proto`, only the `GroupsServerURI = "${Const|jOpensimURL}…"` line;
- in the region and Robust `OpenSim.ini` protos, the "Grid Name for Wifi to show" comments, the `jOpensimURL=` lines, the commented `data_services=http://192.168.2.100…` line and the "Used by JOpensim, from Groups" comment.

If a removed comment headed a setting that stays, rewrite the comment to describe that setting.

In `Robust.HG.ini.proto`, set `register=` and `password=` to empty values. In `OpenSim.ConsoleClient.ini`, change `user=Wifi Admin` to `user=Console Admin`. If Task 3 kept `bin/Robust.HG.ini`, make the same edits there, and change `ConsoleUser=Wifi Admin` to `ConsoleUser=Console Admin`.

Before each removal, record the exact old line in the log.

- [ ] **Step 5: Drop the smoke run's Wifi stub**

In `tests/DreamGrid.Smoke/SmokeRun.cs`, delete the `[WifiService]` section and its two comment lines from the INI text it writes.

- [ ] **Step 6: Regenerate the snapshots and review the diff**

Run: `$env:DG_CAPTURE_SNAPSHOTS='1'; dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release -- --filter-method "*Single_machine_plans_write_what_they_did_before*"; Remove-Item Env:DG_CAPTURE_SNAPSHOTS`

Then run `git diff --stat Launcher/DreamGrid.Launcher.Tests/Snapshots`. Expected: only the `OpenSim.ini` snapshots change, and only by the lines removed in Step 4 and the `[WifiService]` section. Any other difference is a mistake: stop and fix it.

- [ ] **Step 7: Run all launcher tests**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`
Expected: all pass, with the platform skips the suite already has.

- [ ] **Step 8: Log and commit**

Add a "Config lines" section to the log: file, old line, and why. Add a "Launcher code" section covering `SetSmartStart`, the tests and the smoke stub.

```bash
git add -A bin Launcher tests docs/wifi-diva-removal-log.md
git commit -m "config: drop Wifi and jOpenSim lines; the launcher stops writing [WifiService]"
```

### Task 5: The remaining mentions

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Tests/LegacyCommandApiTests.cs` (lines 26, 105, 187-188, 515, 520, 558, 561-566, 631, 635, 653)
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/RegionSettingsTests.cs:125`
- Modify: `.github/workflows/dreamgrid-ci.yml:19-20`, `.gitleaks.toml:18`
- Modify: `docs/jopensim-notes.md`, `docs/opensim-search-notes.md`, `docs/wifi-diva-removal-log.md`

**Interfaces:** none.

- [ ] **Step 1: Rename the test avatar**

In `LegacyCommandApiTests`, "Diva Admin" becomes "Dana Admin", `divapw` becomes `danapw`, `DivaId` becomes `DanaId`, and the local `diva` becomes `dana`. Keep the Guid value and the level 100.

- [ ] **Step 2: Reword the comments**

- **`RegionSettingsTests.cs:125`:** drop the clause about jOpenSimSearch, and keep what the comment says about the restart.
- **`dreamgrid-ci.yml:19-20`:** "the Wifi secrets in it" becomes "the old INI secrets in it". Keep the link to the plan.
- **`.gitleaks.toml:18`:** "passwords Wifi and OpenSim keep in INI files" becomes "passwords OpenSim's INI files used to hold". Leave every rule as it is.

- [ ] **Step 3: Add a line to each notes file**

`docs/jopensim-notes.md` and `docs/opensim-search-notes.md` each get one line, at the place that says the modules are "removed in Phase 3": "Removed on 2026-11-12; see `docs/wifi-diva-removal-log.md`."

- [ ] **Step 4: Run the touched tests**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release -- --filter-class DreamGrid.Launcher.Tests.LegacyCommandApiTests`
Expected: all pass.

- [ ] **Step 5: Log and commit**

```bash
git add -A Launcher .github/workflows/dreamgrid-ci.yml .gitleaks.toml docs
git commit -m "tests and comments: no more Wifi, Diva or jOpenSim names"
```

### Task 6: The CI check

**Files:**
- Create: `.github/scripts/check-retired-names.sh`
- Modify: `.github/workflows/dreamgrid-ci.yml` (new job after `secret-scan`)
- Modify: `docs/wifi-diva-removal-log.md`

**Interfaces:**
- Produces: `.github/scripts/check-retired-names.sh`. It exits 0 when clean. Otherwise it prints `path:line:text` for each match and exits 1.

- [ ] **Step 1: Write the script**

It runs `git grep -n -I -i -E 'wifi|diva|jopensim' -- .` with these exclusions, and nothing else:

```
':!CONTRIBUTORS.txt' ':!OpenSim/' ':!docs/superpowers/' ':!docs/jopensim-notes.md' ':!docs/opensim-search-notes.md'
':!docs/wifi-diva-removal-log.md' ':!Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs'
':!Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs' ':!setup/package.ps1' ':!.github/scripts/check-retired-names.sh'
```

Exit codes: if git grep finds matches (exit 0), print them with a heading saying the names were removed on 2026-11-12, see the log, and exit 1. If it finds none (exit 1), exit 0. Any other git grep exit code is passed through. `set -euo pipefail`, and `chmod +x` in git with `git update-index --chmod=+x`.

- [ ] **Step 2: Run it, and see it fail on a planted match**

Run (Git Bash): `bash .github/scripts/check-retired-names.sh; echo rc=$?`. Expected: no matches, `rc=0`. If it lists anything, fix that file in this task, or stop if the file belongs to upstream OpenSim.

Then: `echo "wifi" >> README.md && bash .github/scripts/check-retired-names.sh; echo rc=$?; git checkout -- README.md`. Expected: it prints `README.md:<n>:wifi` and `rc=1`.

- [ ] **Step 3: Add the CI job**

In `dreamgrid-ci.yml`, after `secret-scan`, add a job `retired-names`: `runs-on: ubuntu-latest`, `timeout-minutes: 5`, steps `actions/checkout@v4` and `run: bash .github/scripts/check-retired-names.sh`. Its comment says what it guards and points at the log, without writing the retired names (or the workflow file would match).

- [ ] **Step 4: Log and commit**

```bash
git add .github docs/wifi-diva-removal-log.md
git commit -m "ci: fail when a removed Wifi, Diva or jOpenSim name comes back"
```

### Task 7: Docs, then prove it on CI and on the VM

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (Phase 3), `docs/wifi-diva-removal-log.md`

**Interfaces:** none.

- [ ] **Step 1: Tick Phase 3 in the work list**

- Tick each removal item.
- Mark the upgrade item ("the Windows installer and the `.deb` delete old Wifi files") and the database items as "Not needed (2026-11-12): no install other than the owner's VM exists. The installers already replace the program folders, and the VM's Robust never used Diva's database provider, so there is no `GridUser.TOS` column."
- Point to the log.

- [ ] **Step 2: Full local test run**

Run: `dotnet build DreamGrid.sln -c Release` and then `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`. Expected: `0 Error(s)`, and all tests pass.

- [ ] **Step 3: Push and open the PR**

Push `chore/remove-wifi-diva` and open a PR to `main`. The body summarises the log, links the design and this plan, and lists the VM and Firestorm checks. End it with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Bind the PR with the app's PR tools. Wait for every check, `retired-names` included. Don't merge with `--auto`.

- [ ] **Step 4: Build the Linux package and install it on the VM**

Run `gh workflow run release.yml --ref chore/remove-wifi-diva`. When it passes, `gh run download <id> -n package-linux`, copy the tarball to `root@192.168.86.37` (key `~/.ssh/dreamgrid_testvm`, `-o IdentitiesOnly=yes`), and run `bash dreamgrid/setup/debian/install.sh --from <tarball>`. Check first that no region is frozen (`/root/bench/region_freeze.py`).

- [ ] **Step 5: Check the VM**

On the VM:
- **No files left:** `find /opt/dreamgrid /var/lib/dreamgrid/runtime/opensim \( -iname 'Diva.*' -o -iname 'Wifi.*' -o -iname 'WhoGotWhat*' -o -iname 'jOpenSim*' -o -iname 'WifiPages*' \)` prints nothing.
- **The grid starts:** after the restart, `/var/lib/dreamgrid/state/logs/launcher.log` says "The grid is running.", and `curl -s http://127.0.0.1:8004/simstatus/` answers `OK`.
- **No Diva add-ins load:** `grep -h -i -E "Diva|WhoGotWhat|jOpenSim" /var/lib/dreamgrid/state/logs/region-*.log /var/lib/dreamgrid/state/logs/robust.log` shows no line written after the restart time.
- **Groups still load:** `robust.log` has no error naming `[Groups]`.

- [ ] **Step 6: Finish the log, and hand the in-world checks to the owner**

Add a "Checked" section to the log: the CI run, the release run, the VM results and the commit installed. Commit, then push. The owner's check in Firestorm: log in, edit a profile and see it saved, open a group, and change an estate setting.

```bash
git add docs
git commit -m "docs: Phase 3 done; removal log checked on CI and the VM"
git push
```
