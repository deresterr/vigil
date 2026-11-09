<#
.SYNOPSIS
    End-to-end test of the installer on a throwaway Windows machine (CI: windows-latest).

.DESCRIPTION
    Starts the server from this checkout on localhost with a temporary database, completes the setup
    wizard over HTTP and drives the installer from the package:

        install -> verify -> verify must fail on a broken install -> re-pair (update-config) -> verify
        -> uninstall -> verify (nothing left)

    Every verify runs twice: the PowerShell one (the oracle) and vigil_setup.exe's (D22, #63); both must
    give the expected exit code, also on a broken install.

    Server side, through the admin API: the PC comes online after install and again with the new key after
    re-pairing, and the uninstall reaches the server as a normal service stop (goodbye ->
    tamper_service_stopped), not as a killed watchdog.

    The installer is called with the same parameters as Install.cmd, so the C# installer (D22) goes
    through this script unchanged. Only for a disposable machine: it installs the service, sets the
    per-user policies on -ChildUsername and changes the boot menu policy.

.PARAMETER ChildUsername
    Local account created as the child's (administrator, profile without a logon).

.PARAMETER PackageDir
    The dist\ package (build-package.ps1). Default: ..\dist next to this script.

.PARAMETER ServerDir
    The server\ folder of the checkout; its requirements (plus tzdata on Windows) must be installed.
#>

[CmdletBinding()]
param(
    [string] $PackageDir = '',
    [string] $ServerDir = '',
    [int] $Port = 8089,
    [string] $ChildUsername = 'vigilchild'
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside param() defaults (Windows PowerShell 5.1)
if (-not $PackageDir) { $PackageDir = Join-Path $PSScriptRoot '..\dist' }
if (-not $ServerDir) { $ServerDir = Join-Path $PSScriptRoot '..\server' }
$PackageDir = (Resolve-Path $PackageDir).Path
$ServerDir = (Resolve-Path $ServerDir).Path

$ServerUrl = "http://127.0.0.1:$Port"
$Installer = Join-Path $PackageDir 'vigil_install.ps1'
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) "vigil-e2e-$PID"
New-Item -Path $WorkDir -ItemType Directory -Force | Out-Null
$ServerLog = Join-Path $WorkDir 'server.log'

# === Helpers ===

function Write-Section([string]$Msg) { Write-Host "`n=== $Msg" -ForegroundColor Cyan }

function Assert-That([bool]$Condition, [string]$What) {
    if (-not $Condition) { throw "FAILED: $What" }
    Write-Host "  ok: $What" -ForegroundColor Green
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        if (& $Condition) { Write-Host "  ok: $What" -ForegroundColor Green; return }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "FAILED after ${Seconds} s: $What"
}

function Invoke-Installer {
    # As Install.cmd runs it (Windows PowerShell 5.1), but -NonInteractive: a question fails instead of
    # hanging. No param() on purpose: the installer's own parameters (-Mode ...) stay in $args.
    Write-Host "> vigil_install.ps1 $($args -join ' ')" -ForegroundColor DarkGray
    & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Installer @args | Out-Host
    return $LASTEXITCODE
}

function Invoke-Setup {
    # vigil_setup.exe from the package; same parameters as the PowerShell installer.
    Write-Host "> vigil_setup.exe $($args -join ' ')" -ForegroundColor DarkGray
    & (Join-Path $PackageDir 'vigil_setup.exe') @args | Out-Host
    return $LASTEXITCODE
}

function Assert-Verify([int]$Expected, [string]$What) {
    $ps = Invoke-Installer -Mode verify
    $cs = Invoke-Setup -Mode verify
    Assert-That ($ps -eq $Expected -and $cs -eq $Expected) `
        "${What}: verify exit code $Expected (vigil_install.ps1 $ps, vigil_setup.exe $cs)"
}

function Start-Server {
    $env:DATABASE_PATH = Join-Path $WorkDir 'vigil.db'
    $script:ServerProc = Start-Process -FilePath python -WorkingDirectory $ServerDir -NoNewWindow -PassThru `
        -ArgumentList '-m', 'uvicorn', 'app.main:app', '--host', '127.0.0.1', '--port', "$Port", '--workers', '1' `
        -RedirectStandardOutput $ServerLog -RedirectStandardError "$ServerLog.err"
    $deadline = (Get-Date).AddSeconds(60)
    do {
        try {
            if ((Invoke-WebRequest "$ServerUrl/health" -TimeoutSec 2 -UseBasicParsing).StatusCode -eq 200) { return }
        } catch { }
        if ($script:ServerProc.HasExited) { break }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "The server did not start (see the server log below)"
}

function Invoke-Admin {
    param([string]$Method, [string]$Path, $Body)
    $request = @{ Uri = "$ServerUrl/admin/api/$Path"; Method = $Method; WebSession = $script:AdminSession }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Compress); $request.ContentType = 'application/json' }
    Invoke-RestMethod @request
}

function New-PairingCode([hashtable]$Target) {
    # "1234 5678" -> digits only, so the code is one native argument
    (Invoke-Admin Post 'pairing_code' $Target).code -replace '\D', ''
}

function Get-Device { (Invoke-Admin Get 'status').child.devices | Select-Object -First 1 }

function Get-ServiceStopCount {
    $days = (Invoke-Admin Get 'journal?days=2').days
    @($days | ForEach-Object { $_.entries } | Where-Object { $_.type -eq 'tamper_service_stopped' }).Count
}

function New-ChildAccount([string]$Name) {
    # The runner's own account is the built-in Administrator (RID 500), which the installer skips by design.
    # A local administrator like the child's (D12), with a profile created without a logon (CreateProfile):
    # NTUSER.DAT on disk, not loaded, so the installer takes the offline-hive path.
    if (-not (Get-LocalUser -Name $Name -ErrorAction SilentlyContinue)) {
        $password = ConvertTo-SecureString "E2e-$([guid]::NewGuid().ToString('N'))" -AsPlainText -Force
        New-LocalUser -Name $Name -Password $password -AccountNeverExpires | Out-Null
        Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $Name
    }
    $sid = (Get-LocalUser -Name $Name).SID.Value
    Add-Type -Namespace E2e -Name UserEnv -MemberDefinition @'
[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
public static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder path, uint size);
'@
    $path = New-Object System.Text.StringBuilder 260
    $hr = [E2e.UserEnv]::CreateProfile($sid, $Name, $path, 260)
    # 0x800700B7 = ERROR_ALREADY_EXISTS (a rerun on the same machine)
    if ($hr -ne 0 -and $hr -ne -2147024713) { throw ('CreateProfile failed: 0x{0:X8}' -f $hr) }
    $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid").ProfileImagePath
    Assert-That (Test-Path (Join-Path $profilePath 'NTUSER.DAT')) "child account '$Name' with a profile ($profilePath)"
}

function Write-Diagnostics {
    Write-Section 'Diagnostics'
    foreach ($log in $ServerLog, "$ServerLog.err") {
        if (Test-Path $log) { Write-Host "--- $log (tail)"; Get-Content $log -Tail 60 | Out-Host }
    }
    foreach ($source in 'Vigil Watchdog', 'Vigil Guardian') {
        Write-Host "--- Event Log: $source"
        try {
            Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source } -MaxEvents 25 -ErrorAction Stop |
                ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) [$($_.LevelDisplayName)] $(($_.Message -split "`n")[0])" } |
                Out-Host
        } catch { Write-Host "  (none)" }
    }
}

# === Scenario ===

$failed = $true
try {
    Write-Section "Server $ServerUrl (database in $WorkDir)"
    Start-Server
    $setup = @{ child_name = 'CI'; password = 'e2e-parent-password'; watchdog_server_url = $ServerUrl } | ConvertTo-Json
    Invoke-RestMethod "$ServerUrl/setup/complete" -Method Post -Body $setup -ContentType 'application/json' `
        -SessionVariable session | Out-Null
    $script:AdminSession = $session
    Assert-That ((Invoke-Admin Get 'status').children.Count -eq 1) 'setup wizard completed, one child'

    Write-Section "Install (child account: $ChildUsername)"
    New-ChildAccount $ChildUsername
    $code = New-PairingCode @{ child_id = 1; device_name = 'CI runner' }
    $rc = Invoke-Installer -Mode install -ServerUrl $ServerUrl -PairingCode $code -ChildUsername $ChildUsername
    Assert-That ($rc -eq 0) "install exit code 0 (got $rc)"
    Wait-Until { (Get-Device).online } 60 'the PC is online (signed heartbeats accepted)'

    Write-Section 'Verify (installed)'
    Assert-Verify 0 'installed'

    Write-Section 'Verify must fail on a broken install'
    $safeBoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
    Remove-Item -Path $safeBoot -Recurse -Force
    try { Assert-Verify 1 'without SafeBoot\Network' }
    finally {
        New-Item -Path $safeBoot -Force | Out-Null
        Set-Item -Path $safeBoot -Value 'Service' -Force
    }
    # Not the service's Start value: the SCM keeps its own copy until a reboot, and admins may not change
    # the service config (DACL). A file admins may delete (folder: Administrators modify).
    $backup = 'C:\ProgramData\Vigil\backup\vigil_watchdog.exe'
    Remove-Item -Path $backup -Force
    try { Assert-Verify 1 'without backup\vigil_watchdog.exe' }
    finally { Copy-Item -Path (Join-Path $PackageDir 'vigil_watchdog.exe') -Destination $backup }
    Assert-Verify 0 'repaired again'

    Write-Section 'Re-pair (update-config)'
    $code = New-PairingCode @{ device_id = (Get-Device).id }
    $rc = Invoke-Installer -Mode update-config -PairingCode $code
    Assert-That ($rc -eq 0) "update-config exit code 0 (got $rc)"
    # The old key stopped working when the code was used, before the service restarted; a heartbeat
    # newer than that wait can only be signed with the new key.
    Start-Sleep -Seconds 10
    Wait-Until { (Get-Device).last_heartbeat_ago_seconds -le 8 } 60 'heartbeats with the new key'
    Assert-Verify 0 'after re-pairing'

    Write-Section 'Uninstall'
    $stopsBefore = Get-ServiceStopCount
    $rc = Invoke-Installer -Mode uninstall
    Assert-That ($rc -eq 0) "uninstall exit code 0 (got $rc)"
    Wait-Until { (Get-ServiceStopCount) -gt $stopsBefore } 20 'the server logged a normal service stop (goodbye)'

    Write-Section 'Verify (removed)'
    Assert-Verify 0 'after uninstall'

    $failed = $false
    Write-Host "`nInstaller e2e: PASSED" -ForegroundColor Green
} finally {
    if ($failed) { Write-Diagnostics }
    if ($script:ServerProc -and -not $script:ServerProc.HasExited) { Stop-Process -Id $script:ServerProc.Id -Force }
}
