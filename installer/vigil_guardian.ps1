<#
.SYNOPSIS
    Vigil Guardian: puts back what keeps vigil_watchdog running and reports every repair.

.DESCRIPTION
    Scheduled Task "Vigil Guardian" (SYSTEM, at startup + every 5 min). Threat model: friction +
    detection. An elevated child can disable this task too; until then every change below is undone
    and the watchdog reports it to the parent's journal as tamper_guardian_repaired (#36).

    Checks, in this order (a replaced binary needs a stop/start, the start type must be right before
    starting the service):
      7003 binary_replaced      vigil_watchdog.exe hash != guardian.dat → restore from backup\
      7004 ifeo_debugger        IFEO Debugger on vigil_watchdog.exe → removed
      7002 service_disabled     start type not automatic → automatic
      7006 recovery_missing     no restart actions → the installer's ones
      7005 safeboot_missing     SafeBoot Minimal/Network entries → recreated
      7001 service_not_running  still stopped after the recovery delay → started

    Event Log (Application, source "Vigil Guardian"): those IDs are the contract with the watchdog
    (client/watchdog/Guardian/GuardianReporter.cs). Warning = repaired, Error = repair failed.
    Other IDs (1xxx, 9999) are diagnostics only.
#>

[CmdletBinding()]
param(
    [string]$ProgramDataRoot = "C:\ProgramData\Vigil",
    [string]$ServiceName     = "vigil_watchdog",
    [string]$EventSource     = "Vigil Guardian"
)

$ErrorActionPreference = 'Continue'  # one failed check must not stop the others

$watchdogExePath    = Join-Path $ProgramDataRoot "vigil_watchdog.exe"
$guardianDatPath    = Join-Path $ProgramDataRoot "guardian.dat"
$watchdogBackupPath = Join-Path $ProgramDataRoot "backup\vigil_watchdog.exe"
$serviceKey         = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$ifeoKey            = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vigil_watchdog.exe'
$safeBootKeys       = @(
    "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\$ServiceName",
    "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\$ServiceName"
)
# Service recovery restarts the watchdog 60 s after a crash or kill; wait that out before calling a
# stopped service tampering.
$RecoveryWaitSeconds = 75

# === Event Log ===

function Initialize-EventSource {
    if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
        try { New-EventLog -LogName Application -Source $EventSource -ErrorAction Stop }
        catch { Write-Host "Could not create the event source: $_" }
    }
}

function Write-GuardianEvent {
    param(
        [Parameter(Mandatory)] [string] $Message,
        [ValidateSet('Information','Warning','Error')] [string] $EntryType = 'Information',
        [int] $EventId = 1000
    )
    try {
        Write-EventLog -LogName Application -Source $EventSource `
            -EntryType $EntryType -EventId $EventId -Message $Message -ErrorAction Stop
    } catch { }
    Write-Host "[$EntryType $EventId] $Message"  # Task Scheduler history
}

# Runs $Repair and logs the finding for the watchdog: Warning if it worked, Error if it threw.
function Repair-Finding {
    param([int] $EventId, [string] $What, [scriptblock] $Repair)
    try {
        & $Repair
        Write-GuardianEvent -EntryType Warning -EventId $EventId -Message "$What — naprawione"
    } catch {
        Write-GuardianEvent -EntryType Error -EventId $EventId -Message "$What — naprawa nie powiodła się: $_"
    }
}

# === Checks ===

function Get-ReferenceHash {
    # guardian.dat: SHA256 hex of vigil_watchdog.exe, DPAPI LocalMachine, written by the installer.
    if (-not (Test-Path $guardianDatPath)) { return $null }
    try {
        Add-Type -AssemblyName System.Security
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            [System.IO.File]::ReadAllBytes($guardianDatPath), $null,
            [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        return ([System.Text.Encoding]::UTF8.GetString($plain)).Trim()
    } catch {
        Write-GuardianEvent -EntryType Warning -EventId 1003 -Message "guardian.dat unreadable: $_"
        return $null
    }
}

function Test-BinaryIntact {
    $reference = Get-ReferenceHash
    if (-not $reference) { return $true }  # no reference: better no check than a false alert
    if (-not (Test-Path $watchdogExePath)) { return $false }
    return ((Get-FileHash -Path $watchdogExePath -Algorithm SHA256).Hash -eq $reference)
}

function Restore-Binary {
    if (-not (Test-Path $watchdogBackupPath)) { throw "brak kopii $watchdogBackupPath" }
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue  # file lock
    Start-Sleep -Seconds 2
    Copy-Item -Path $watchdogBackupPath -Destination $watchdogExePath -Force -ErrorAction Stop
    Start-Service -Name $ServiceName -ErrorAction Stop
}

function Test-IfeoClean {
    if (-not (Test-Path $ifeoKey)) { return $true }
    $debugger = (Get-ItemProperty -Path $ifeoKey -Name 'Debugger' -ErrorAction SilentlyContinue).Debugger
    return [string]::IsNullOrEmpty($debugger)
}

function Test-StartAutomatic {
    # Start: 2 = automatic (installer: New-Service -StartupType Automatic)
    return ((Get-ItemProperty -Path $serviceKey -Name 'Start' -ErrorAction SilentlyContinue).Start -eq 2)
}

function Test-ServiceRecovery {
    $output = & sc.exe qfailure $ServiceName 2>&1 | Out-String
    return ($output -match 'RESTART')
}

function Set-ServiceRecovery {
    # Same as Step-RegisterService in vigil_install.ps1.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure: exit $LASTEXITCODE" }
    & sc.exe failureflag $ServiceName 1 | Out-Null
}

function Test-SafeBootEntries {
    foreach ($key in $safeBootKeys) {
        if (-not (Test-Path $key)) { return $false }
        if ((Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).'(default)' -ne 'Service') { return $false }
    }
    return $true
}

function Set-SafeBootEntries {
    foreach ($key in $safeBootKeys) {
        if (-not (Test-Path $key)) { New-Item -Path $key -Force -ErrorAction Stop | Out-Null }
        Set-Item -Path $key -Value 'Service' -Force -ErrorAction Stop  # the key's default value
    }
}

function Test-ServiceUp {
    $status = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
    return ($status -eq 'Running' -or $status -eq 'StartPending')
}

function Start-WatchdogService {
    Start-Service -Name $ServiceName -ErrorAction Stop
    Start-Sleep -Seconds 5
    if (-not (Test-ServiceUp)) { throw "usługa nie wstała" }
}

# === Main ===

function Invoke-Guardian {
    Initialize-EventSource

    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        # Nothing to repair without the service; reinstalling is the installer's job.
        Write-GuardianEvent -EntryType Error -EventId 1099 -Message "Usługa $ServiceName nie istnieje"
        return
    }

    if (-not (Test-BinaryIntact)) {
        Repair-Finding 7003 "Plik vigil_watchdog.exe był podmieniony" { Restore-Binary }
    }
    if (-not (Test-IfeoClean)) {
        Repair-Finding 7004 "IFEO Debugger na vigil_watchdog.exe" {
            Remove-ItemProperty -Path $ifeoKey -Name 'Debugger' -ErrorAction Stop
        }
    }
    if (-not (Test-StartAutomatic)) {
        Repair-Finding 7002 "Usługa wyłączona z autostartu" {
            & sc.exe config $ServiceName start= auto | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "sc.exe config: exit $LASTEXITCODE" }
        }
    }
    if (-not (Test-ServiceRecovery)) {
        Repair-Finding 7006 "Brak automatycznego restartu usługi" { Set-ServiceRecovery }
    }
    if (-not (Test-SafeBootEntries)) {
        Repair-Finding 7005 "Brak wpisów SafeBoot" { Set-SafeBootEntries }
    }
    if (-not (Test-ServiceUp)) {
        # A crash or kill is restarted by service recovery after 60 s; update-config stops it briefly.
        Start-Sleep -Seconds $RecoveryWaitSeconds
        if (-not (Test-ServiceUp)) {
            Repair-Finding 7001 "Usługa była zatrzymana" { Start-WatchdogService }
        }
    }
}

try {
    Invoke-Guardian
} catch {
    Initialize-EventSource
    Write-GuardianEvent -EntryType Error -EventId 9999 -Message "Guardian crash: $_`n$($_.ScriptStackTrace)"
    exit 1
}
