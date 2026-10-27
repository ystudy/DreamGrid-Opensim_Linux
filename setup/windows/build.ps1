<#
.SYNOPSIS  Builds the Windows installer DreamGridSetup-<Version>.exe from a win-x64 package and a MariaDB zip.
.DESCRIPTION
  Stages <Out>\staging\ from the package folder (setup/package.ps1 -Rid win-x64): launcher\, bin\, content\, VERSION,
  RUNTIME_VERSION, dotnet\ renamed to dotnet-<RUNTIME_VERSION>\, and MariaDB's bin\, lib\ and share\ (no include\, no
  *.pdb) as mariadb-<version>\, plus MariaDB's copies of the Visual C++ runtime DLLs that bin\lib64's natives import.
  Then compiles DreamGrid.iss with Inno Setup 6 (ISCC.exe) into <Out>.
  -SkipCompile stops after staging (no Inno Setup needed). Runs on Windows PowerShell 5.1 and PowerShell 7.
.EXAMPLE   powershell -File setup/windows/build.ps1 -Package $env:TEMP\dgpkg\dreamgrid -MariaDbZip mariadb-11.4.9-winx64.zip -Version 0.0.0-dev -Out $env:TEMP\dgsetup
#>
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$MariaDbZip,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [switch]$SkipCompile,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Package = [IO.Path]::GetFullPath($Package)
$MariaDbZip = [IO.Path]::GetFullPath($MariaDbZip)
$Out = [IO.Path]::GetFullPath($Out)
$staging = Join-Path $Out 'staging'

# --- Package -----------------------------------------------------------------------------------------------------
foreach ($required in 'VERSION', 'RUNTIME_VERSION', 'dotnet\dotnet.exe', 'launcher\DreamGrid.Launcher.dll', 'bin\OpenSim.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $Package $required) -PathType Leaf)) {
        throw "$Package is not a win-x64 DreamGrid package (it has no $required)."
    }
}
$packageVersion = (Get-Content -LiteralPath (Join-Path $Package 'VERSION') -TotalCount 1).Trim()
if ($packageVersion -ne $Version) { throw "-Version is $Version but the package is version $packageVersion." }
$runtimeVersion = (Get-Content -LiteralPath (Join-Path $Package 'RUNTIME_VERSION') -TotalCount 1).Trim()
# A suffix (10.0.n-upgradetest) is allowed: CI relabels a package's runtime to test a runtime change on upgrade.
if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "The package's RUNTIME_VERSION '$runtimeVersion' is not a version." }

# --- Staging -----------------------------------------------------------------------------------------------------
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
foreach ($folder in 'launcher', 'bin', 'content') { Copy-Item -LiteralPath (Join-Path $Package $folder) -Destination $staging -Recurse }
foreach ($file in 'VERSION', 'RUNTIME_VERSION') { Copy-Item -LiteralPath (Join-Path $Package $file) -Destination $staging }
Copy-Item -LiteralPath (Join-Path $Package 'dotnet') -Destination (Join-Path $staging "dotnet-$runtimeVersion") -Recurse

# MariaDB: the zip holds one top folder, mariadb-<version>-winx64\.
$zip = [IO.Compression.ZipFile]::OpenRead($MariaDbZip)
try {
    $tops = @($zip.Entries | ForEach-Object { ($_.FullName -replace '\\', '/').Split('/')[0] } | Sort-Object -Unique)
    if ($tops.Count -ne 1 -or -not ($tops[0] -match '^mariadb-(\d+\.\d+\.\d+)-winx64$')) {
        throw "$MariaDbZip is not a MariaDB winx64 zip (top folders: $($tops -join ', '))."
    }
    $mariaDbVersion = $Matches[1]
    $mariaDb = Join-Path $staging "mariadb-$mariaDbVersion"
    foreach ($entry in $zip.Entries) {
        $parts = ($entry.FullName -replace '\\', '/').Split('/')
        if ($parts.Count -lt 3 -or $parts[1] -notin 'bin', 'lib', 'share') { continue }
        if ($entry.Name -eq '' -or $entry.Name -like '*.pdb') { continue }
        $relative = ($parts[1..($parts.Count - 1)] -join '\')
        if ($relative.Split('\') -contains '..') { throw "$MariaDbZip has an unsafe entry: $($entry.FullName)" }
        $target = Join-Path $mariaDb $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally { $zip.Dispose() }
if (-not (Test-Path -LiteralPath (Join-Path $mariaDb 'bin\mariadbd.exe'))) { throw "$MariaDbZip has no bin\mariadbd.exe." }

# Visual C++ runtime: OpenSim's bin\lib64\ubode.dll imports vcruntime140.dll, which a clean Windows does not have
# (the region then exits with "Unable to load DLL 'ubode'"). Each VC++ 14 runtime DLL a lib64 native imports is copied
# beside it from MariaDB's bin\, which carries Microsoft's app-local copies; Windows looks in a DLL's own folder for
# its imports when the DLL is loaded by full path. The import names are read as text from the binary.
$lib64 = Join-Path $staging 'bin\lib64'
$vcRuntime = New-Object System.Collections.Generic.SortedSet[string] ([StringComparer]::OrdinalIgnoreCase)
foreach ($native in Get-ChildItem -LiteralPath $lib64 -Filter '*.dll' -File) {
    $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($native.FullName))
    foreach ($match in [regex]::Matches($text, '(?i)\b(?:vcruntime140(?:_1)?|msvcp140(?:_\w+)?|vcomp140|concrt140)\.dll')) {
        if ($match.Value -ine $native.Name) { [void]$vcRuntime.Add($match.Value.ToLowerInvariant()) }
    }
}
foreach ($name in $vcRuntime) {
    $source = Join-Path $mariaDb "bin\$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "bin\lib64 needs $name, which $MariaDbZip does not carry." }
    Copy-Item -LiteralPath $source -Destination $lib64
}
Write-Host "Visual C++ runtime in bin\lib64: $(if ($vcRuntime.Count) { $vcRuntime -join ', ' } else { 'none needed' })"

$bytes = (Get-ChildItem -LiteralPath $staging -Recurse -Force -File | Measure-Object -Property Length -Sum).Sum
Write-Host ('Staged {0} (dotnet-{1}, mariadb-{2}): {3:N1} MB' -f $staging, $runtimeVersion, $mariaDbVersion, ($bytes / 1MB))
if ($SkipCompile) { return }

# --- Compile -----------------------------------------------------------------------------------------------------
if (-not $Iscc) {
    # Order: the two Program Files installs, then PATH. Chocolatey's ISCC.exe on PATH is a shim (under \Chocolatey\bin\)
    # and is skipped. The version is not sniffed from the file's version resource, which is unreliable (it can read 0);
    # DreamGrid.iss enforces the minimum Inno Setup version itself.
    $onPath = @(Get-Command ISCC.exe -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source } |
        Where-Object { $_ -notlike '*\Chocolatey\bin\*' })
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) + $onPath
    $Iscc = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) })[0]
    if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (choco install innosetup) or pass -Iscc <path>.' }
}
$isccInfo = (Get-Item -LiteralPath $Iscc).VersionInfo
Write-Host "ISCC: $Iscc (FileVersion '$($isccInfo.FileVersion)', ProductVersion '$($isccInfo.ProductVersion)')"
& $Iscc "/DAppVersion=$Version" "/DRuntimeVersion=$runtimeVersion" "/DMariaDbVersion=$mariaDbVersion" "/DSource=$staging" `
    "/O$Out" (Join-Path $PSScriptRoot 'DreamGrid.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }
$setup = Join-Path $Out "DreamGridSetup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC.exe did not produce $setup" }
Write-Host ('Installer: {0} ({1:N1} MB)' -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
