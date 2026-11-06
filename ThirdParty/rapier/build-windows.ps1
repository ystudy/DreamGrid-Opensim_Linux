<#
Builds rapier.dll (Rapier's C library, 3D, single precision, parallel) for 64-bit Windows from the commit in source.env.

  build-windows.ps1 -OutDir <directory>

OutDir receives rapier.dll. The C runtime is linked in statically (crt-static), so the DLL imports only KERNEL32, ntdll
and api-ms-win-core-* style system DLLs, not VCRUNTIME140 or MSVCP140.
Needs git, Rust/Cargo 1.99 or newer with the x86_64-pc-windows-msvc target, and Visual Studio 2022 (or its Build Tools)
with the C++ workload for the linker.
#>
param(
    [Parameter(Mandatory)] [string] $OutDir
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

        $env:RUSTFLAGS = '-C target-feature=+crt-static'
        Invoke-Native cargo @('build', '--release', '--package', $source.RAPIER_FFI_PACKAGE, '--features', 'parallel', '--target', 'x86_64-pc-windows-msvc')
    } finally { Pop-Location }

    $dll = Join-Path $src 'target/x86_64-pc-windows-msvc/release/rapier3d_ffi.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'The build finished but produced no rapier3d_ffi.dll.' }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $target = Join-Path $OutDir 'rapier.dll'
    Copy-Item -LiteralPath $dll -Destination $target -Force
    Write-Host "built $target ($((Get-Item -LiteralPath $target).Length) bytes)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
