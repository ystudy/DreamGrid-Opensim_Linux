<#
.SYNOPSIS  Builds the DreamGrid release package for one runtime identifier.
.DESCRIPTION
  Produces <Out>/dreamgrid/ (launcher, bin, content, a private ASP.NET Core runtime, VERSION, RUNTIME_VERSION).
  linux-x64 also produces <Out>/dreamgrid-<Version>-linux-x64.tar.gz and must be built on Linux.
  Runs on Windows PowerShell 5.1 and PowerShell 7.
.EXAMPLE   powershell -File setup/package.ps1 -Rid win-x64 -Version 0.0.0-dev -Out $env:TEMP\dgpkg
#>
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'win-x64')][string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [string]$RuntimeVersion
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if ($Rid -eq 'linux-x64' -and $onWindows) { throw 'linux-x64 packages keep Unix execute bits and must be built on Linux (CI).' }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Out = [IO.Path]::GetFullPath($Out)
$pkg = Join-Path $Out 'dreamgrid'
$cache = Join-Path ([IO.Path]::GetTempPath()) 'dreamgrid-package-cache'

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

# --- Runtime resolution -----------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $cache | Out-Null
$releasesFile = Join-Path $cache 'releases.json'
Invoke-WebRequest -UseBasicParsing 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -OutFile $releasesFile
$releases = Get-Content -LiteralPath $releasesFile -Raw | ConvertFrom-Json
if (-not $RuntimeVersion) { $RuntimeVersion = $releases.'latest-runtime' }
$release = @($releases.releases | Where-Object { $_.'aspnetcore-runtime'.version -eq $RuntimeVersion })[0]
if (-not $release) { throw "ASP.NET Core runtime $RuntimeVersion is not in releases.json" }
$extension = if ($Rid -eq 'win-x64') { '.zip' } else { '.tar.gz' }
$file = @($release.'aspnetcore-runtime'.files | Where-Object { $_.rid -eq $Rid -and $_.url.EndsWith($extension) })[0]
if (-not $file) { throw "No $Rid$extension ASP.NET Core runtime in release $RuntimeVersion" }

$archive = Join-Path $cache ([IO.Path]::GetFileName($file.url))
Invoke-WebRequest -UseBasicParsing $file.url -OutFile $archive
$actualHash = (Get-FileHash -Algorithm SHA512 -LiteralPath $archive).Hash
if ($actualHash -ne $file.hash) {
    Remove-Item -LiteralPath $archive -Force
    throw "SHA-512 mismatch for $($file.url): expected $($file.hash), got $actualHash"
}

# --- Build ------------------------------------------------------------------------------------------------------
if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
New-Item -ItemType Directory -Force $pkg | Out-Null

Push-Location $repo
try {
    Invoke-Native 'dotnet build' { dotnet build -c Release DreamGrid.sln "-p:Version=$Version" }
    Invoke-Native 'dotnet publish' { dotnet publish Launcher/DreamGrid.Launcher -c Release "-p:Version=$Version" -o (Join-Path $pkg 'launcher') }
}
finally { Pop-Location }

# --- bin/ -------------------------------------------------------------------------------------------------------
# Same folders and files RuntimeStager leaves out of the staged runtime copy (assetcache, addin-db-* ...).
$excludedDirectories = @('assetcache', 'Regions', 'ScriptEngines', 'fsassets', 'MeshCache', 'j2kDecodeCache', 'maptiles', 'bakes')
# Wifi.ini and Wifi.bak are a developer's live Wifi configs (passwords); only Wifi.ini.proto ships.
$excludedFiles = @('Gloebit.dll', 'Wifi.ini', 'Wifi.bak')
$excludedExtensions = @('.pdb', '.log', '.pid', '.db')
# Windows-only native binaries, dropped from the linux-x64 package (paths relative to bin/, '/'-separated):
#   lib32/            32-bit Windows natives
#   lib64/*.dll       Windows copies of BulletSim, openjpeg, ubode and sqlite (Linux uses the lib*.so beside them)
#   lame.exe, lame_enc.dll, libzstd.dll   Windows MP3 encoder and zstd native library
$linuxExcludedDirectories = @('lib32')
$linuxExcludedFiles = @('lame.exe', 'lame_enc.dll', 'libzstd.dll')

function Copy-Bin([string]$Source, [string]$Target, [string]$Relative) {
    New-Item -ItemType Directory -Force $Target | Out-Null
    foreach ($entry in (New-Object IO.DirectoryInfo $Source).GetFileSystemInfos()) {
        $name = $entry.Name
        $path = if ($Relative) { "$Relative/$name" } else { $name }
        if ($entry -is [IO.DirectoryInfo]) {
            if ($name.StartsWith('addin-db-', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($excludedDirectories -icontains $name) { continue }
            if ($Rid -eq 'linux-x64' -and $linuxExcludedDirectories -icontains $path) { continue }
            Copy-Bin $entry.FullName (Join-Path $Target $name) $path
        }
        else {
            if ($excludedFiles -icontains $name) { continue }
            if ($excludedExtensions -icontains $entry.Extension) { continue }
            if ($Rid -eq 'linux-x64') {
                if ($linuxExcludedFiles -icontains $path) { continue }
                if ($Relative -eq 'lib64' -and $entry.Extension -ieq '.dll') { continue }
            }
            [IO.File]::Copy($entry.FullName, (Join-Path $Target $name), $true)
        }
    }
}
Copy-Bin (Join-Path $repo 'bin') (Join-Path $pkg 'bin') ''

# --- content/, runtime, version files, Debian installer files ---------------------------------------------------
$content = Join-Path $pkg 'content'
New-Item -ItemType Directory -Force $content | Out-Null
Copy-Item (Join-Path $repo 'content/README.md') $content
$oar = Join-Path $repo 'content/starter-region.oar'
if (Test-Path $oar) { Copy-Item $oar $content }

$dotnetDir = Join-Path $pkg 'dotnet'
New-Item -ItemType Directory -Force $dotnetDir | Out-Null
if ($Rid -eq 'win-x64') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $dotnetDir)
}
else {
    Invoke-Native 'tar extract' { tar -xzf $archive -C $dotnetDir }
}

# Single-line files without a BOM or CRLF so shell scripts can read them as-is.
[IO.File]::WriteAllText((Join-Path $pkg 'VERSION'), "$Version`n")
[IO.File]::WriteAllText((Join-Path $pkg 'RUNTIME_VERSION'), "$RuntimeVersion`n")

if ($Rid -eq 'linux-x64') {
    $debian = Join-Path $pkg 'setup/debian'
    New-Item -ItemType Directory -Force $debian | Out-Null
    foreach ($name in 'install.sh', 'dreamgrid.service') {
        $source = Join-Path $repo "setup/debian/$name"
        if (Test-Path $source) { Copy-Item $source $debian }
    }
}

# Publish output may carry symbols.
Get-ChildItem -LiteralPath $pkg -Recurse -Force -Filter '*.pdb' -File | Remove-Item -Force

# --- Tarball (linux-x64) ----------------------------------------------------------------------------------------
if ($Rid -eq 'linux-x64') {
    $dotnetHost = Join-Path $pkg 'dotnet/dotnet'
    $installer = Join-Path $pkg 'setup/debian/install.sh'
    Invoke-Native 'chmod' { chmod +x $dotnetHost }
    if (Test-Path $installer) { Invoke-Native 'chmod' { chmod +x $installer } }
    $tarball = Join-Path $Out "dreamgrid-$Version-linux-x64.tar.gz"
    if (Test-Path $tarball) { Remove-Item -Force $tarball }
    Invoke-Native 'tar' { tar -czf $tarball --owner=0 --group=0 -C $Out dreamgrid }
    Write-Host "Package: $tarball"
}
else { Write-Host "Package folder: $pkg" }

$bytes = (Get-ChildItem -LiteralPath $pkg -Recurse -Force -File | Measure-Object -Property Length -Sum).Sum
Write-Host ('Package size: {0:N1} MB' -f ($bytes / 1MB))
