<#
.SYNOPSIS
    End-to-end test of the installer on a throwaway Windows machine (CI: windows-latest).

.DESCRIPTION
    Starts the server from this checkout on localhost with a temporary database, completes the setup
    wizard over HTTP and drives the installers in parameter mode:

        PowerShell install (an installation from before D22)
        -> vigil_setup update (migration: same key, no pairing)
        -> broken installs (verify must fail; update repairs)
        -> vigil_setup update-config with re-pairing
        -> vigil_setup uninstall (goodbye)
        -> vigil_setup install
        -> the guardian (vigil_setup.exe guardian, #65): five repairs in one run, then a stopped service
        -> C:\ProgramData\Vigil deleted with the watchdog stopped (the README's emergency path from WinRE)
        -> Cleanup.cmd (vigil_setup uninstall) -> nothing left
        -> the wizard (key script): unreachable address, wrong code, install -> verify
        -> the menu (key script): check, uninstall -> nothing left

    Verify is vigil_setup.exe's. The PowerShell one (installer\vigil_install.ps1, the oracle until #64 proved
    both equal at every point) checks only the PowerShell install: it does not know the C# guardian (#65).

    Server side, through the admin API: the PC comes online after each install, keeps its key through an
    update, comes back with the new key after re-pairing, every guardian repair reaches the journal as
    tamper_guardian_repaired, and an uninstall reaches the server as a normal service stop (goodbye ->
    tamper_service_stopped), not as a killed watchdog.

    Only for a disposable machine: it installs the service, sets the per-user policies on -ChildUsername
    and changes the boot menu policy.

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
$Installer = (Resolve-Path (Join-Path $PSScriptRoot '..\installer\vigil_install.ps1')).Path
$PsGuardian = (Resolve-Path (Join-Path $PSScriptRoot '..\installer\vigil_guardian.ps1')).Path
# Not $Setup: PowerShell names are case-insensitive and the scenario has a $setup (the wizard's JSON).
$SetupExe = Join-Path $PackageDir 'vigil_setup.exe'
$InstallDir = 'C:\ProgramData\Vigil'
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
    # The PowerShell installer as Install.cmd ran it before D22 (Windows PowerShell 5.1), on the package's
    # binaries, but -NonInteractive: a question fails instead of hanging. No param() on purpose: the
    # installer's own parameters (-Mode ...) stay in $args.
    Write-Host "> vigil_install.ps1 $($args -join ' ')" -ForegroundColor DarkGray
    & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Installer @args `
        -SourceDir $PsPackageDir | Out-Host
    return $LASTEXITCODE
}

function Invoke-Setup {
    # vigil_setup.exe from the package, parameter mode (input redirected: the wizard can never start).
    Write-Host "> vigil_setup.exe $($args -join ' ')" -ForegroundColor DarkGray
    $null | & $SetupExe @args | Out-Host
    return $LASTEXITCODE
}

function Invoke-Screens([string]$What, [string[]]$Keys) {
    # vigil_setup.exe without -Mode: the wizard or the menu, driven by a key script (-Keys, Screens/Scripted.cs).
    # The screens go to the CI log as plain text. A script that runs out ends the run with exit code 3.
    $file = Join-Path $WorkDir "keys-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content -Path $file -Value $Keys -Encoding UTF8
    Write-Host "> vigil_setup.exe -Keys ($($Keys -join ' | '))" -ForegroundColor DarkGray
    $null | & $SetupExe -Keys $file -ChildUsername $ChildUsername | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) "${What}: exit code 0 (got $LASTEXITCODE)"
}

function Assert-Setup([string]$What) {
    # vigil_setup.exe with the remaining arguments; exit code 0 expected.
    $rc = Invoke-Setup @args
    Assert-That ($rc -eq 0) "${What}: exit code 0 (got $rc)"
}

function Assert-Verify([int]$Expected, [string]$What, [switch]$WithOracle) {
    $cs = Invoke-Setup -Mode verify
    if ($WithOracle) {
        $ps = Invoke-Installer -Mode verify
        Assert-That ($ps -eq $Expected -and $cs -eq $Expected) `
            "${What}: verify exit code $Expected (vigil_install.ps1 $ps, vigil_setup.exe $cs)"
    } else {
        Assert-That ($cs -eq $Expected) "${What}: verify exit code $Expected (got $cs)"
    }
}

function Invoke-Guardian([string]$What) {
    # One run of the task as Windows runs it (SYSTEM); returns when it has finished. Returns its start time.
    $task = 'Vigil Guardian'
    $start = Get-Date
    Start-ScheduledTask -TaskName $task
    Wait-Until {
        (Get-ScheduledTaskInfo -TaskName $task).LastRunTime -ge $start.AddSeconds(-2) -and
        (Get-ScheduledTask -TaskName $task).State -ne 'Running'
    } 180 "${What}: the guardian run finished"
    return $start
}

function Get-GuardianEvents([datetime]$Since) {
    @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Vigil Guardian'; StartTime = $Since } `
        -ErrorAction SilentlyContinue)
}

function Get-GuardianRepairs {
    # The checks the server has as repaired (tamper_guardian_repaired, details.repaired).
    $days = (Invoke-Admin Get 'journal?days=2').days
    @($days | ForEach-Object { $_.entries } |
        Where-Object { $_.type -eq 'tamper_guardian_repaired' -and $_.details.repaired } |
        ForEach-Object { $_.details.check })
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

function Wait-Heartbeat([string]$What) {
    # A heartbeat newer than the service restart: the device is online with whatever key it has now.
    Start-Sleep -Seconds 10
    Wait-Until { (Get-Device).last_heartbeat_ago_seconds -le 8 } 60 $What
}

function Get-KeyHash {
    # signing.dat as stored (DPAPI output is randomized: the same bytes mean the file was not rewritten).
    # Administrators may read it (secret ACL: SYSTEM full, Administrators read).
    (Get-FileHash -Path (Join-Path $InstallDir 'signing.dat') -Algorithm SHA256).Hash
}

function Invoke-AsSystem([string]$Command, [scriptblock]$Until, [int]$Seconds, [string]$What) {
    # cmd.exe /c <Command> as SYSTEM through a one-off task (as WinRE's cmd would have the rights to).
    $name = "vigil_e2e_$PID"
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c $Command"
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $name
        Wait-Until $Until $Seconds $What
    } finally {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Get-PairedCount {
    $days = (Invoke-Admin Get 'journal?days=2').days
    @($days | ForEach-Object { $_.entries } | Where-Object { $_.type -eq 'device_paired' }).Count
}

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

    Write-Section "PowerShell install, as before D22 (child account: $ChildUsername)"
    # The package of those days: the binaries and the PowerShell guardian (not in dist\ since #65).
    $script:PsPackageDir = Join-Path $WorkDir 'package-ps'
    New-Item -ItemType Directory -Path $PsPackageDir -Force | Out-Null
    Copy-Item -Path (Join-Path $PackageDir '*') -Destination $PsPackageDir -Recurse
    Copy-Item -Path $PsGuardian -Destination $PsPackageDir
    New-ChildAccount $ChildUsername
    $code = New-PairingCode @{ child_id = 1; device_name = 'CI runner' }
    $rc = Invoke-Installer -Mode install -ServerUrl $ServerUrl -PairingCode $code -ChildUsername $ChildUsername
    Assert-That ($rc -eq 0) "PowerShell install exit code 0 (got $rc)"
    Wait-Until { (Get-Device).online } 60 'the PC is online (signed heartbeats accepted)'
    Assert-Verify 0 'PowerShell install (vigil_setup verify: the old guardian is a warning)' -WithOracle

    Write-Section 'vigil_setup update over the PowerShell install (migration, same key)'
    $keyBefore = Get-KeyHash
    Assert-Setup 'update' -Mode update
    Assert-That ((Get-KeyHash) -eq $keyBefore) 'signing.dat kept as it was'
    Wait-Heartbeat 'heartbeats with the same key after the update'
    Assert-That (@((Invoke-Admin Get 'status').child.devices).Count -eq 1) 'still one device (no new pairing)'
    Assert-Verify 0 'after the update'
    Assert-That (-not (Test-Path (Join-Path $InstallDir 'vigil_guardian.ps1'))) 'the PowerShell guardian removed'
    $action = (Get-ScheduledTask -TaskName 'Vigil Guardian').Actions[0]
    Assert-That ($action.Execute -eq (Join-Path $InstallDir 'vigil_setup.exe') -and $action.Arguments -eq 'guardian') `
        "the guardian task runs vigil_setup.exe guardian (got $($action.Execute) $($action.Arguments))"

    Write-Section 'Verify must fail on a broken install; update repairs it'
    $safeBoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
    Remove-Item -Path $safeBoot -Recurse -Force
    Assert-Verify 1 'without SafeBoot\Network'
    Assert-Setup 'update as a repair' -Mode update
    Assert-Verify 0 'repaired by update'
    # Not the service's Start value: the SCM keeps its own copy until a reboot, and admins may not change
    # the service config (DACL). A file admins may delete (folder: Administrators modify).
    $backup = Join-Path $InstallDir 'backup\vigil_watchdog.exe'
    Remove-Item -Path $backup -Force
    try { Assert-Verify 1 'without backup\vigil_watchdog.exe' }
    finally { Copy-Item -Path (Join-Path $PackageDir 'vigil_watchdog.exe') -Destination $backup }
    Assert-Verify 0 'repaired again'

    Write-Section 'Re-pair (vigil_setup update-config)'
    $keyBefore = Get-KeyHash
    $repairStart = Get-Date
    $code = New-PairingCode @{ device_id = (Get-Device).id }
    Assert-Setup 'update-config' -Mode update-config -PairingCode $code
    Assert-That ((Get-KeyHash) -ne $keyBefore) 'signing.dat rewritten with the new key'
    # The old key stopped working when the code was used, before the service restarted; a heartbeat
    # newer than that can only be signed with the new key.
    Wait-Heartbeat 'heartbeats with the new key'
    # The old key dies when the code is used, before the stop: the refused goodbye is expected, not a
    # warning (it showed in every verify after re-pairing, VM 1 of #64).
    $goodbyeWarnings = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Application'; ProviderName = 'Vigil Watchdog'; Level = 2, 3; StartTime = $repairStart
        } -ErrorAction SilentlyContinue | Where-Object { $_.Message -like '*Goodbye not delivered*' }).Count
    Assert-That ($goodbyeWarnings -eq 0) "no goodbye warning after re-pairing (got $goodbyeWarnings)"
    Assert-Verify 0 'after re-pairing'

    Write-Section 'vigil_setup uninstall'
    $stopsBefore = Get-ServiceStopCount
    Assert-Setup 'uninstall' -Mode uninstall
    Wait-Until { (Get-ServiceStopCount) -gt $stopsBefore } 20 'the server logged a normal service stop (goodbye)'
    Assert-Verify 0 'after uninstall (nothing left)'

    Write-Section 'vigil_setup install'
    $code = New-PairingCode @{ device_id = (Get-Device).id }
    Assert-Setup 'install' -Mode install -ServerUrl $ServerUrl -PairingCode $code -ChildUsername $ChildUsername
    Wait-Heartbeat 'the PC is online again'
    Assert-Verify 0 'vigil_setup install'

    Write-Section 'Guardian: five repairs in one run'
    $reportedBefore = (Get-GuardianRepairs).Count
    $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\vigil_watchdog'
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vigil_watchdog.exe'
    # A replaced binary: stopped as SYSTEM (admins may not stop it), then one byte appended (admins may write
    # the folder). The rest as an elevated child would: registry values admins may change.
    Invoke-AsSystem 'sc.exe stop vigil_watchdog' { (Get-Service vigil_watchdog).Status -eq 'Stopped' } 30 `
        'the watchdog stopped (as SYSTEM)'
    [IO.File]::AppendAllText((Join-Path $InstallDir 'vigil_watchdog.exe'), 'x')
    New-Item -Path $ifeo -Force | Out-Null
    Set-ItemProperty -Path $ifeo -Name Debugger -Value 'C:\Windows\System32\systray.exe'
    Set-ItemProperty -Path $svcKey -Name Start -Value 4
    Remove-ItemProperty -Path $svcKey -Name FailureActions
    Remove-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog' -Recurse -Force
    Assert-Verify 1 'tampered five ways'
    $since = Invoke-Guardian 'five repairs'
    $events = Get-GuardianEvents $since
    foreach ($id in 7003, 7004, 7002, 7006, 7005) {
        Assert-That (@($events | Where-Object { $_.Id -eq $id -and $_.Level -eq 3 }).Count -eq 1) `
            "event $id as a repair (Warning)"
    }
    Assert-That (@($events | Where-Object { $_.Level -le 2 }).Count -eq 0) 'no failed repair or crash (Error)'
    Assert-That (@($events | Where-Object { $_.Id -eq 7001 }).Count -eq 0) 'no 7001: the binary repair started the service'
    Assert-That ((Get-ItemProperty -Path $ifeo -Name Debugger -ErrorAction SilentlyContinue) -eq $null) 'IFEO Debugger gone'
    Assert-Verify 0 'repaired by the guardian'
    Wait-Until { (Get-GuardianRepairs).Count -ge $reportedBefore + 5 } 180 'the watchdog reported five repairs'
    $checks = Get-GuardianRepairs
    foreach ($check in 'binary_replaced', 'ifeo_debugger', 'service_disabled', 'recovery_missing', 'safeboot_missing') {
        Assert-That ($checks -contains $check) "journal: tamper_guardian_repaired $check"
    }

    Write-Section 'Guardian: a stopped service'
    Invoke-AsSystem 'sc.exe stop vigil_watchdog' { (Get-Service vigil_watchdog).Status -eq 'Stopped' } 30 `
        'the watchdog stopped (as SYSTEM)'
    $since = Invoke-Guardian 'stopped service (waits out service recovery, 75 s)'
    $events = Get-GuardianEvents $since
    Assert-That (@($events | Where-Object { $_.Id -eq 7001 -and $_.Level -eq 3 }).Count -eq 1) 'event 7001 as a repair (Warning)'
    Assert-That ((Get-Service vigil_watchdog).Status -eq 'Running') 'the watchdog runs again'
    Wait-Until { (Get-GuardianRepairs) -contains 'service_not_running' } 180 'journal: tamper_guardian_repaired service_not_running'
    Assert-Verify 0 'after the guardian started it'

    Write-Section 'Emergency path: the folder deleted with the watchdog stopped (README, from WinRE)'
    # Offline nothing runs; online the guardian would start the watchdog again or hold vigil_setup.exe open.
    Disable-ScheduledTask -TaskName 'Vigil Guardian' | Out-Null
    Invoke-AsSystem 'sc.exe stop vigil_watchdog' { (Get-Service vigil_watchdog).Status -eq 'Stopped' } 30 `
        'the watchdog stopped (as SYSTEM)'
    Get-Process -Name vigil_ui -ErrorAction SilentlyContinue | Stop-Process -Force
    Invoke-AsSystem "rmdir /s /q `"$InstallDir`"" { -not (Test-Path $InstallDir) } 30 "$InstallDir deleted (as SYSTEM)"
    Assert-Verify 1 'service registered, folder gone'
    # Through Cleanup.cmd (#66): the shortcut must pass the exit code on.
    Write-Host "> Cleanup.cmd" -ForegroundColor DarkGray
    $null | & cmd.exe /c (Join-Path $PackageDir 'Cleanup.cmd') | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) "Cleanup.cmd on what is left: exit code 0 (got $LASTEXITCODE)"
    Assert-Verify 0 'after uninstall (nothing left)'

    Write-Section 'The wizard (key script)'
    $code = New-PairingCode @{ device_id = (Get-Device).id }
    $pairedBefore = Get-PairedCount
    $wrong = ([int]::Parse($code.Substring(0, 1)) + 1) % 10
    $wrong = "$wrong$($code.Substring(1))"
    Invoke-Screens 'wizard install' @(
        '127.0.0.1:1'           # 1: nothing listens there -> the same screen with an error
        '{Esc}'                 #    clear the field
        ($ServerUrl -replace '^http://', '')
        $wrong                  # 2: a wrong code: accepted as 8 digits, used only after the summary
        '{1}'                   # summary: Install -> pairing fails (401), nothing changed -> the code again
        $code
        '{Enter}'               # installed, verify table -> the menu
        '{0}'                   # menu: Exit
    )
    Assert-That ((Get-PairedCount) -eq $pairedBefore + 1) 'paired once: the wrong code changed nothing'
    Wait-Heartbeat 'the PC is online after the wizard'
    Assert-Verify 0 'installed by the wizard'

    Write-Section 'The menu (key script): check, uninstall'
    $stopsBefore = Get-ServiceStopCount
    Invoke-Screens 'menu check and uninstall' @(
        '{1}'                   # Check the installation (full list)
        '{Enter}'               # back to the menu
        '{5}'                   # Uninstall
        '{1}'                   # confirm
        '{Enter}'               # exit
    )
    Wait-Until { (Get-ServiceStopCount) -gt $stopsBefore } 20 'the server logged a normal service stop (goodbye)'
    Assert-Verify 0 'after uninstall from the menu (nothing left)'

    $failed = $false
    Write-Host "`nInstaller e2e: PASSED" -ForegroundColor Green
} finally {
    if ($failed) { Write-Diagnostics }
    if ($script:ServerProc -and -not $script:ServerProc.HasExited) { Stop-Process -Id $script:ServerProc.Id -Force }
}
