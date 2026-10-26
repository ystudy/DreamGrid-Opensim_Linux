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
