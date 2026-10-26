<#
.SYNOPSIS  Checks a package folder produced by setup/package.ps1 (the dreamgrid/ folder).
.EXAMPLE   powershell -File setup/test/Test-Package.ps1 -Package $env:TEMP\dgpkg\dreamgrid
#>
param([Parameter(Mandatory)][string]$Package)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$message) { $failures.Add($message); Write-Host "FAIL: $message" }

$Package = [IO.Path]::GetFullPath($Package)
$isWindowsPackage = Test-Path (Join-Path $Package 'dotnet/dotnet.exe')
$isLinuxPackage = Test-Path (Join-Path $Package 'dotnet/dotnet')
$dotnetName = if ($isWindowsPackage) { 'dotnet.exe' } else { 'dotnet' }

$required = @(
    "dotnet/$dotnetName", 'launcher/DreamGrid.Launcher.dll', 'bin/OpenSim.dll', 'bin/Robust.dll',
    'content/README.md', 'VERSION', 'RUNTIME_VERSION')
# The Debian installer files only ship in the linux-x64 package.
if (-not $isWindowsPackage) { $required += 'setup/debian/install.sh', 'setup/debian/dreamgrid.service' }

foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $Package $relative) -PathType Leaf)) { Fail "missing file: $relative" }
}

if ($failures.Count -eq 0) {
    $expected = (Get-Content -LiteralPath (Join-Path $Package 'VERSION') -Raw).Trim()
    $actual = ((& (Join-Path $Package "dotnet/$dotnetName") (Join-Path $Package 'launcher/DreamGrid.Launcher.dll') --version) -join "`n").Trim()
    if ($LASTEXITCODE -ne 0) { Fail "launcher --version exited with $LASTEXITCODE" }
    elseif ($actual -ne $expected) { Fail "launcher --version printed '$actual', VERSION says '$expected'" }
    else { Write-Host "launcher --version: $actual" }

    $runtime = (Get-Content -LiteralPath (Join-Path $Package 'RUNTIME_VERSION') -Raw).Trim()
    if ($runtime -notmatch '^\d+\.\d+\.\d+$') { Fail "RUNTIME_VERSION '$runtime' is not a version" }

    $pdbs = @(Get-ChildItem -LiteralPath $Package -Recurse -Force -Filter '*.pdb' -File)
    foreach ($pdb in $pdbs) { Fail "debug symbols present: $($pdb.FullName.Substring($Package.Length + 1))" }
}

if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed."; exit 1 }
Write-Host "PASS: $Package"
