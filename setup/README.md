# Release packaging

`setup/package.ps1` assembles the folder that the installers consume. It runs on Windows PowerShell 5.1 and PowerShell 7.

```
dreamgrid/
  launcher/        DreamGrid.Launcher, published framework-dependent
  bin/             the built OpenSim runtime (no *.pdb, *.log, *.pid, *.db, runtime-only folders, Gloebit.dll)
  content/         README.md and starter-region.oar (when present)
  dotnet/          private ASP.NET Core runtime for the RID
  VERSION          the package version, one line
  RUNTIME_VERSION  the bundled ASP.NET Core runtime version, one line
  setup/debian/    linux-x64 only: install.sh, dreamgrid.service
```

The excluded folders and files match what `RuntimeStager` leaves out of the staged runtime. The linux-x64 package also drops the Windows-only natives (`lib32/`, `bin/lib64/*.dll`, `lame.exe`, `lame_enc.dll`, `libzstd.dll`); the list is in the script.

## Building a package

```
powershell -File setup/package.ps1 -Rid win-x64 -Version 0.0.0-dev -Out $env:TEMP\dgpkg
powershell -File setup/test/Test-Package.ps1 -Package $env:TEMP\dgpkg\dreamgrid
```

`-Rid linux-x64` must run on Linux (CI): it also writes `dreamgrid-<Version>-linux-x64.tar.gz`, keeping the execute bits on `dotnet/dotnet` and `setup/debian/install.sh`.

`-RuntimeVersion 10.0.n` pins the ASP.NET Core runtime; without it the script uses `latest-runtime` from the .NET 10 `releases.json`. The runtime archive is downloaded to a temp cache outside the repo and its SHA-512 is checked against `releases.json`.

`Test-Package.ps1` checks the layout, that `launcher --version` prints exactly `VERSION`, that `OpenSim.dll` and `Robust.dll` are present and that no `*.pdb` files shipped.

## Installer verbs of the launcher

The installers call the launcher (`dotnet launcher/DreamGrid.Launcher.dll <verb> …`) with these verbs as the first argument:

- `--version` prints the build version.
- `admin-link --data <dir> [--wait <s>] [--open]` prints the setup link while setup is open, else the admin page's address; it never creates the settings (exit 4 when none appear in time).
- `set-web-port <n> --data <dir>` saves the admin website's port. It needs the service stopped (exit 3 while a launcher holds the data directory), refuses a port the launcher would refuse with the same message (exit 2: 1024–65535, not another DreamGrid port) and creates the settings when there are none. `install.sh --web-port` and `DreamGridSetup.exe /WEBPORT=` use it; neither puts `--web-port` into the service's command line.
- `stop-grid --data <dir> --opensim <dir> [--mariadb-bin <dir>]` stops the region, Robust and MariaDB (the uninstaller) and waits up to 60 s for them to exit (exit 5 otherwise).

The host option `--web-port <n>` (for development) applies the port again on every start, so a port changed later in Advanced Settings → Network would be undone at the next start.
