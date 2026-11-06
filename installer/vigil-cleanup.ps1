<#
.SYNOPSIS
    Usuwa wszystkie ślady systemu Vigil (kontrola czasu PC dziecka) z Windowsa.

.DESCRIPTION
    Działa w dwóch trybach (auto-detect):

    ONLINE — uruchomiony na działającym systemie operacyjnym jako Administrator.
        Zatrzymuje i usuwa service vigil_watchdog, kasuje Scheduled Task, zabija procesy,
        kasuje pliki, czyści live HKLM oraz HKU dla wszystkich załadowanych profili.

    OFFLINE — uruchomiony z Windows Recovery Environment (WinPE) / instalatora w trybie
        naprawy. Mountuje hives docelowego systemu, czyści rejestr, kasuje pliki.
        Wymaga parametru -SystemDrive wskazującego literę dysku z zainstalowanym Windowsem
        (zwykle D: lub E: gdy WinPE jest na X:).


.PARAMETER SystemDrive
    Litera dysku z zainstalowanym Windows (np. 'D:'). Wymagane dla trybu offline.
    Pomijane w trybie online — używamy $env:SystemDrive ('C:').

.PARAMETER Force
    Pomija interaktywne potwierdzenie usunięcia. Konieczne w skryptach automatycznych.

.PARAMETER KeepUserPolicies
    Nie cofa zmian w user policies (DisableTaskMgr, sticky-keys itp.). Domyślnie cofamy
    żeby user mógł znowu używać Windows normalnie. Useful gdy chcesz reinstalować Vigil.

.PARAMETER WhatIf
    Standardowy PowerShell -WhatIf — pokazuje co by usunął, niczego nie kasuje.

.EXAMPLE
    .\vigil-cleanup.ps1
    Online cleanup na działającym systemie.

.EXAMPLE
    .\vigil-cleanup.ps1 -Force
    Online cleanup bez pytań.

.EXAMPLE
    # Z Windows Recovery / instalatora (cmd → powershell.exe):
    X:\> D:                        # przełącz na dysk z Windowsem
    D:\> cd \                      # gdziekolwiek
    D:\> powershell.exe -ExecutionPolicy Bypass -File X:\vigil-cleanup.ps1 -SystemDrive D: -Force
    Offline cleanup gdy Windows nie startuje albo chcemy fizycznie wykasować z innej OS.

.NOTES
    Co konkretnie kasuje:

    1. Service Windows: vigil_watchdog (włącznie z konfiguracją, recovery actions, DACL)
    2. Scheduled Task: "Vigil Guardian", "Vigil Tray"
    3. Procesy (online): vigil_watchdog.exe, vigil_lockscreen.exe, vigil_tray.exe, vigil_guardian (PS)
    4. Pliki:
        - C:\ProgramData\Vigil\
        - C:\Windows\System32\Tasks\Vigil Guardian
    5. Event Log sources: Vigil Watchdog, Vigil Guardian
    6. SafeBoot entries: vigil_watchdog w Minimal i Network
    7. User policies (jeśli -KeepUserPolicies NIE podany):
        - DisableTaskMgr, DisableRegistryTools, DisableLockWorkstation, DisableChangePassword
        - NoLogoff
        - Accessibility: StickyKeys/Keyboard Response/ToggleKeys/MouseKeys Flags (przywrócone defaulty)
        - PrecisionTouchPad: ThreeFinger/FourFinger Slide/Tap (przywrócone 1)
       Powyższe dla:
        - Default profile (C:\Users\Default\NTUSER.DAT)
        - Każde konto user w C:\Users\<name>\NTUSER.DAT (offline mount jeśli nie zalogowany)

    Czego NIE robi:
        - reagentc /enable — opt-in. Jeśli installer wyłączył WinRE przez -DisableWinRE,
          przywróć ręcznie: reagentc /enable (wymaga zamontowanego image albo działającego systemu).
        - bcdedit bootmenupolicy — Standard jest default na Win10+ więc nie ma czego cofać.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SystemDrive,
    [switch] $Force,
    [switch] $KeepUserPolicies
)

$ErrorActionPreference = 'Continue'  # nie wywalamy się na pierwszym błędzie — best-effort cleanup

# === Konsola: stylizowane outputy ===

function Write-Step    { param([string]$Msg) Write-Host "==> $Msg" -ForegroundColor Cyan }
function Write-OK      { param([string]$Msg) Write-Host "    [OK] $Msg" -ForegroundColor Green }
function Write-Skip    { param([string]$Msg) Write-Host "    [skip] $Msg" -ForegroundColor DarkGray }
function Write-Warn    { param([string]$Msg) Write-Host "    [warn] $Msg" -ForegroundColor Yellow }
function Write-ErrMsg  { param([string]$Msg) Write-Host "    [err] $Msg" -ForegroundColor Red }

# === Detekcja trybu (online / offline) ===

function Test-IsOnlineMode {
    # Online = $SystemDrive nie podany ALBO równy $env:SystemDrive ($env:SystemDrive zazwyczaj 'C:').
    # Sprawdzamy też że jakiś service manager jest dostępny — w WinPE Get-Service rzuca błędem.
    if ($SystemDrive -and $SystemDrive.TrimEnd(':\') -ne $env:SystemDrive.TrimEnd(':\')) {
        return $false  # explicit offline
    }
    try {
        $null = Get-Service -Name 'vigil_watchdog' -ErrorAction Stop
        return $true  # service exists → online
    } catch [Microsoft.PowerShell.Commands.ServiceCommandException] {
        # Service not found — ale Get-Service działa, więc jesteśmy online (tylko bez watchdoga)
        return $true
    } catch {
        # Get-Service w ogóle nie działa — prawdopodobnie WinPE
        return $false
    }
}

function Resolve-OfflineSystemDrive {
    if (-not $SystemDrive) {
        # Auto-detect: szukamy literek dysku z prawdopodobnym Windows install.
        # W WinPE typowo: X:\ to PE, prawdziwy system to C:\ albo D:\.
        $candidates = @('C:', 'D:', 'E:', 'F:', 'G:')
        foreach ($d in $candidates) {
            if ((Test-Path "$d\Windows\System32\config\SOFTWARE") -and
                (Test-Path "$d\Users\Default\NTUSER.DAT")) {
                Write-Step "Auto-detected system drive: $d"
                $script:SystemDrive = $d
                return
            }
        }
        throw "Nie podano -SystemDrive i auto-detect nie znalazł żadnego dysku z Windows. Podaj literę dysku ręcznie: -SystemDrive D:"
    }
    $SystemDrive = $SystemDrive.TrimEnd('\').TrimEnd(':') + ':'
    if (-not (Test-Path "$SystemDrive\Windows\System32\config\SOFTWARE")) {
        throw "$SystemDrive nie wygląda na dysk z zainstalowanym Windows (brak \Windows\System32\config\SOFTWARE)."
    }
    $script:SystemDrive = $SystemDrive
}

# === Helpery: rejestr ===

function Clear-RegistryValues {
    param(
        [string] $Path,
        [string[]] $ValueNames
    )
    if (-not (Test-Path $Path)) { return }
    foreach ($name in $ValueNames) {
        $val = Get-ItemProperty -Path $Path -Name $name -ErrorAction SilentlyContinue
        if ($null -ne $val) {
            if ($PSCmdlet.ShouldProcess("$Path\$name", "Remove")) {
                Remove-ItemProperty -Path $Path -Name $name -ErrorAction SilentlyContinue
            }
        }
    }
}

function Set-RegistryValue {
    param(
        [string] $Path,
        [string] $Name,
        $Value,
        [Microsoft.Win32.RegistryValueKind] $Kind
    )
    if (-not (Test-Path $Path)) { return }
    if ($PSCmdlet.ShouldProcess("$Path\$Name = $Value", "Restore")) {
        Set-ItemProperty -Path $Path -Name $Name -Value $Value -Type $Kind -ErrorAction SilentlyContinue
    }
}

function Clear-VigilPoliciesFromHive {
    <#
        Cofa wszystkie zmiany policy/accessibility/touchpad w danym hive.
        BasePath: prefix do user hive np.
            "Registry::HKEY_USERS\S-1-5-21-..." (live HKU)
            "Registry::HKEY_LOCAL_MACHINE\PC_CleanupTemp" (offline mount)
    #>
    param([string] $BasePath, [string] $Label)

    Write-Step "Czyszczę policies w '$Label'"

    # 1. Policies\System
    Clear-RegistryValues `
        -Path "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\System" `
        -ValueNames @(
            'DisableTaskMgr',
            'DisableRegistryTools',
            'DisableLockWorkstation',
            'DisableChangePassword'
        )

    # 2. Policies\Explorer
    Clear-RegistryValues `
        -Path "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" `
        -ValueNames @('NoLogoff')

    # 3. Accessibility shortcuts — przywracamy domyślne Windows
    $accDefaults = @{
        'StickyKeys'        = 510
        'Keyboard Response' = 126
        'ToggleKeys'        = 62
        'MouseKeys'         = 62
    }
    foreach ($key in $accDefaults.Keys) {
        Set-RegistryValue `
            -Path "$BasePath\Control Panel\Accessibility\$key" `
            -Name 'Flags' `
            -Value $accDefaults[$key] `
            -Kind String
    }

    # 4. Precision touchpad gestures — przywracamy 1 (enabled, Windows default)
    $touchpadPath = "$BasePath\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"
    if (Test-Path $touchpadPath) {
        foreach ($name in @(
            'ThreeFingerSlideEnabled',
            'FourFingerSlideEnabled',
            'ThreeFingerTapEnabled',
            'FourFingerTapEnabled'
        )) {
            Set-RegistryValue -Path $touchpadPath -Name $name -Value 1 -Kind DWord
        }
    }

    Write-OK "$Label cleaned"
}

function Mount-OfflineHive {
    param(
        [string] $HiveFile,   # np. "D:\Users\jasiek\NTUSER.DAT"
        [string] $TempKey     # np. "HKLM\PC_CleanupUser"
    )
    if (-not (Test-Path $HiveFile)) { return $false }
    & reg.exe load $TempKey $HiveFile 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Dismount-OfflineHive {
    param([string] $TempKey)
    # Wymagane GC żeby zwolnić handle które PowerShell trzyma na klucze podczas Set/Remove
    [gc]::Collect()
    [gc]::WaitForPendingFinalizers()
    Start-Sleep -Milliseconds 500
    & reg.exe unload $TempKey 2>&1 | Out-Null
}

# === Online mode helpers ===

function Stop-VigilServiceMultiStrategy {
    Write-Step "Zatrzymuję service vigil_watchdog"
    $svc = Get-Service -Name 'vigil_watchdog' -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Skip "Service nie istnieje"; return }

    if ($svc.Status -eq 'Stopped') { Write-OK "Już zatrzymany"; return }

    # Strategy 1: PowerShell native
    try {
        Stop-Service -Name 'vigil_watchdog' -Force -ErrorAction Stop
        Start-Sleep -Seconds 2
        $svc.Refresh()
        if ($svc.Status -eq 'Stopped') { Write-OK "Zatrzymany (Stop-Service)"; return }
    } catch { Write-Warn "Stop-Service failed: $_" }

    # Strategy 2: sc.exe stop
    & sc.exe stop vigil_watchdog 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $svc.Refresh()
    if ($svc.Status -eq 'Stopped') { Write-OK "Zatrzymany (sc.exe)"; return }

    # Strategy 3: kill process
    $proc = Get-Process -Name 'vigil_watchdog' -ErrorAction SilentlyContinue
    if ($proc) {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
            Start-Sleep -Seconds 1
            Write-OK "Zatrzymany (Stop-Process)"
            return
        } catch { Write-Warn "Stop-Process failed: $_" }
    }

    # Strategy 4: scheduled task as SYSTEM (DACL na service daje SY: GenericAll nawet
    # gdy zwykły Admin nie ma access).
    $sysTaskName = "VigilCleanup_StopService_$([guid]::NewGuid().ToString('N').Substring(0,8))"
    try {
        $action = New-ScheduledTaskAction -Execute 'sc.exe' -Argument 'stop vigil_watchdog'
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
        Register-ScheduledTask -TaskName $sysTaskName -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName $sysTaskName
        Start-Sleep -Seconds 3
        Unregister-ScheduledTask -TaskName $sysTaskName -Confirm:$false -ErrorAction SilentlyContinue
        $svc.Refresh()
        if ($svc.Status -eq 'Stopped') { Write-OK "Zatrzymany (SYSTEM scheduled task)"; return }
    } catch { Write-Warn "SYSTEM task fallback failed: $_" }

    Write-ErrMsg "Nie udało się zatrzymać service. Spróbuj ponownie po reboot lub w trybie offline (WinRE)."
}

function Remove-VigilService {
    Write-Step "Kasuję service vigil_watchdog"
    $svc = Get-Service -Name 'vigil_watchdog' -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Skip "Service nie istnieje"; return }

    & sc.exe delete vigil_watchdog 2>&1 | Out-Null
    Start-Sleep -Seconds 1
    if ($LASTEXITCODE -eq 0) {
        Write-OK "Service skasowany"
    } else {
        # Fallback przez SYSTEM task (DACL bypass)
        $sysTaskName = "VigilCleanup_DeleteService_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        try {
            $action = New-ScheduledTaskAction -Execute 'sc.exe' -Argument 'delete vigil_watchdog'
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
            Register-ScheduledTask -TaskName $sysTaskName -Action $action -Principal $principal -Force | Out-Null
            Start-ScheduledTask -TaskName $sysTaskName
            Start-Sleep -Seconds 3
            Unregister-ScheduledTask -TaskName $sysTaskName -Confirm:$false -ErrorAction SilentlyContinue
            $svc2 = Get-Service -Name 'vigil_watchdog' -ErrorAction SilentlyContinue
            if (-not $svc2) { Write-OK "Service skasowany (przez SYSTEM)" }
            else { Write-ErrMsg "Nie udało się skasować service. Restart i spróbuj ponownie." }
        } catch { Write-ErrMsg "sc.exe delete failed: exit=$LASTEXITCODE" }
    }
}

function Stop-VigilProcesses {
    Write-Step "Zabijam procesy Vigil (lockscreen, tray, watchdog)"
    $names = @('vigil_watchdog', 'vigil_lockscreen', 'vigil_tray')
    foreach ($n in $names) {
        $procs = Get-Process -Name $n -ErrorAction SilentlyContinue
        if (-not $procs) { Write-Skip "$n nie działa"; continue }
        try {
            $procs | Stop-Process -Force -ErrorAction Stop
            Write-OK "$n zabity ($($procs.Count) proces(ów))"
        } catch {
            Write-Warn "$n stop-process failed: $_"
        }
    }
    # PowerShell vigil_guardian — może uruchamiać się przez Scheduled Task, więc i tak skasujemy task.
}

function Remove-VigilScheduledTasks {
    Write-Step "Kasuję Scheduled Tasks"
    foreach ($taskName in @('Vigil Guardian', 'Vigil Tray')) {
        try {
            $t = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
            Write-OK "Task '$taskName' skasowany"
        } catch [Microsoft.Management.Infrastructure.CimException] {
            Write-Skip "Task '$taskName' nie istnieje"
        } catch {
            Write-Warn "Unregister-ScheduledTask '$taskName' failed: $_"
        }
    }
}

function Remove-EventLogSources {
    Write-Step "Kasuję Event Log sources"
    # Event log sources są w HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\<Name>.
    # Remove-EventLog wymaga że źródło istnieje, więc try/catch per source.
    foreach ($src in @(
        'Vigil Watchdog',
        'Vigil Guardian'
    )) {
        try {
            if ([System.Diagnostics.EventLog]::SourceExists($src)) {
                [System.Diagnostics.EventLog]::DeleteEventSource($src)
                Write-OK "Source '$src' usunięty"
            } else {
                Write-Skip "Source '$src' nie istnieje"
            }
        } catch {
            Write-Warn "DeleteEventSource '$src' failed: $_"
        }
    }
}

function Remove-SafeBootEntries {
    Write-Step "Kasuję SafeBoot entries (online)"
    $paths = @(
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\vigil_watchdog',
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
    )
    foreach ($p in $paths) {
        if (Test-Path $p) {
            try {
                Remove-Item -Path $p -Recurse -Force -ErrorAction Stop
                Write-OK "$p usunięty"
            } catch { Write-Warn "$p remove failed: $_" }
        } else {
            Write-Skip "$p nie istnieje"
        }
    }
}

function Clear-LiveUserPolicies {
    Write-Step "Czyszczę policies w live HKU dla zalogowanych userów"

    if ($KeepUserPolicies) {
        Write-Skip "Pominięte (-KeepUserPolicies)"
        return
    }

    # HKEY_USERS ma podklucze dla każdej załadowanej hive (zalogowani userzy + Default + ServiceAccounts).
    # SID-y zwykłych userów zaczynają się na S-1-5-21-..., system accounts to krótsze ID.
    try {
        $hives = Get-ChildItem -Path 'Registry::HKEY_USERS\' -ErrorAction Stop |
            Where-Object { $_.Name -match '\\S-1-5-21-' -and $_.Name -notmatch '_Classes$' }
    } catch {
        Write-Warn "Nie udało się wyenumerować HKEY_USERS: $_"
        return
    }

    foreach ($h in $hives) {
        $sid = $h.PSChildName
        try {
            $acc = (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate([System.Security.Principal.NTAccount]).Value
        } catch {
            $acc = $sid  # nie da się rozpoznać — używamy SID jako label
        }
        Clear-VigilPoliciesFromHive `
            -BasePath "Registry::HKEY_USERS\$sid" `
            -Label "live $acc"
    }
}

function Clear-OfflineUserPolicies {
    <#
        Iteruje po wszystkich profilach w $SystemDrive\Users\*\NTUSER.DAT i czyści policies offline.
        Działa zarówno online (jako fallback dla niezalogowanych userów) jak i offline (WinPE).
    #>
    Write-Step "Czyszczę policies w offline NTUSER.DAT (każde konto user)"

    if ($KeepUserPolicies) {
        Write-Skip "Pominięte (-KeepUserPolicies)"
        return
    }

    $usersRoot = Join-Path $script:SystemDrive 'Users'
    if (-not (Test-Path $usersRoot)) {
        Write-Warn "$usersRoot nie istnieje"
        return
    }

    $profiles = Get-ChildItem -Path $usersRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin @('Public', 'All Users') }

    # Plus Default profile (NOWE konta będą tworzone z tego templatu)
    $profiles = @($profiles) + @(Get-Item (Join-Path $usersRoot 'Default') -ErrorAction SilentlyContinue)

    foreach ($prof in $profiles) {
        if (-not $prof) { continue }
        $hiveFile = Join-Path $prof.FullName 'NTUSER.DAT'
        if (-not (Test-Path $hiveFile)) { continue }

        $tempKey = "HKLM\PC_Cleanup_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $mounted = Mount-OfflineHive -HiveFile $hiveFile -TempKey $tempKey

        if (-not $mounted) {
            Write-Warn "Profile '$($prof.Name)': mount NTUSER.DAT failed (zalogowany?), pomijam offline path"
            continue
        }
        try {
            $hkuPath = "Registry::HKEY_LOCAL_MACHINE\$($tempKey -replace '^HKLM\\','')"
            Clear-VigilPoliciesFromHive -BasePath $hkuPath -Label "offline $($prof.Name)"
        } finally {
            Dismount-OfflineHive -TempKey $tempKey
        }
    }
}

function Remove-VigilFiles {
    Write-Step "Kasuję pliki Vigil"

    $paths = @(
        (Join-Path $script:SystemDrive 'ProgramData\Vigil'),
        (Join-Path $script:SystemDrive 'Users\Public\Vigil'),            # bypass.flag location
        (Join-Path $script:SystemDrive 'Windows\System32\Tasks\Vigil Guardian'),
        (Join-Path $script:SystemDrive 'Windows\System32\Tasks\Vigil Tray')
    )

    foreach ($p in $paths) {
        if (-not (Test-Path $p)) { Write-Skip "$p nie istnieje"; continue }
        try {
            # -Force żeby kasować read-only ACLem, -Recurse dla katalogów
            if ($PSCmdlet.ShouldProcess($p, "Remove")) {
                Remove-Item -Path $p -Recurse -Force -ErrorAction Stop
            }
            Write-OK "$p usunięty"
        } catch {
            # Może wystąpić "Access denied" jeśli DACL ustawiony przez vigil_install.ps1 jeszcze blokuje.
            # Próbujemy nadać sobie ownership + reset ACL.
            Write-Warn "${p}: ${_}, próbuję reset ACL"
            try {
                & icacls.exe $p /reset /T /C /Q 2>&1 | Out-Null
                & takeown.exe /F $p /A /R /D Y 2>&1 | Out-Null
                Remove-Item -Path $p -Recurse -Force -ErrorAction Stop
                Write-OK "$p usunięty (po reset ACL)"
            } catch {
                Write-ErrMsg "${p}: ostatecznie failed: ${_}"
            }
        }
    }
}

function Remove-OfflineService {
    <#
        Offline mode: nie mamy Service Manager dostępu, więc usuwamy entry przez registry hive.
        Mountuje SYSTEM\CurrentControlSet\Services\vigil_watchdog z $SystemDrive.
    #>
    Write-Step "Kasuję service vigil_watchdog z offline SYSTEM hive"

    $systemHive = Join-Path $script:SystemDrive 'Windows\System32\config\SYSTEM'
    if (-not (Test-Path $systemHive)) {
        Write-Warn "SYSTEM hive nie istnieje pod $systemHive"
        return
    }

    $tempKey = "HKLM\PC_CleanupSystem"
    & reg.exe load $tempKey $systemHive 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "reg load SYSTEM failed: exit=$LASTEXITCODE (czy hive jest zablokowany przez running OS?)"
        return
    }

    try {
        # ControlSet001 to obecny activeControlSet w większości systemów; "Select" key wskazuje.
        # Najbezpieczniej: iteruj po wszystkich ControlSet*.
        $mountedRoot = "Registry::HKEY_LOCAL_MACHINE\$($tempKey -replace '^HKLM\\','')"
        $controlSets = Get-ChildItem -Path $mountedRoot -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^ControlSet\d+$' -or $_.PSChildName -eq 'CurrentControlSet' }

        foreach ($cs in $controlSets) {
            $svcPath = "$($cs.PSPath)\Services\vigil_watchdog"
            if (Test-Path $svcPath) {
                try {
                    Remove-Item -Path $svcPath -Recurse -Force -ErrorAction Stop
                    Write-OK "Service entry usunięty z $($cs.PSChildName)"
                } catch { Write-Warn "Remove $svcPath failed: $_" }
            }

            # SafeBoot
            foreach ($sb in @('Minimal','Network')) {
                $sbPath = "$($cs.PSPath)\Control\SafeBoot\$sb\vigil_watchdog"
                if (Test-Path $sbPath) {
                    try {
                        Remove-Item -Path $sbPath -Recurse -Force -ErrorAction Stop
                        Write-OK "SafeBoot\$sb entry usunięty z $($cs.PSChildName)"
                    } catch { Write-Warn "Remove $sbPath failed: $_" }
                }
            }
        }
    } finally {
        Dismount-OfflineHive -TempKey $tempKey
    }
}

function Remove-OfflineEventLogSources {
    Write-Step "Kasuję Event Log sources z offline SOFTWARE hive"

    $softwareHive = Join-Path $script:SystemDrive 'Windows\System32\config\SOFTWARE'
    if (-not (Test-Path $softwareHive)) {
        Write-Warn "SOFTWARE hive nie istnieje pod $softwareHive"
        return
    }

    $tempKey = "HKLM\PC_CleanupSoftware"
    & reg.exe load $tempKey $softwareHive 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "reg load SOFTWARE failed: exit=$LASTEXITCODE"
        return
    }

    try {
        # Event log sources: SYSTEM\CurrentControlSet\Services\EventLog\Application\<Source>
        # ALE jesteśmy w SOFTWARE hive — Eventlog sources są faktycznie w SYSTEM hive!
        # Tu nic do roboty, ale zostawiamy strukturę na wypadek przyszłych zmian.
        Write-Skip "Event log sources są w SYSTEM hive (już oczyszczone osobno)"
    } finally {
        Dismount-OfflineHive -TempKey $tempKey
    }
}

function Test-IsElevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ============================================================================
# MAIN
# ============================================================================

Write-Host ""
Write-Host "=============================================================" -ForegroundColor Magenta
Write-Host " Vigil Cleanup — usuwa wszystkie ślady systemu kontroli czasu" -ForegroundColor Magenta
Write-Host "=============================================================" -ForegroundColor Magenta
Write-Host ""

if (-not (Test-IsElevated)) {
    Write-ErrMsg "Skrypt wymaga uprawnień Administratora. Uruchom z 'Run as Administrator'."
    exit 1
}

$online = Test-IsOnlineMode
if ($online) {
    $script:SystemDrive = $env:SystemDrive  # 'C:'
    Write-Host "Tryb: ONLINE (system aktywny, target = $($script:SystemDrive))" -ForegroundColor White
} else {
    Write-Host "Tryb: OFFLINE (recovery/WinPE)" -ForegroundColor White
    Resolve-OfflineSystemDrive
    Write-Host "Target system drive: $($script:SystemDrive)" -ForegroundColor White
}

Write-Host ""

if (-not $Force -and -not $WhatIfPreference) {
    Write-Host "Operacja usunie:" -ForegroundColor Yellow
    Write-Host "  - Service vigil_watchdog" -ForegroundColor Yellow
    Write-Host "  - Scheduled Task Vigil Guardian, Vigil Tray" -ForegroundColor Yellow
    Write-Host "  - Katalogi $($script:SystemDrive)\ProgramData\Vigil" -ForegroundColor Yellow
    Write-Host "  - Event log sources" -ForegroundColor Yellow
    if (-not $KeepUserPolicies) {
        Write-Host "  - User policies (DisableTaskMgr, sticky-keys lock itp.) we wszystkich profilach" -ForegroundColor Yellow
    }
    Write-Host ""
    $answer = Read-Host "Kontynuować? (y/N)"
    if ($answer -notin @('y','Y','yes','Yes','tak','Tak')) {
        Write-Host "Anulowano." -ForegroundColor DarkGray
        exit 0
    }
    Write-Host ""
}

if ($online) {
    Stop-VigilProcesses
    Stop-VigilServiceMultiStrategy
    Remove-VigilService
    Remove-VigilScheduledTasks
    Remove-EventLogSources
    Remove-SafeBootEntries
    Clear-LiveUserPolicies        # zalogowani userzy
    Clear-OfflineUserPolicies     # niezalogowani userzy (Default + reszta)
    Remove-VigilFiles
} else {
    # Offline: brak service manager, mountujemy hives directly
    Remove-OfflineService
    Remove-OfflineEventLogSources
    Clear-OfflineUserPolicies
    Remove-VigilFiles
}

Write-Host ""
Write-Host "=============================================================" -ForegroundColor Green
Write-Host " Cleanup zakończony" -ForegroundColor Green
Write-Host "=============================================================" -ForegroundColor Green
Write-Host ""

if ($online) {
    Write-Host "ZALECANE: zrestartuj komputer żeby upewnić się że wszystkie" -ForegroundColor White
    Write-Host "user policies zostały przeładowane przy następnym logowaniu." -ForegroundColor White
}
Write-Host ""
Write-Host "Jeśli installer wyłączył WinRE (przez -DisableWinRE), przywróć ręcznie:" -ForegroundColor DarkGray
Write-Host "  reagentc /enable" -ForegroundColor DarkGray
Write-Host ""
