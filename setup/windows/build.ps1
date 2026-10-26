<#
.SYNOPSIS  Builds the Windows installer DreamGridSetup-<Version>.exe from a win-x64 package and a MariaDB zip.
.DESCRIPTION
  Stages <Out>\staging\ from the package folder (setup/package.ps1 -Rid win-x64): launcher\, bin\, content\, VERSION,
  RUNTIME_VERSION, dotnet\ renamed to dotnet-<RUNTIME_VERSION>\, and MariaDB's bin\, lib\ and share\ (no include\, no
  *.pdb) as mariadb-<version>\. Then compiles DreamGrid.iss with Inno Setup 6 (ISCC.exe) into <Out>.
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
if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+$') { throw "The package's RUNTIME_VERSION '$runtimeVersion' is not a version." }

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

$bytes = (Get-ChildItem -LiteralPath $staging -Recurse -Force -File | Measure-Object -Property Length -Sum).Sum
Write-Host ('Staged {0} (dotnet-{1}, mariadb-{2}): {3:N1} MB' -f $staging, $runtimeVersion, $mariaDbVersion, ($bytes / 1MB))
if ($SkipCompile) { return }

# --- Compile -----------------------------------------------------------------------------------------------------
if (-not $Iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(if ($found) { $found.Source }) + @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    $Iscc = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) })[0]
    if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (choco install innosetup) or pass -Iscc <path>.' }
}
# DreamGrid.iss uses x64compatible, which needs Inno Setup 6.3 or later.
$isccInfo = (Get-Item -LiteralPath $Iscc).VersionInfo
$isccVersion = New-Object Version $isccInfo.FileMajorPart, $isccInfo.FileMinorPart, $isccInfo.FileBuildPart
if ($isccVersion -lt [Version]'6.3.0') { throw "$Iscc is Inno Setup $isccVersion; DreamGrid.iss needs 6.3 or later." }
Write-Host "Inno Setup $isccVersion ($Iscc)"
& $Iscc "/DAppVersion=$Version" "/DRuntimeVersion=$runtimeVersion" "/DMariaDbVersion=$mariaDbVersion" "/DSource=$staging" `
    "/O$Out" (Join-Path $PSScriptRoot 'DreamGrid.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }
$setup = Join-Path $Out "DreamGridSetup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC.exe did not produce $setup" }
Write-Host ('Installer: {0} ({1:N1} MB)' -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
