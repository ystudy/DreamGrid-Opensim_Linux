# Plan 1: .NET 10 + SDK-style build Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The OpenSim fork builds from checked-in SDK-style projects on .NET 10 and boots a working region on both Windows and Debian, proven by an automated smoke test that runs in CI.

**Architecture:**
- **Projects:** Prebuild already emits SDK-style `.csproj` files. We generate them once, check them in, and move shared settings (`net10.0`) into `Directory.Build.props`. `prebuild.xml` stays as the reference for mirroring Fred's project changes, but nothing runs it.
- **Build outputs:** our own DLLs leave git; only third-party binaries stay tracked in `bin/`.
- **Runtime gaps:** two .NET 10 gaps are closed and verified, `BinaryFormatter` (compatibility package) and `System.Drawing` on Linux (existing workaround, tested).
- **Smoke test:** a cross-platform C# harness boots a standalone region from a copy of `bin/` and checks the risky paths.

**Tech Stack:**
- .NET 10 SDK (10.0.301 verified locally).
- MSBuild `Directory.Build.props` / `Directory.Build.targets`.
- NuGet `System.Runtime.Serialization.Formatters` 10.0.13.
- xUnit v3 (`xunit.v3` 4.0.2) for new tests.
- GitHub Actions with `windows-latest` plus the `mcr.microsoft.com/dotnet/sdk:10.0` container (Debian 12) for Linux.

**Spec:** `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md`, §4.4, §4.6, §4.9, §7, §8 and §9. This is plan 1 of 3. Plan 2 is the launcher and web site; plan 3 is the installers and release pipeline.

**Facts established while writing this plan** (verified 2026-10-24 on this repo):
- `MSYS_NO_PATHCONV=1 dotnet bin/prebuild.dll /target vs2022 /targetframework net8_0 /excludedir = "obj | bin" /file prebuild.xml` generates SDK-style projects (`<Project Sdk="Microsoft.NET.Sdk">`) for every Prebuild project, writing each to `<path>/<name>.csproj` and `OpenSim.sln` at the root. In Git Bash `MSYS_NO_PATHCONV=1` is required, or `/target` is mangled into a path.
- With every project's `TargetFramework` switched to `net10.0`, `dotnet build -c Release OpenSim.sln --no-incremental` gives **0 errors, 28 warnings** in ~9 s. The warnings are CA2022, CS9193, MSB3243/MSB3245 (stale `System`/`System.Core` references) and SYSLIB0057.
- Seven add-on projects are hand-written and already tracked, all `net8.0`:
  - `addon-modules/00Data/MySQL/Diva.Data.MySQL.csproj`
  - `addon-modules/Gloebit/GloebitMoneyModule/Gloebit.csproj`
  - `addon-modules/OpenSimSearch/Modules/OpenSimSearch.Modules.csproj`
  - `addon-modules/OpenSimTide/Module/OpenSimTide.Module.csproj`
  - `addon-modules/ProcessorTest/Diva.Wifi.ProcessorTest.csproj`
  - `addon-modules/WhoGotWhat/WhoGotWhat.csproj`
  - `addon-modules/jOpenSimSearch/Modules/jOpenSimSearch.Modules.csproj`
- **`BinaryFormatter` compiles but throws `PlatformNotSupportedException` at run time on .NET 9+.** The callers swallow the exception and log it, so failure is silent:
  - `OpenSim/Framework/Util.cs:2551-2580` (`SerializeToFile` / `DeserializeFromFile`),
  - `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs:529,1016` (asset disk cache),
  - `OpenSim/Region/Framework/Scenes/KeyframeMotion.cs:317,839`,
  - `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1695-1708` (script state).
- **The build writes our DLLs into `bin/`, where 89 of them are tracked by git** (force-added despite `bin/*.dll` in `.gitignore`). Every build dirties the tree, and every merge of Fred's commits that rebuild them conflicts.
- `runprebuild.bat`/`.sh` also copy `bin/System.Drawing.Common.dll.win` or `.linux` over `bin/System.Drawing.Common.dll`. That OS-specific step must survive Prebuild's removal.
- The legacy OpenSim test projects use NUnit 2.5.9 (`bin/nunit.framework.dll`), which `dotnet test` cannot run. They stay in the solution as compile-only and are not part of this plan's verification.

## Global Constraints

- Target framework: `net10.0` for every project; nothing may still say `net8.0`.
- Supported platforms: Debian 12/13 amd64 and Windows 10/11 x64.
- Do not move, rename or restructure anything under `OpenSim/`. Edits to existing OpenSim source files only where a task names them.
- All new code goes in new top-level folders. For this plan that is `tests/` and `.github/workflows/dreamgrid-ci.yml`.
- New dependencies must be BSD, MIT or Apache-2.0. No ImageSharp.
- Do not touch the SmartStart patch code (`OpenSim/Framework/SmartStartAltClient.cs`, `OpenSim/Region/Application/SmartStart.cs`, `OpenSim/SmartStart/SmartStart.cs`).
- Keep `prebuild.xml` and `Prebuild/` in the tree, unused, as the reference when mirroring Fred's project changes.
- Build command after this plan: `dotnet build -c Release DreamGrid.sln`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

Inputs and conditions the spec implies that are most likely to hurt a real user. Each has a test in the owning task.

1. **Fresh clone with no build outputs in git:** `dotnet build` from a clean clone must produce a bootable `bin/` (Task 2, clean-clone build step).
2. **Asset disk cache failing silently on .NET 10:** after a boot, `assetcache/` must contain files, and the log must have no `PlatformNotSupportedException` (Task 3 unit tests; Task 4 smoke check `C4`).
3. **Install path containing spaces** (Windows `Program Files`, `/opt/dream grid`): the smoke harness always runs from a directory whose name contains a space (Task 4).
4. **Second boot of the same data directory:** restarting must reuse the estate and database without blocking on a console prompt (Task 4 smoke check `C6`).
5. **Non-English locale on Linux** (decimal comma, `de_DE.UTF-8`): the region must still boot and parse its INI numbers (Task 5 CI runs the smoke harness with `--culture de_DE.UTF-8` on Linux).

---

### Task 1: Check in SDK-style projects on .NET 10 and retire Prebuild

**Files:**
- Create: `Directory.Build.props`, `Directory.Build.targets`, `DreamGrid.sln`, plus one tracked `.csproj` per Prebuild project. That's 85 generated files, at the paths Prebuild writes (e.g. `OpenSim/Framework/OpenSim.Framework.csproj`).
- Modify:
  - `.gitignore`: remove the `*.csproj` and `OpenSim.sln` lines.
  - the 7 tracked add-on `.csproj` files listed above,
  - `BUILDING.md`, `README.md` (build sections only),
  - `compile.bat`, `compile.sh`.
- Delete: `runprebuild.bat`, `runprebuild.sh`, `SetProjectIcons.ps1`, `Makefile` (NAnt, dead), and `OpenSim.sln` (tracked; replaced by `DreamGrid.sln`).

**Interfaces:**
- Produces:
  - `DreamGrid.sln` at the repo root, containing every OpenSim, add-on and legacy test project. Later tasks add `tests/*` projects to it.
  - `Directory.Build.props`, which sets `<TargetFramework>net10.0</TargetFramework>` for all projects. Individual projects do not set it.
  - `Directory.Build.targets`, with a target named `SelectSystemDrawing`. It runs before `Build` in `OpenSim.Framework.csproj` only. It copies `$(MSBuildThisFileDirectory)bin/System.Drawing.Common.dll.win` (Windows) or `bin/System.Drawing.Common.dll.linux` (otherwise) to `bin/System.Drawing.Common.dll`, chosen by `$([MSBuild]::IsOSPlatform('Windows'))`, with `SkipUnchangedFiles="true"`.

- [ ] **Step 1: Generate the projects with Prebuild one last time**

Run (Git Bash, repo root):
```bash
cp bin/System.Drawing.Common.dll.win bin/System.Drawing.Common.dll
MSYS_NO_PATHCONV=1 dotnet bin/prebuild.dll /target vs2022 /targetframework net8_0 /excludedir = "obj | bin" /file prebuild.xml
powershell -NoProfile -ExecutionPolicy Bypass -File SetProjectIcons.ps1
```
Expected:
- the output ends with `...Creating project: WhoGotWhat`,
- `git status --short --ignored | grep -c csproj` prints 85 or more.

`SetProjectIcons.ps1` writes the application icons into the generated projects, so they are captured before that script is deleted.

- [ ] **Step 2: Move the target framework into `Directory.Build.props`**

Create `Directory.Build.props` with one `PropertyGroup` setting `TargetFramework` to `net10.0` and `LangVersion` to `latest`. Then delete the `<TargetFramework>…</TargetFramework>` line from every generated `.csproj`, and from the 7 tracked add-on `.csproj` files:
```bash
git ls-files -o -i --exclude-standard -- '*.csproj' | xargs sed -i '/<TargetFramework>net8.0<\/TargetFramework>/d'
git ls-files '*.csproj' | xargs sed -i '/<TargetFramework>net8.0<\/TargetFramework>/d'
```

- [ ] **Step 3: Add `Directory.Build.targets` with `SelectSystemDrawing`**

Implement the target as described under Interfaces. Condition it on `'$(MSBuildProjectName)' == 'OpenSim.Framework'` so it runs once per build. `OpenSim.Framework` is the first project that references `System.Drawing.Common`.

In the same file, add a `PropertyGroup` with `<NoWarn>$(NoWarn);CA2022;CS9193;MSB3243;MSB3245;SYSLIB0057</NoWarn>`. These are exactly the warnings the .NET 10 trial build produced; none is a behavior change. This has to go in `.targets`, not `.props`: every generated project sets its own `<NoWarn>` in its configuration `PropertyGroup`, which would overwrite a value from `.props`. `.targets` is evaluated after the project body, so it appends.

- [ ] **Step 4: Create `DreamGrid.sln` and stop ignoring projects**

```bash
dotnet new sln --name DreamGrid --format sln
dotnet sln DreamGrid.sln add $(git ls-files -o -i --exclude-standard -- '*.csproj') $(git ls-files '*.csproj')
git rm --cached OpenSim.sln && rm OpenSim.sln
```
Remove the `*.csproj` and `OpenSim.sln` lines from `.gitignore`.

- [ ] **Step 5: Build and verify every project targets .NET 10**

Run:
```bash
dotnet build -c Release DreamGrid.sln --no-incremental -nologo -v q
grep -rl --include=*.csproj 'net8.0' . ; echo "net8 refs: $?"
grep -h '"tfm"' bin/OpenSim.runtimeconfig.json bin/Robust.runtimeconfig.json
```
Expected:
- `Build succeeded`, with `0 Error(s)` and `0 Warning(s)`,
- `net8 refs: 1` (grep found nothing),
- both runtimeconfig files show `"tfm": "net10.0"`.

- [ ] **Step 6: Delete the Prebuild runners and update the docs**

Delete `runprebuild.bat`, `runprebuild.sh`, `SetProjectIcons.ps1` and `Makefile`. Change `compile.bat` and `compile.sh` to the single line `dotnet build -c Release DreamGrid.sln`.

In `BUILDING.md` and `README.md`, replace the Prebuild steps and ".NET 8" with:
- .NET 10 SDK,
- `dotnet build -c Release DreamGrid.sln`,
- `libgdiplus` on Debian,
- one paragraph: "`prebuild.xml` is kept only as a reference when merging from Outworldz/DreamGrid-Opensim; see `MERGING.md`." (`MERGING.md` is written in Task 2.)

- [ ] **Step 7: Commit**

```bash
git add -A Directory.Build.props Directory.Build.targets DreamGrid.sln .gitignore BUILDING.md README.md compile.bat compile.sh
git add $(git ls-files -o --exclude-standard -- '*.csproj') $(git ls-files -m -- '*.csproj')
git add -u
git commit -m "Build: SDK-style projects on .NET 10, Prebuild retired"
```
`git add -u` stages the deletions. Do not stage anything under `bin/`; Task 2 handles `bin/`.

---

### Task 2: Stop tracking our build outputs; document Fred merges

**Files:**
- Modify: `.gitignore` (add `bin/*.runtimeconfig.json`, `bin/*.exe`, and `bin/System.Drawing.Common.dll`; the `.exe` line already exists, so check)
- Create: `MERGING.md`
- Untrack (no file deletion on disk): every file in `bin/` whose base name equals the `AssemblyName` of a project in `DreamGrid.sln`, with extension `.dll`, `.exe`, `.runtimeconfig.json` or `.dll.config`. Also untrack `bin/System.Drawing.Common.dll`, which `SelectSystemDrawing` now produces.

**Interfaces:**
- Consumes: `DreamGrid.sln` and the `.csproj` files from Task 1.
- Produces: the guarantee that a clean clone plus `dotnet build -c Release DreamGrid.sln` yields a complete runtime `bin/`. Task 4's harness and Task 5's CI rely on it.

- [ ] **Step 1: Untrack the outputs**

Build the list from the projects, not by hand:
```bash
for n in $(grep -h -oP '(?<=<AssemblyName>)[^<]+' $(git ls-files '*.csproj') | sort -u); do
  git ls-files "bin/$n.dll" "bin/$n.exe" "bin/$n.runtimeconfig.json" "bin/$n.dll.config"
done | xargs git rm --cached --quiet
git rm --cached --quiet bin/System.Drawing.Common.dll
```
Expected: `git status --short | grep -c '^D '` is between 89 and 120, and `ls bin/OpenSim.dll` still succeeds.

Keep tracked: third-party DLLs (OpenMetaverse*, Nini, log4net, MySql.Data, Mono.Addins*, BulletXNA, etc.), `bin/System.Drawing.Common.dll.win` / `.linux`, and every `.ini`/`.example`/`.proto`/data file.

- [ ] **Step 2: Extend `.gitignore`** with the lines listed under Files.

- [ ] **Step 3: Prove a clean clone builds**

Do this after the Step 5 commit, because a clone only sees committed state:
```bash
d="$(mktemp -d)/clean clone"; git clone -q -c core.longpaths=true . "$d"
dotnet build -c Release "$d/DreamGrid.sln" -nologo -v q
ls "$d/bin/OpenSim.dll" "$d/bin/Robust.dll" "$d/bin/OpenSim.Region.CoreModules.dll" "$d/bin/System.Drawing.Common.dll"
```
Expected: `0 Error(s)`, and all four files exist. If the build fails because a DLL is missing, that DLL was wrongly untracked: restore it with `git checkout HEAD~1 -- bin/<name>`, amend, and repeat.

- [ ] **Step 4: Write `MERGING.md`**

Contents (exact commands):
- One-time setup: `git remote add fred https://github.com/Outworldz/DreamGrid-Opensim.git`.
- Each merge: `git fetch fred` then `git merge fred/V7`.
- Conflicts on our build outputs in `bin/` (modify/delete): resolve with `git rm --cached <file>`. Those outputs are built, not tracked.
- After the merge, run `git diff ORIG_HEAD..fred/V7 -- prebuild.xml addon-modules/*/prebuild*.xml`. For each project or reference added, removed or changed there, make the same change to the matching `.csproj` (new project: create a `.csproj` modeled on a sibling, then `dotnet sln DreamGrid.sln add <path>`).
- Then `dotnet build -c Release DreamGrid.sln` and run the smoke test (`tests/DreamGrid.Smoke`, Task 4).

- [ ] **Step 5: Commit, then run Step 3**

```bash
git add .gitignore MERGING.md && git commit -m "Build: stop tracking our own build outputs in bin/; add MERGING.md"
```
(The `git rm --cached` from Step 1 is already staged.)

---

### Task 3: Restore `BinaryFormatter` on .NET 10 (compatibility package)

**Files:**
- Create: `tests/DreamGrid.Platform.Tests/DreamGrid.Platform.Tests.csproj`, `tests/DreamGrid.Platform.Tests/BinaryFormatterTests.cs`
- Modify, adding one `PackageReference` each and `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`:
  - `OpenSim/Framework/OpenSim.Framework.csproj`
  - `OpenSim/Region/CoreModules/OpenSim.Region.CoreModules.csproj`
  - `OpenSim/Region/Framework/OpenSim.Region.Framework.csproj`
  - `OpenSim/Region/ScriptEngine/YEngine/OpenSim.Region.ScriptEngine.YEngine.csproj`
- Modify: `DreamGrid.sln` (add the test project)

**Interfaces:**
- Consumes: `OpenSim.Framework.Util.SerializeToFile(string filename, object obj)` and `OpenSim.Framework.Util.DeserializeFromFile(string filename) -> object` (existing, unchanged); `OpenSim.Framework.AssetBase` (existing).
- Produces:
  - `bin/System.Runtime.Serialization.Formatters.dll` (package version 10.0.13), deployed next to `OpenSim.dll` by every build.
  - The test project `tests/DreamGrid.Platform.Tests`, which later plans may add tests to.

- [ ] **Step 1: Create the test project**

`DreamGrid.Platform.Tests.csproj`:
- SDK project; `IsPackable=false`; `OutputType=Exe` (required by xUnit v3).
- Packages: `xunit.v3` 4.0.2, `xunit.runner.visualstudio` (latest 3.x), `Microsoft.NET.Test.Sdk` (latest 17.x).
- A `ProjectReference` to `../../OpenSim/Framework/OpenSim.Framework.csproj`.
- `Reference` items with `HintPath` to `../../bin/OpenMetaverseTypes.dll` and `../../bin/log4net.dll`.

Add it with `dotnet sln DreamGrid.sln add tests/DreamGrid.Platform.Tests/DreamGrid.Platform.Tests.csproj`.

- [ ] **Step 2: Write the failing tests**

```csharp
public class BinaryFormatterTests
{
    [Fact]
    public void Util_SerializeToFile_then_DeserializeFromFile_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dg bf {Guid.NewGuid()}.bin");
        var original = new Dictionary<string, int> { ["alpha"] = 1, ["beta"] = 2 };
        OpenSim.Framework.Util.SerializeToFile(path, original);
        var back = OpenSim.Framework.Util.DeserializeFromFile(path) as Dictionary<string, int>;
        Assert.NotNull(back);            // null today: the call throws PlatformNotSupportedException and is swallowed
        Assert.Equal(original, back);
    }

    [Fact]
    public void AssetBase_round_trips_through_BinaryFormatter_like_FlotsamAssetCache()
    {
        var asset = new OpenSim.Framework.AssetBase(OpenMetaverse.UUID.Random(), "smoke", (sbyte)OpenMetaverse.AssetType.Notecard, OpenMetaverse.UUID.Zero.ToString())
        { Data = new byte[] { 1, 2, 3, 4 } };
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var f = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
        f.Serialize(ms, asset);
        ms.Position = 0;
        var back = (OpenSim.Framework.AssetBase)f.Deserialize(ms);
#pragma warning restore SYSLIB0011
        Assert.Equal(asset.FullID, back.FullID);
        Assert.Equal(asset.Data, back.Data);
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test tests/DreamGrid.Platform.Tests -c Release -nologo`
Expected: both FAIL. The first fails on `Assert.NotNull`; the second throws `PlatformNotSupportedException` ("BinaryFormatter serialization and deserialization have been removed").

- [ ] **Step 4: Add the compatibility package to the four projects**

To each of the four `.csproj` files listed under Files, add `<PackageReference Include="System.Runtime.Serialization.Formatters" Version="10.0.13" />` and `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`. The package carries the real implementation; the existing `EnableUnsafeBinaryFormatterSerialization` settings in `OpenSim/Region/Application/runtimeconfig.template.json` and `OpenSim/Server/runtimeconfig.template.json` stay as they are. Add the same `PackageReference` to the test project.

- [ ] **Step 5: Run the tests and confirm they pass; confirm deployment**

Run:
```bash
dotnet build -c Release DreamGrid.sln -nologo -v q
dotnet test tests/DreamGrid.Platform.Tests -c Release -nologo
ls bin/System.Runtime.Serialization.Formatters.dll
```
Expected: `Passed: 2, Failed: 0`, and the file exists. (Whether OpenSim really *loads* this copy rather than the framework's stub is proven at run time by Task 4 check `C4`.)

- [ ] **Step 6: Commit**

```bash
git add tests/DreamGrid.Platform.Tests DreamGrid.sln OpenSim/Framework/OpenSim.Framework.csproj OpenSim/Region/CoreModules/OpenSim.Region.CoreModules.csproj OpenSim/Region/Framework/OpenSim.Region.Framework.csproj OpenSim/Region/ScriptEngine/YEngine/OpenSim.Region.ScriptEngine.YEngine.csproj
git commit -m "Restore BinaryFormatter on .NET 10 via the compatibility package"
```

---

### Task 4: Cross-platform smoke harness (`tests/DreamGrid.Smoke`)

**Files:**
- Create:
  - `tests/DreamGrid.Smoke/DreamGrid.Smoke.csproj` (console app, no OpenSim project references; it only drives `bin/`),
  - `tests/DreamGrid.Smoke/Program.cs` (argument parsing and exit code),
  - `tests/DreamGrid.Smoke/SmokeRun.cs` (sandbox, config writing, process control),
  - `tests/DreamGrid.Smoke/SmokeChecks.cs` (pure check functions over the log text and the sandbox directory),
  - `tests/DreamGrid.Platform.Tests/SmokeChecksTests.cs`.
- Modify: `DreamGrid.sln`; `tests/DreamGrid.Platform.Tests/DreamGrid.Platform.Tests.csproj` (add a `ProjectReference` to the smoke project).

**Interfaces:**
- Consumes: a built `bin/` (Tasks 1–3).
- Produces:
  - **Command line:** `dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin <path-to-bin> [--physics ubODE|BulletSim] [--culture <name>] [--timeout <seconds>] [--keep]`. Defaults: physics `ubODE`, timeout `300`.
  - **Exit code:** `0` if every check passed, `1` otherwise. One line per check is printed: `PASS C1 …` / `FAIL C1 … <reason>`.
  - **`public static class SmokeChecks`**, with:
    - `CheckResult LoginsEnabled(string log)`: `C1` passes iff `log` contains `INITIALIZATION COMPLETE FOR` and `LOGINS ENABLED`.
    - `CheckResult NoForbiddenExceptions(string log)`: `C2` fails iff `log` contains any of `PlatformNotSupportedException`, `TypeInitializationException`, `DllNotFoundException`, `gdiplus`, `Unable to load shared library`.
    - `CheckResult FileNonEmpty(string id, string path)`: used for `C3` (the terrain PNG).
    - `CheckResult DirectoryHasFiles(string id, string dir)`: used for `C4` (the asset cache).
  - **`public sealed record CheckResult(string Id, bool Passed, string Detail)`**.
  - **Checks performed by a run:**
    - `C1` the region boots,
    - `C2` no forbidden exceptions in the log,
    - `C3` `smoke-terrain.png` exists and is non-empty after the startup commands,
    - `C4` `assetcache/` contains at least one file,
    - `C5` `GET http://127.0.0.1:<http_port>/simstatus/` returns body `OK`,
    - `C6` a second boot of the same sandbox reaches `C1` again within the timeout, with no console prompt.

- [ ] **Step 1: Write the failing unit tests for `SmokeChecks`**

In `SmokeChecksTests.cs`:
- `LoginsEnabled_passes_on_the_RegionReady_line`: log `"INITIALIZATION COMPLETE FOR Smoke Region - LOGINS ENABLED"` gives `Passed == true`, `Id == "C1"`.
- `LoginsEnabled_fails_on_empty_log`: gives `Passed == false`.
- `NoForbiddenExceptions_fails_on_PlatformNotSupported`: a log containing `System.PlatformNotSupportedException: BinaryFormatter serialization` gives `Passed == false`, and `Detail` contains `PlatformNotSupportedException`.
- `NoForbiddenExceptions_fails_on_gdiplus`: a log containing `Unable to load shared library 'libgdiplus'` gives `Passed == false`.
- `NoForbiddenExceptions_passes_on_clean_log`: `"[REGIONMODULES]: Loaded 42 modules"` gives `Passed == true`.
- `DirectoryHasFiles_fails_for_empty_dir` and `DirectoryHasFiles_passes_with_one_file`: use a temp dir.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/DreamGrid.Platform.Tests -c Release -nologo --filter SmokeChecks`
Expected: build error, `SmokeChecks` not defined.

- [ ] **Step 3: Implement `SmokeChecks` and `CheckResult` in `SmokeChecks.cs`**

Plain string and file checks as specified under Interfaces.

- [ ] **Step 4: Run the tests and confirm they pass**

Same command. Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 5: Implement `SmokeRun` and `Program`**

**Sandbox and configuration:**
- **Sandbox:** copy the whole `--bin` directory to `<temp>/dg smoke <yyyyMMdd-HHmmss>/bin`. The space in the name is deliberate (Review Focus 3). Exclude `addin-db-*`, `assetcache`, `*.log` and `*.db`.
- **Free ports:** pick two free TCP ports: `http_port` (OpenSim HTTP / `[Const] PublicPort`) and `region_port` (UDP/region).
- **`OpenSim.ini`:**
  - `[Const]`: copy the section verbatim from `bin/OpenSim.ini.example`, then set `BaseHostname = 127.0.0.1` and `PublicPort = <http_port>`.
  - `[Startup]`: `physics = <--physics>`, `startup_console_commands_file = smoke-commands.txt`.
  - `[Network]`: `http_listener_port = <http_port>`.
  - `[Map]`: `GenerateMaptiles = true`, `MapImageModule = "Warp3DImageModule"`.
  - `[Estates]`: `DefaultEstateName = Smoke Estate`, `DefaultEstateOwnerName = Smoke Owner`, `DefaultEstateOwnerUUID = 11111111-2222-3333-4444-555555555555`, `DefaultEstateOwnerEMail = smoke@example.invalid`, `DefaultEstateOwnerPassword = smoke-password`.
  - `[Modules]`: `AssetCaching = "FlotsamAssetCache"`, `Include-FlotsamCache = "config-include/FlotsamCache.ini"`.
  - `[Architecture]`: `Include-Architecture = "config-include/Standalone.ini"`.
- **Other config files:**
  - Copy `config-include/StandaloneCommon.ini.example` to `StandaloneCommon.ini`, and `config-include/FlotsamCache.ini.example` to `FlotsamCache.ini`.
  - Write `Regions/Regions.ini` with one region `[Smoke Region]`: `RegionUUID = 6f1f6b1e-0000-4000-8000-00000000d6d6`, `Location = 1000,1000`, `InternalAddress = 0.0.0.0`, `InternalPort = <region_port>`, `ExternalHostName = 127.0.0.1`.
  - Write `smoke-commands.txt` with two lines: `terrain save smoke-terrain.png` and `terrain load smoke-terrain.png`.

**Process control:**
- **Start:** `dotnet OpenSim.dll` with working directory = sandbox `bin`, stdin/stdout/stderr redirected. Append stdout and stderr to an in-memory log and to `smoke.log` in the sandbox.
- **Culture:** if `--culture` is given, set both `LC_ALL` and `LANG` to it in the child environment.
- **Waiting:** poll every 1 s until `C1` passes or `--timeout` elapses. Then wait up to 30 s for `smoke-terrain.png`. Then do `C5` with `HttpClient`.
- **Stop:** write `quit\n` to stdin, wait 60 s, then kill the process tree.
- **Second boot:** start again in the same sandbox and evaluate `C1` on the second boot's output only. That is `C6`.

**Results:**
- Evaluate `C2` over both boots' logs, and `C3` and `C4` after the first boot.
- Print the results, and the path of `smoke.log` on any failure.
- Delete the sandbox on success unless `--keep` is given.

- [ ] **Step 6: Run the harness on Windows**

Run:
```bash
dotnet build -c Release DreamGrid.sln -nologo -v q
dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin bin
dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin bin --physics BulletSim
```
Expected: each prints `PASS` for C1–C6 and exits 0.

If `C4` fails with `PlatformNotSupportedException` in the log, the compatibility DLL from Task 3 is not the one being loaded. Fix by adding `System.Runtime.Serialization.Formatters` as a `PackageReference` to `OpenSim/Region/Application/OpenSim.csproj` and `OpenSim/Server/Robust.csproj` as well. Those are the exe projects, so their `runtimeconfig`/probing includes it. Re-run.

If a check fails because the boot blocked on an estate or user prompt, the `[Estates]` keys above are wrong for this OpenSim version. Read `OpenSim/Region/Application/OpenSimBase.cs` (the method after `NotifyDreamGridOfPidFireAndForget`, "Try to set up the estate owner") for the keys it reads, correct `SmokeRun`, and re-run. Record the working keys in a code comment; plan 2's launcher reuses them (spec §9 risk "non-interactive estate creation").

- [ ] **Step 7: Run the harness on Debian**

On a Debian 12 or 13 machine (or `wsl --install -d Debian` on the dev box):
- install the .NET 10 SDK and `libgdiplus`,
- clone the branch, run `dotnet build -c Release DreamGrid.sln`,
- run the same two harness commands, plus `--culture de_DE.UTF-8` (after `sudo sed -i 's/# de_DE.UTF-8/de_DE.UTF-8/' /etc/locale.gen && sudo locale-gen`).

Expected: all PASS.

**Decision gate (spec §4.9).** If `C2` or `C3` fails with `gdiplus`, `System.Drawing` or `TypeInitializationException` errors on Debian, **stop this plan here.** Report the failing log lines. The `System.Drawing` → SkiaSharp replacement becomes its own plan (plan 1b) before Task 5. Do not start that replacement inside this plan. Failures of native physics libraries (`DllNotFoundException` for `libubode`/`libBulletSim`) are fixed here instead: check file names in `bin/lib64` against the names the `.dll.config` files map to, and correct the mapping.

- [ ] **Step 8: Commit**

```bash
git add tests/DreamGrid.Smoke tests/DreamGrid.Platform.Tests DreamGrid.sln
git commit -m "Add cross-platform OpenSim smoke harness"
```

---

### Task 5: CI on Windows and Debian

**Files:**
- Create: `.github/workflows/dreamgrid-ci.yml`. Leave the existing `.github/workflows/msbuildnet.yml` untouched; it only runs on `opensim/opensim`, and leaving it avoids merge conflicts with Fred.

**Interfaces:**
- Consumes: `DreamGrid.sln`, `tests/DreamGrid.Platform.Tests`, `tests/DreamGrid.Smoke` (command line as defined in Task 4).
- Produces: a required-looking status check named `build-and-smoke` with two jobs, `windows` and `debian`. Plan 3's release pipeline extends this workflow.

- [ ] **Step 1: Write the workflow**

- **Triggers:** `push` on any branch, `pull_request`, `workflow_dispatch`.
- **Job `windows`** (`runs-on: windows-latest`):
  1. A first step running `git config --system core.longpaths true`. The repo has paths longer than 260 characters, and checkout fails without this.
  2. `actions/checkout@v4` with `submodules: false`.
  3. `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`.
  4. `dotnet build -c Release DreamGrid.sln`.
  5. `dotnet test tests/DreamGrid.Platform.Tests -c Release`.
  6. The smoke harness, with `--physics ubODE`, then with `--physics BulletSim`.
- **Job `debian`** (`runs-on: ubuntu-latest`, `container: mcr.microsoft.com/dotnet/sdk:10.0`, which is Debian 12):
  1. `apt-get update && apt-get install -y libgdiplus locales git`.
  2. Generate `de_DE.UTF-8`.
  3. Checkout, then build and test as on Windows.
  4. The smoke harness three times: ubODE, BulletSim, and ubODE with `--culture de_DE.UTF-8`.
- **Artifacts:** on failure, upload `**/dg smoke */bin/smoke.log`. Run the smoke steps with `--keep` so the logs survive.

- [ ] **Step 2: Push the branch and confirm both jobs pass**

Run: `git push -u origin design/crossplatform-launcher-v1`, then `gh run watch --exit-status`.
Expected: `windows` and `debian` both succeed. If one fails, fix the cause in the task that owns it, not by loosening a check.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/dreamgrid-ci.yml
git commit -m "CI: build, unit tests and smoke test on Windows and Debian"
```
(Commit before Step 2's push if the push is what triggers the run.)

---

## Self-review notes

**Spec coverage.**
- §4.4: SDK-style projects, one solution, Prebuild retired, `Directory.Build.props` → Task 1. The "one solution with launcher and web" part arrives in plan 2, which adds those projects to `DreamGrid.sln`.
- §4.6: Fred merges, `prebuild.xml` mirroring → Task 2 `MERGING.md`. Layout untouched → Global Constraints.
- §4.9: `System.Drawing` test-first, SkiaSharp fallback → Task 4 Step 7 decision gate.
- §8:
  - step 1 → Task 1,
  - step 2 → Task 4,
  - step 3 (build breaks) → Task 1 Step 5 (trial already shows none),
  - step 4 (committed DLL compatibility) → Tasks 3 and 4 (the runtime proves it),
  - step 5 (`LaunchSLClient` is not in `prebuild.xml`, so not built; `kernel32` calls are already OS-guarded) → nothing to do,
  - step 6 (native libs) → Task 4 Steps 6–7 with both physics engines,
  - step 7 → Task 5.
- §9: the `System.Drawing`, DLL-compatibility and estate-creation risks are all exercised by the smoke harness.

**Not in this plan, by design:**
- Robust in grid mode with MariaDB (needs the launcher's private MariaDB; plan 2's smoke test).
- Launcher, web site, installers (plans 2 and 3).
