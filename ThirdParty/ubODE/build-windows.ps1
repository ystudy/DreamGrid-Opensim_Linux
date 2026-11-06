<#
Builds ubode.dll (OpenSim's ubODE physics library) for 64-bit Windows from opensim-libs, with this folder's patch applied.

  build-windows.ps1 -Output bin\lib64\ubode.dll [-Patch <file>]

-Patch defaults to 0001-*.patch beside this script; pass check-old-vs-new-preprocess.patch for the test build.
Needs git and Visual Studio 2022 (or its Build Tools) with the C++ workload. Uses ubODE's own premake4.exe, as its
OPENSIM-README.txt describes, but generates a VS2010 project (MSBuild can still build those) instead of VS2008.
#>
param(
    [Parameter(Mandatory)] [string] $Output,
    [string] $Patch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$source = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'source.env')) {
    if ($line -match '^\s*([A-Z_]+)=(.*)$') { $source[$Matches[1]] = $Matches[2].Trim() }
}
if (-not $Patch) { $Patch = (Get-ChildItem -LiteralPath $here -Filter '0001-*.patch' | Select-Object -First 1).FullName }
$Patch = (Resolve-Path -LiteralPath $Patch).Path
$Output = [System.IO.Path]::GetFullPath($Output)

function Invoke-Native([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') exited with $LASTEXITCODE" }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ubode-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $src = Join-Path $work 'src'
    Invoke-Native git @('-c', 'init.defaultBranch=main', 'init', '-q', $src)
    Push-Location $src
    try {
        Invoke-Native git @('config', 'core.autocrlf', 'false')
        Invoke-Native git @('remote', 'add', 'origin', $source.OPENSIM_LIBS_REPO)
        Invoke-Native git @('fetch', '-q', '--depth', '1', '--filter=blob:none', 'origin', $source.OPENSIM_LIBS_COMMIT)
        Invoke-Native git @('sparse-checkout', 'set', $source.UBODE_PATH)
        Invoke-Native git @('checkout', '-q', 'FETCH_HEAD')
        Invoke-Native git @('apply', '--check', $Patch)
        Invoke-Native git @('apply', $Patch)
    } finally { Pop-Location }
    Write-Host "opensim-libs $($source.OPENSIM_LIBS_COMMIT) + $(Split-Path -Leaf $Patch)"

    $build = Join-Path $src ($source.UBODE_PATH + '/build')
    Push-Location $build
    try {
        # ubODE's OPENSIM-README.txt: premake4 --only-single --platform=x64 --no-threading-intf vs2008. Single precision
        # is premake4.lua's only choice, so --only-single is not an option it knows; --only-shared builds just the DLL.
        Invoke-Native (Join-Path $build 'premake4.exe') @('--only-shared', '--platform=x64', '--no-threading-intf', 'vs2010')
    } finally { Pop-Location }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild was not found (needs Visual Studio 2022 or its Build Tools with the C++ workload).' }
    $sln = Join-Path $build 'vs2010\ode.sln'
    Invoke-Native $msbuild @($sln, '/m', '/nologo', '/v:minimal', '/p:Configuration=Release', '/p:Platform=x64',
        '/p:PlatformToolset=v143', '/p:WindowsTargetPlatformVersion=10.0')

    $dll = Get-ChildItem -LiteralPath (Join-Path $src ($source.UBODE_PATH + '/lib')) -Recurse -Filter 'ode.dll' | Select-Object -First 1
    if (-not $dll) { throw 'The build finished but produced no ode.dll.' }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
    Copy-Item -LiteralPath $dll.FullName -Destination $Output -Force
    Write-Host "built $Output ($((Get-Item -LiteralPath $Output).Length) bytes)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
