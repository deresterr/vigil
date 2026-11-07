<#
.SYNOPSIS
    Drugi watchdog dla vigil_watchdog. Sprawdza integralność i odtwarza kompromitowane elementy.

.DESCRIPTION
    Uruchamiany przez Scheduled Task jako SYSTEM, trigger: At startup + co 5 minut.
    Sprawdza:
      1. Service vigil_watchdog istnieje
      2. Status == Running (3 retry restart przed reboot)
      3. Hash vigil_watchdog.exe vs reference w DPAPI-protected guardian.dat
      4. IFEO debugger key (HKLM Image File Execution Options) — nie powinien być
      5. SafeBoot entries — czy istnieją (żeby vigil_watchdog działał w safe mode)
      6. Service Recovery config — czy ma restart actions

    Wszystkie naprawcze działania logowane do Windows Event Log:
      Source: Vigil Guardian
      Log:    Application

    Instalacja Scheduled Task — patrz vigil_install.ps1 / Install.cmd, tutaj tylko sam script.
#>

[CmdletBinding()]
param(
    [string]$ProgramDataRoot = "C:\ProgramData\Vigil",
    [string]$ServiceName     = "vigil_watchdog",
    [string]$WatchdogExeName = "vigil_watchdog.exe",
    [string]$EventSource     = "Vigil Guardian",
    [int]   $MaxRestartRetries = 3,
    [int]   $RebootDelaySeconds = 60,

    # Tryb dry-run — sprawdza wszystko i loguje, ale nie wykonuje napraw.
    # Przydatne do testowania bez ryzyka reboota.
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'  # nie chcemy żeby pojedynczy błąd zabił cały check

# === Stałe lokalne ===

$watchdogExePath = Join-Path $ProgramDataRoot $WatchdogExeName
$guardianDatPath = Join-Path $ProgramDataRoot "guardian.dat"
$watchdogBackupPath = Join-Path $ProgramDataRoot "backup\vigil_watchdog.exe"

# Counter persistence — ile restartów z rzędu nie pomogło.
# Nie chcemy reboota po pierwszym false-positive (np. timing race przy boot).
$restartCounterPath = Join-Path $ProgramDataRoot "guardian_restart_counter.txt"

# SafeBoot keys (Network = NetworkWindows, Minimal = SafeWindows)
$safeBootKeys = @(
    'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\vigil_watchdog',
    'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
)

# IFEO key — jeśli ktoś ustawi Debugger=cmd.exe to przy starcie vigil_watchdog.exe
# system odpali debugger zamiast nas. Klasyczna technika persistence/sabotage.
$ifeoKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vigil_watchdog.exe'

# === Logging helpers ===

# Event Log source musi istnieć przed pisaniem. Tworzymy idempotentnie — jeśli już jest, ignoruje.
# UWAGA: New-EventLog wymaga uprawnień admin (które guardian ma jako SYSTEM).
function Ensure-EventSource {
    if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
        try {
            New-EventLog -LogName Application -Source $EventSource -ErrorAction Stop
        } catch {
            # Jeśli stworzenie source padło, zostaw — fall back na Write-Host żeby przynajmniej
            # task scheduler history coś pokazało.
            Write-Host "Nie udało się stworzyć event source: $_"
        }
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
    } catch {
        Write-Host "[$EntryType] $Message"
    }
    # Zawsze do stdout dla Task Scheduler history
    Write-Host "[$EntryType] $Message"
}

# === Restart counter (persistent) ===

function Get-RestartCounter {
    if (Test-Path $restartCounterPath) {
        $raw = Get-Content $restartCounterPath -Raw -ErrorAction SilentlyContinue
        if ($raw -match '^\s*(\d+)\s*$') { return [int]$matches[1] }
    }
    return 0
}

function Set-RestartCounter {
    param([int]$Value)
    Set-Content -Path $restartCounterPath -Value $Value -Force -ErrorAction SilentlyContinue
}

function Reset-RestartCounter { Set-RestartCounter 0 }

# === Check 1: Service istnieje ===

function Test-ServiceExists {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    return ($null -ne $svc)
}

# === Check 2: Service running (z restart logic) ===

function Test-ServiceRunning {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) { return $false }
    return ($svc.Status -eq 'Running')
}

function Repair-Service {
    if ($DryRun) {
        Write-GuardianEvent -EntryType Warning -EventId 2001 `
            -Message "[DryRun] Service $ServiceName nie running — pominięto restart"
        return
    }

    $counter = Get-RestartCounter
    $counter++
    Set-RestartCounter $counter

    Write-GuardianEvent -EntryType Warning -EventId 2002 `
        -Message "Service $ServiceName nie running (próba $counter/$MaxRestartRetries) — Restart-Service"

    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        # Daj 5s na faktyczny start
        Start-Sleep -Seconds 5
        $svc = Get-Service -Name $ServiceName
        if ($svc.Status -eq 'Running') {
            Write-GuardianEvent -EventId 2003 -Message "Service $ServiceName wystartował, counter zresetowany"
            Reset-RestartCounter
            return
        }
    } catch {
        Write-GuardianEvent -EntryType Error -EventId 2004 `
            -Message "Start-Service failed: $_"
    }

    # Jeśli osiągnęliśmy próg, reboot
    if ($counter -ge $MaxRestartRetries) {
        Write-GuardianEvent -EntryType Error -EventId 2099 `
            -Message "Service $ServiceName nie wstaje po $counter próbach — reboot za $RebootDelaySeconds s"
        Reset-RestartCounter
        Start-Process -FilePath shutdown.exe -ArgumentList @(
            '/r', '/t', "$RebootDelaySeconds",
            '/c', "Vigil Guardian: vigil_watchdog nie odpowiada"
        ) -NoNewWindow
    }
}

# === Check 3: Hash binarki ===

function Get-WatchdogReferenceHash {
    # guardian.dat: DPAPI-encrypted plain text z hashem SHA256 (hex string). Zapisywany przez vigil_install.ps1 (Install.cmd).
    if (-not (Test-Path $guardianDatPath)) { return $null }
    try {
        $encrypted = [byte[]](Get-Content -Path $guardianDatPath -Encoding Byte -ErrorAction Stop)
        Add-Type -AssemblyName System.Security
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $encrypted, $null,
            [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        return ([System.Text.Encoding]::UTF8.GetString($plain)).Trim()
    } catch {
        Write-GuardianEvent -EntryType Warning -EventId 3001 `
            -Message "Nie udało się odczytać guardian.dat: $_"
        return $null
    }
}

function Test-WatchdogHash {
    if (-not (Test-Path $watchdogExePath)) {
        Write-GuardianEvent -EntryType Error -EventId 3002 `
            -Message "Plik $watchdogExePath nie istnieje"
        return $false
    }

    $referenceHash = Get-WatchdogReferenceHash
    if (-not $referenceHash) {
        # Brak reference — installer nie ustawił. Logujemy ale nie traktujemy jako fail
        # (lepiej brak checka niż false-positive sabotage alert).
        Write-GuardianEvent -EntryType Warning -EventId 3003 `
            -Message "Brak reference hash w guardian.dat — pomijam check integralności"
        return $true
    }

    $actualHash = (Get-FileHash -Path $watchdogExePath -Algorithm SHA256).Hash
    return ($actualHash -eq $referenceHash)
}

function Repair-WatchdogBinary {
    if (-not (Test-Path $watchdogBackupPath)) {
        Write-GuardianEvent -EntryType Error -EventId 3098 `
            -Message "Hash mismatch i brak backup w $watchdogBackupPath — manual recovery wymagany"
        return
    }
    if ($DryRun) {
        Write-GuardianEvent -EntryType Warning -EventId 3099 `
            -Message "[DryRun] Hash mismatch — pominięto restore z backup"
        return
    }
    try {
        # Stop service przed nadpisaniem (file lock)
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Copy-Item -Path $watchdogBackupPath -Destination $watchdogExePath -Force
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
        Write-GuardianEvent -EntryType Warning -EventId 3010 `
            -Message "Hash mismatch — restored z backup $watchdogBackupPath"
    } catch {
        Write-GuardianEvent -EntryType Error -EventId 3011 `
            -Message "Restore z backup failed: $_"
    }
}

# === Check 4: IFEO Debugger ===

function Test-IfeoClean {
    if (-not (Test-Path $ifeoKey)) { return $true }
    $debugger = (Get-ItemProperty -Path $ifeoKey -Name 'Debugger' -ErrorAction SilentlyContinue).Debugger
    return ([string]::IsNullOrEmpty($debugger))
}

function Repair-Ifeo {
    if ($DryRun) {
        Write-GuardianEvent -EntryType Warning -EventId 4001 `
            -Message "[DryRun] IFEO Debugger ustawiony — pominięto usunięcie"
        return
    }
    try {
        if (Test-Path $ifeoKey) {
            Remove-ItemProperty -Path $ifeoKey -Name 'Debugger' -ErrorAction SilentlyContinue
            # Jeśli klucz pusty po usunięciu Debugger, można usunąć cały klucz, ale safer
            # zostawić — może mieć inne legitne values.
        }
        Write-GuardianEvent -EntryType Warning -EventId 4010 `
            -Message "IFEO Debugger usunięty z $ifeoKey"
    } catch {
        Write-GuardianEvent -EntryType Error -EventId 4011 -Message "IFEO repair failed: $_"
    }
}

# === Check 5: SafeBoot entries ===

function Test-SafeBootEntries {
    foreach ($key in $safeBootKeys) {
        if (-not (Test-Path $key)) { return $false }
        # Default value w rejestrze PS access przez '(default)'.
        # Get-ItemProperty zwraca obiekt PSCustomObject z property '(default)' (lowercase).
        $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -eq $props) { return $false }
        $val = $props.'(default)'
        # SafeBoot wymaga value "Service" dla services
        if ($val -ne 'Service') { return $false }
    }
    return $true
}

function Repair-SafeBootEntries {
    if ($DryRun) {
        Write-GuardianEvent -EntryType Warning -EventId 5001 `
            -Message "[DryRun] SafeBoot entries brakują — pominięto recreate"
        return
    }
    foreach ($key in $safeBootKeys) {
        try {
            if (-not (Test-Path $key)) {
                # Tworzymy całą ścieżkę
                $parent = Split-Path -Parent $key
                if (-not (Test-Path $parent)) { New-Item -Path $parent -Force | Out-Null }
                New-Item -Path $key -Force | Out-Null
            }
            # Default value w rejestrze: Set-Item (nie Set-ItemProperty z Name='(Default)' — to nie działa).
            # `Set-Item -Path <key> -Value "..."` ustawia default value.
            Set-Item -Path $key -Value 'Service' -Force
        } catch {
            Write-GuardianEvent -EntryType Error -EventId 5011 `
                -Message "SafeBoot $key recreate failed: $_"
        }
    }
    Write-GuardianEvent -EntryType Warning -EventId 5010 -Message "SafeBoot entries recreated"
}

# === Check 6: Service Recovery config ===

function Test-ServiceRecovery {
    # sc qfailure zwraca text. Szukamy "RESTART -- Delay = 0 milliseconds." lub "REBOOT" w outputie.
    $output = & sc.exe qfailure $ServiceName 2>&1 | Out-String
    return ($output -match 'RESTART' -or $output -match 'REBOOT')
}

function Repair-ServiceRecovery {
    if ($DryRun) {
        Write-GuardianEvent -EntryType Warning -EventId 6001 `
            -Message "[DryRun] Service Recovery config brakuje — pominięto"
        return
    }
    try {
        # reset=60 (counter reset po 60s normalnej pracy)
        # actions=restart/0/restart/0/reboot/60000 — restart natychmiast×2, potem reboot za 60s
        & sc.exe failure $ServiceName reset= 60 actions= restart/0/restart/0/reboot/60000 | Out-Null
        Write-GuardianEvent -EntryType Warning -EventId 6010 -Message "Service Recovery config recreated"
    } catch {
        Write-GuardianEvent -EntryType Error -EventId 6011 -Message "Service Recovery repair failed: $_"
    }
}

# === Main flow ===

function Main {
    Ensure-EventSource

    Write-GuardianEvent -EventId 1001 -Message "Guardian check start (DryRun=$DryRun)"

    # Check 1: service exists. Jeśli nie, dalsze checki nie mają sensu.
    if (-not (Test-ServiceExists)) {
        Write-GuardianEvent -EntryType Error -EventId 1099 `
            -Message "Service $ServiceName nie istnieje — installer powinien ją stworzyć"
        # Guardian tylko alertuje, reinstall to job installera.
        return
    }

    # Check 2: service running
    if (-not (Test-ServiceRunning)) {
        Repair-Service
    } else {
        # Service działa — reset counter (na wypadek gdyby poprzedni run go inkrementował)
        if ((Get-RestartCounter) -gt 0) { Reset-RestartCounter }
    }

    # Check 3: hash. Restore tylko jeśli jest backup.
    if (-not (Test-WatchdogHash)) {
        Write-GuardianEvent -EntryType Warning -EventId 3009 `
            -Message "Hash $WatchdogExeName != reference"
        Repair-WatchdogBinary
    }

    # Check 4: IFEO debugger
    if (-not (Test-IfeoClean)) {
        Write-GuardianEvent -EntryType Warning -EventId 4009 `
            -Message "IFEO Debugger key wykryty na vigil_watchdog.exe"
        Repair-Ifeo
    }

    # Check 5: SafeBoot
    if (-not (Test-SafeBootEntries)) {
        Write-GuardianEvent -EntryType Warning -EventId 5009 `
            -Message "SafeBoot entries niekompletne"
        Repair-SafeBootEntries
    }

    # Check 6: Service Recovery config
    if (-not (Test-ServiceRecovery)) {
        Write-GuardianEvent -EntryType Warning -EventId 6009 `
            -Message "Service Recovery config brakuje"
        Repair-ServiceRecovery
    }

    Write-GuardianEvent -EventId 1002 -Message "Guardian check zakończony"
}

# Wrap całość żeby nieoczekiwany wyjątek też trafił do event log a nie tylko Task Scheduler history
try {
    Main
} catch {
    Ensure-EventSource
    Write-GuardianEvent -EntryType Error -EventId 9999 `
        -Message "Guardian crash: $_`n$($_.ScriptStackTrace)"
    exit 1
}
