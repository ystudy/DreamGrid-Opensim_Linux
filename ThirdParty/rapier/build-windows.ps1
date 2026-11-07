<#
Builds Rapier's C library (3D, single precision) for 64-bit Windows from the commit in source.env.

  build-windows.ps1 -OutDir <directory> [-Parallel]

Without -Parallel the library is built without Rapier's parallel feature and OutDir receives rapier.dll, which OpenSim
loads when RapierNumThreads is 1. With -Parallel it is built with --features parallel (rayon) and OutDir receives
rapier-mt.dll, which OpenSim loads only when RapierNumThreads is more than 1.
The C runtime is linked in statically (crt-static), so the DLL imports only KERNEL32, ntdll
and api-ms-win-core-* style system DLLs, not VCRUNTIME140 or MSVCP140.
Needs git, rustup with the toolchain named in source.env (RUST_TOOLCHAIN) and its x86_64-pc-windows-msvc target, and Visual Studio 2022 (or its Build Tools)
with the C++ workload for the linker.
#>
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [switch] $Parallel
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$source = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'source.env')) {
    if ($line -match '^\s*([A-Z_]+)=(.*)$') { $source[$Matches[1]] = $Matches[2].Trim() }
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

function Invoke-Native([string] $exe, [string[]] $arguments) {
    # Cargo writes its progress to stderr; Windows PowerShell 5.1 would turn that into a terminating error.
    $ErrorActionPreference = 'Continue'
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') exited with $LASTEXITCODE" }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("rapier-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $src = Join-Path $work 'src'
    Invoke-Native git @('-c', 'init.defaultBranch=main', 'init', '-q', $src)
    Push-Location $src
    try {
        Invoke-Native git @('config', 'core.autocrlf', 'false')
        Invoke-Native git @('remote', 'add', 'origin', $source.RAPIER_REPO)
        Invoke-Native git @('fetch', '-q', '--depth', '1', '--filter=blob:none', 'origin', $source.RAPIER_COMMIT)
        # The workspace manifest names every crate in the repository, so all the crate directories must exist.
        Invoke-Native git @('sparse-checkout', 'set', '--cone', 'bindings/c', 'bindings/python', 'crates', 'src', 'examples2d', 'examples3d', 'examples3d-f64')
        Invoke-Native git @('checkout', '-q', 'FETCH_HEAD')
        Write-Host "rapier $($source.RAPIER_TAG) $(git rev-parse HEAD)"
        # Rapier has no Cargo.lock; this folder's one fixes every dependency version.
        Copy-Item -LiteralPath (Join-Path $here 'Cargo.lock') -Destination 'Cargo.lock'

        $env:RUSTFLAGS = '-C target-feature=+crt-static'
        $cargoArgs = @("+$($source.RUST_TOOLCHAIN)", 'build', '--release', '--locked', '--package', $source.RAPIER_FFI_PACKAGE, '--target', 'x86_64-pc-windows-msvc')
        if ($Parallel) { $cargoArgs += @('--features', 'parallel') }
        Invoke-Native cargo $cargoArgs
    } finally { Pop-Location }

    $dll = Join-Path $src 'target/x86_64-pc-windows-msvc/release/rapier3d_ffi.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'The build finished but produced no rapier3d_ffi.dll.' }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $name = if ($Parallel) { 'rapier-mt.dll' } else { 'rapier.dll' }
    $target = Join-Path $OutDir $name
    Copy-Item -LiteralPath $dll -Destination $target -Force
    Write-Host "built $target ($((Get-Item -LiteralPath $target).Length) bytes)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
