# git clone

get or update source from git

 `git clone -c core.longpaths=true https://github.com/ystudy/DreamGrid-Opensim_Linux.git`

On Windows the repository has paths longer than 260 characters, so `core.longpaths` is required.

# Project files

The `.csproj` files and `DreamGrid.sln` are checked in. Prebuild is no longer run:
`prebuild.xml` is kept only as a reference when merging from Outworldz/DreamGrid-Opensim; see `MERGING.md`.
Shared settings (target framework `net10.0`) live in `Directory.Build.props` and `Directory.Build.targets`.

# Building on Windows

## Requirements

  * [dotnet 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

optionally also

  * [Visual Studio](https://visualstudio.microsoft.com/vs/features/net-development/), version 2022 17.14 or later

### Building

run
  `compile.bat`

which is `dotnet build -c Release DreamGrid.sln`. Or open `DreamGrid.sln` in Visual Studio and build the solution.

Configure, see below

Now just run `OpenSim.exe` from the `bin` folder, and set up the region.

# Building on Linux (Debian)

## Requirements

 * [dotnet 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
 * libgdiplus

 `apt-get update && apt-get install -y libgdiplus libc6-dev`

### Building

 `dotnet build -c Release DreamGrid.sln`

The build copies the Linux `System.Drawing.Common.dll` into `bin/` by itself.

Configure. See below

run `./opensim.sh` from the `bin` folder, and set up the region



# Configure #
## Standalone mode ##
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case.

On `[Architecture]` section uncomment only the line with Standalone.ini if you do now want HG, or the line with StandaloneHypergrid.ini if you do

copy the `StandaloneCommon.ini.example` to `StandaloneCommon.ini` in the `bin/config-include` directory.

The StandaloneCommon.ini file describes the database and backend services that OpenSim will use, and is set to use sqlite by default, which requires no setup.


## Grid mode ##
Each grid may have its own requirements, so FOLLOW your Grid instructions!
in general:
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case
 
On `[Architecture]` section uncomment only the line with Grid.ini if you do now want HG, or the line with GridHypergrid.ini if you do

and copy the `GridCommon.ini.example` file to `GridCommon.ini` inside the `bin/config-include` directory and edit as necessary



# References

* http://opensimulator.org/wiki/Build_Instructions
* http://opensimulator.org/wiki/Configuration