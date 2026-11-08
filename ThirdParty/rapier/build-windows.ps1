<#
Builds Rapier's C library (3D, single precision) for 64-bit Windows from the commit in source.env.

  build-windows.ps1 -OutDir <directory> [-Parallel]

Without -Parallel the library is built without Rapier's parallel feature and OutDir receives rapier.dll, which OpenSim
loads when RapierNumThreads is 1. With -Parallel it is built with --features parallel (rayon) and OutDir receives
rapier-mt.dll, which OpenSim loads only when RapierNumThreads is more than 1.
The C runtime is linked in statically (crt-static), so the DLL imports only KERNEL32, ntdll
and api-ms-win-core-* style system DLLs, not VCRUNTIME140 or MSVCP140.
Parry (Rapier's collision library) is downloaded from crates.io, checked against PARRY_CRATE_SHA256 in source.env, patched
with patches/parry3d-<version>-edge-codes.patch (the build stops if it does not apply) and tested with edge-codes-test
before Rapier is built against it.
Needs git, tar (Windows 10 and later have it), rustup with the toolchain named in source.env (RUST_TOOLCHAIN) and its x86_64-pc-windows-msvc target, and Visual Studio 2022 (or its Build Tools)
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
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $source[$Matches[1]] = $Matches[2].Trim() }
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

        # The patched Parry, outside the Rapier checkout (git apply works relative to an enclosing repository).
        $version = $source.PARRY_VERSION
        $crate = Join-Path $work 'parry3d.crate'
        Invoke-WebRequest -UseBasicParsing -Uri "https://static.crates.io/crates/parry3d/parry3d-$version.crate" -OutFile $crate
        $hash = (Get-FileHash -LiteralPath $crate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $source.PARRY_CRATE_SHA256) { throw "parry3d-$version.crate has SHA-256 $hash, not $($source.PARRY_CRATE_SHA256) (source.env)." }
        # Windows' own bsdtar: a GNU tar earlier on PATH (Git's) would read "C:" in the paths as a remote host.
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tar)) { $tar = 'tar' }
        Invoke-Native $tar @('-xzf', $crate, '-C', $work)
        $parry = Join-Path $work "parry3d-$version"
        $patch = Join-Path $here "patches/parry3d-$version-edge-codes.patch"
        Push-Location $parry
        try {
            try { Invoke-Native git @('apply', '--verbose', $patch) }
            catch { throw "patches/parry3d-$version-edge-codes.patch does not apply to parry3d $version`: $_" }
        } finally { Pop-Location }
        $patchParry = "patch.crates-io.parry3d.path='$($parry -replace '\\', '/')'"
        $env:CARGO_TARGET_DIR = Join-Path $work 'edge-codes-test'
        try {
            Invoke-Native cargo @("+$($source.RUST_TOOLCHAIN)", 'test', '--release', '--locked',
                '--manifest-path', (Join-Path $here 'edge-codes-test/Cargo.toml'), '--config', $patchParry)
        } finally { Remove-Item Env:CARGO_TARGET_DIR }

        $env:RUSTFLAGS = '-C target-feature=+crt-static'
        $cargoArgs = @("+$($source.RUST_TOOLCHAIN)", 'build', '--release', '--locked', '--config', $patchParry, '--package', $source.RAPIER_FFI_PACKAGE, '--target', 'x86_64-pc-windows-msvc')
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
