<#
.SYNOPSIS  Install smoke for the Windows installer (DreamGridSetup-<version>.exe).
.DESCRIPTION
  Runs on a disposable Windows 10/11 x64 machine from an elevated PowerShell (CI: windows-latest), since it installs,
  upgrades and uninstalls the DreamGrid service for real. -InstallerV2 is the same build with another -Version (the
  upgrade step). When its runtime version differs too, the upgrade stage also checks that the runtime the adopted region
  still uses is kept.
    INSTALL_SMOKE_DIR  a published tests/DreamGrid.InstallSmoke (default: published here with dotnet).
    SMOKE_LOGS         a directory that receives the setup logs and the launcher's logs when a stage fails.
  Prints one line per stage; exits 0 when every stage passed, 1 at the first failing stage. Runs on Windows PowerShell 5.1.
.EXAMPLE   powershell -File setup/windows/test/smoke.ps1 -Installer DreamGridSetup-0.0.1.exe -InstallerV2 DreamGridSetup-0.0.2.exe
#>
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$InstallerV2
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$data = Join-Path $env:ProgramData 'DreamGrid'
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\DreamGrid'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\DreamGrid admin page.url'
$work = Join-Path ([IO.Path]::GetTempPath()) ('dreamgrid-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$script:stage = 'prepare'

function Ok([string]$detail) { Write-Host "OK   ${script:stage}: $detail" }

function Fail([string]$detail) {
    Write-Host "FAIL ${script:stage}: $detail"
    $launcherLog = Join-Path $data 'state\logs\launcher.log'
    if (Test-Path -LiteralPath $launcherLog) {
        Write-Host '--- launcher.log ---'
        Get-Content -LiteralPath $launcherLog -Tail 100 | Write-Host
    }
    if ($env:SMOKE_LOGS) {
        New-Item -ItemType Directory -Force $env:SMOKE_LOGS | Out-Null
        Get-ChildItem -LiteralPath $work -Filter '*.log' | Copy-Item -Destination $env:SMOKE_LOGS
        $logs = Join-Path $data 'state\logs'
        if (Test-Path -LiteralPath $logs) { Copy-Item -Recurse -Force $logs (Join-Path $env:SMOKE_LOGS 'logs-windows') }
    }
    exit 1
}

# Runs an installer or uninstaller silently and returns its exit code; its log is $work\<Name>.log.
function Invoke-Setup([string]$Exe, [string]$Name) {
    $log = Join-Path $work "$Name.log"
    # /NORESTART: a reboot request must show up in the log and fail the stage, never reboot the machine.
    $arguments = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`""
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -Wait -PassThru
    return $process.ExitCode
}

function Read-SetupLog([string]$Name) {
    $log = Join-Path $work "$Name.log"
    if (Test-Path -LiteralPath $log) { return Get-Content -LiteralPath $log -Raw }
    return ''
}

# Polls $Check every second until it returns true or $Seconds pass.
function Wait-Until([int]$Seconds, [scriptblock]$Check) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Check)) {
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Seconds 1
    }
    return $true
}

function Test-Port([int]$Port) {
    $client = New-Object Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); return $true }
    catch { return $false }
    finally { $client.Close() }
}

function Get-Http([string]$Path) {
    try { return (Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "http://127.0.0.1:8001$Path").Content }
    catch { return $null }
}

# The installed service: its command line (dotnet.exe, the launcher, the arguments) and where it lives.
function Get-ServiceCommand {
    $image = (Get-ItemProperty -LiteralPath $serviceKey -Name ImagePath).ImagePath
    if ($image -notmatch '^"(?<dotnet>[^"]+\\dotnet-(?<runtime>[^"\\]+)\\dotnet\.exe)" "(?<app>[^"]+)\\launcher\\DreamGrid\.Launcher\.dll"') {
        Fail "the service runs '$image', not the launcher on a versioned dotnet folder"
    }
    return [pscustomobject]@{ Image = $image; Dotnet = $Matches.dotnet; Runtime = $Matches.runtime; App = $Matches.app }
}

function Get-FirewallRules { @(Get-NetFirewallRule -DisplayName 'DreamGrid (dotnet *)' -ErrorAction SilentlyContinue) }

# --- Prepare -----------------------------------------------------------------------------------------------------
foreach ($exe in $Installer, $InstallerV2) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Fail "there is no installer $exe" }
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$InstallerV2 = (Resolve-Path -LiteralPath $InstallerV2).Path
if (Get-Service DreamGrid -ErrorAction SilentlyContinue) { Fail 'a DreamGrid service is already installed; run the smoke on a clean machine' }
if ($env:INSTALL_SMOKE_DIR) { $smokeDir = [IO.Path]::GetFullPath($env:INSTALL_SMOKE_DIR) }
else {
    $smokeDir = Join-Path $work 'installsmoke'
    & dotnet publish (Join-Path $repo 'tests\DreamGrid.InstallSmoke') -c Release -o $smokeDir | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish of DreamGrid.InstallSmoke exited $LASTEXITCODE" }
}
$smokeDll = Join-Path $smokeDir 'DreamGrid.InstallSmoke.dll'
if (-not (Test-Path -LiteralPath $smokeDll)) { Fail "no DreamGrid.InstallSmoke.dll in $smokeDir" }
Ok "installers $([IO.Path]::GetFileName($Installer)) and $([IO.Path]::GetFileName($InstallerV2)); work folder $work"

# --- Busy port (Review Focus 3) ----------------------------------------------------------------------------------
$script:stage = 'busy-port'
$listener = Start-Job {
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, 8001)
    $l.Start()
    while ($true) { Start-Sleep -Seconds 1 }
}
if (-not (Wait-Until 30 { Test-Port 8001 })) { Fail 'the test listener did not take port 8001' }
$code = Invoke-Setup $Installer 'busy-port'
Stop-Job $listener
Remove-Job -Force $listener
if (-not (Wait-Until 30 { -not (Test-Port 8001) })) { Fail 'port 8001 was not released after the test listener stopped' }
if ($code -eq 0) { Fail 'the installer succeeded although port 8001 was taken' }
$sentence = 'Port 8001 is already used by another program.'
if ((Read-SetupLog 'busy-port').IndexOf($sentence, [StringComparison]::Ordinal) -lt 0) { Fail "the setup log does not say '$sentence'" }
if (Get-Service DreamGrid -ErrorAction SilentlyContinue) { Fail 'a service was created although the port was taken' }
Ok "exit $code with the port sentence; no service"

# --- Install -----------------------------------------------------------------------------------------------------
$script:stage = 'install'
$code = Invoke-Setup $Installer 'install'
if ($code -ne 0) { Fail "the installer exited $code" }
$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Fail 'the rest of the smoke reads the data folder and controls the service: run it from an elevated PowerShell' }
$service = Get-Service DreamGrid -ErrorAction SilentlyContinue
if (-not $service) { Fail 'there is no DreamGrid service' }
if (-not (Wait-Until 60 { (Get-Service DreamGrid).Status -eq 'Running' })) { Fail "the service is $((Get-Service DreamGrid).Status), not Running" }
$props = Get-ItemProperty -LiteralPath $serviceKey
if ($props.ObjectName -ne 'NT SERVICE\DreamGrid') { Fail "the service runs as '$($props.ObjectName)', not NT SERVICE\DreamGrid" }
if ($props.Start -ne 2) { Fail "the service start type is $($props.Start), not automatic (2)" }
$command = Get-ServiceCommand
$app = $command.App
$v1 = (Get-Content -LiteralPath (Join-Path $app 'VERSION') -TotalCount 1).Trim()
$expectedArgs = "--data `"$data`" --opensim `"$app\bin`" --mariadb-bin `"$app\mariadb-"
if ($command.Image.IndexOf($expectedArgs, [StringComparison]::OrdinalIgnoreCase) -lt 0) { Fail "the service command lacks '$expectedArgs': $($command.Image)" }
$rules = Get-FirewallRules
if ($rules.Count -ne 1 -or $rules[0].DisplayName -ne "DreamGrid (dotnet $($command.Runtime))") {
    Fail "expected one firewall rule 'DreamGrid (dotnet $($command.Runtime))', found: $(($rules | ForEach-Object DisplayName) -join ', ')"
}
if (-not (Test-Path -LiteralPath $shortcut)) { Fail "there is no Start menu shortcut $shortcut" }
if ((Get-Content -LiteralPath $shortcut -Raw) -notmatch '(?m)^URL=http://localhost:8001/\s*$') {
    Fail "the Start menu shortcut does not point at http://localhost:8001/: $(Get-Content -LiteralPath $shortcut -Raw)"
}
# The website's styles come from launcher\wwwroot, which only the service's content root makes reachable.
if (-not (Wait-Until 60 { $null -ne (Get-Http '/css/site.css') })) { Fail 'the website does not serve /css/site.css' }
$link = (& $command.Dotnet (Join-Path $app 'launcher\DreamGrid.Launcher.dll') admin-link --wait 120) -join "`n"
if ($LASTEXITCODE -ne 0) { Fail "admin-link exited $LASTEXITCODE`: $link" }
if ($link -notmatch '^http://\S+/setup\?token=\S+$') { Fail "admin-link printed '$link', not a setup link" }
Ok "DreamGrid $v1 in $app; service running as NT SERVICE\DreamGrid on dotnet $($command.Runtime); link $link"

# --- Setup, logins, service stop/start with adoption (InstallSmoke) -----------------------------------------------
$script:stage = 'installsmoke'
# sc.exe returns while the service is still stopping or starting; wait for the state, as systemctl does on Debian.
$stop = "sc.exe stop DreamGrid && powershell.exe -NoProfile -Command (Get-Service DreamGrid).WaitForStatus('Stopped','00:01:00')"
$start = "sc.exe start DreamGrid && powershell.exe -NoProfile -Command (Get-Service DreamGrid).WaitForStatus('Running','00:01:00')"
& $command.Dotnet $smokeDll --url http://127.0.0.1:8001 --setup-link $link --data $data --robust-port 8002 --stop $stop --start $start |
    Write-Host
if ($LASTEXITCODE -ne 0) { Fail 'InstallSmoke failed' }
Ok 'InstallSmoke passed'

# --- Upgrade (Review Focus 1 and 5) ------------------------------------------------------------------------------
$script:stage = 'upgrade'
$pidFile = Join-Path $data 'state\pids\Smoke_Island.pid'
$regionPid = [int](Get-Content -LiteralPath $pidFile -TotalCount 1).Trim()
$settingsBefore = (Get-FileHash -LiteralPath (Join-Path $data 'settings.json')).Hash
$runtime1 = $command.Runtime
$code = Invoke-Setup $InstallerV2 'upgrade'
if ($code -ne 0) { Fail "the installer (upgrade) exited $code" }
$log = Read-SetupLog 'upgrade'
# Every Inno Setup log ends with "Need to restart Windows? No|Yes"; a file in use is "replaced on restart".
if ($log -match 'Need to restart Windows\? Yes' -or $log -match 'replace on restart') {
    Fail "the upgrade asked for a restart: $(($log -split "`r?`n" | Select-String -Pattern 'restart' -SimpleMatch) -join ' | ')"
}
$command = Get-ServiceCommand
$v2 = (Get-Content -LiteralPath (Join-Path $command.App 'VERSION') -TotalCount 1).Trim()
if ($v2 -eq $v1) { Fail "both installers are version $v1" }
$footer = $null
Wait-Until 120 {
    $page = Get-Http '/login'
    if ($page -and $page -match '<footer>[^<]*</footer>') { $script:footer = $Matches[0] }
    $script:footer -eq "<footer>DreamGrid $v2</footer>"
} | Out-Null
if ($footer -ne "<footer>DreamGrid $v2</footer>") { Fail "the /login footer is '$footer', not version $v2" }
if (-not (Wait-Until 300 { (Get-Http '/api/status') -match '"loginsEnabled":true' })) { Fail 'logins were not enabled again within 5 minutes' }
$pidAfter = [int](Get-Content -LiteralPath $pidFile -TotalCount 1).Trim()
if ($pidAfter -ne $regionPid) { Fail "the region PID changed from $regionPid to $pidAfter" }
$region = Get-Process -Id $regionPid -ErrorAction SilentlyContinue
if (-not $region) { Fail "the region (PID $regionPid) is gone" }
if ((Get-FileHash -LiteralPath (Join-Path $data 'settings.json')).Hash -ne $settingsBefore) { Fail 'settings.json changed' }
$runtime2 = $command.Runtime
$folder1 = Join-Path $command.App "dotnet-$runtime1"
if ($runtime1 -ne $runtime2) {
    if ($region.Path -ne (Join-Path $folder1 'dotnet.exe')) { Fail "the adopted region runs $($region.Path), not $folder1\dotnet.exe" }
    if (-not (Test-Path -LiteralPath (Join-Path $folder1 'dotnet.exe'))) { Fail "$folder1 was removed while the adopted region still uses it" }
    $wanted = @("DreamGrid (dotnet $runtime1)", "DreamGrid (dotnet $runtime2)")
    $runtimes = "kept dotnet-$runtime1 for the adopted region, launcher on dotnet-$runtime2"
}
else {
    $wanted = @("DreamGrid (dotnet $runtime2)")
    $runtimes = "runtime $runtime2"
}
$names = @(Get-FirewallRules | ForEach-Object DisplayName | Sort-Object)
if (($names -join '|') -ne (($wanted | Sort-Object) -join '|')) { Fail "firewall rules are '$($names -join ', ')', expected '$($wanted -join ', ')'" }
Ok "footer shows $v2; logins enabled; region PID $regionPid unchanged; settings.json byte-identical; no restart; $runtimes"

# --- Uninstall ---------------------------------------------------------------------------------------------------
$script:stage = 'uninstall'
$uninstaller = Join-Path $command.App 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { Fail "there is no uninstaller $uninstaller" }
$code = Invoke-Setup $uninstaller 'uninstall'
if ($code -ne 0) { Fail "the uninstaller exited $code" }
# The uninstaller hands over to a copy of itself in %TEMP%; wait for that copy's work too.
if (-not (Wait-Until 180 { -not (Get-Service DreamGrid -ErrorAction SilentlyContinue) })) { Fail 'the DreamGrid service is still installed' }
if (-not (Wait-Until 60 { -not (Get-Process -Id $regionPid -ErrorAction SilentlyContinue) })) { Fail "the region (PID $regionPid) is still running" }
if (-not (Test-Path -LiteralPath (Join-Path $data 'settings.json'))) { Fail "the data folder $data was not kept" }
$rules = Get-FirewallRules
if ($rules.Count -ne 0) { Fail "firewall rules left behind: $(($rules | ForEach-Object DisplayName) -join ', ')" }
if (Test-Path -LiteralPath $shortcut) { Fail "the Start menu shortcut $shortcut was left behind" }
Ok "service removed; region PID $regionPid ended; $data kept; firewall rules and shortcut removed"

$script:stage = 'finished'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host 'Windows install smoke: all stages passed'
