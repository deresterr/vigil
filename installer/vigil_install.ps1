<#
.SYNOPSIS
    Installer dla Vigil.

.DESCRIPTION
    Tryby:
        -Mode install        — fresh install (wymaga binarek; bez parametrów wchodzi w tryb interaktywny)
        -Mode uninstall      — remove all (service, task, folder, policies)
        -Mode update-config  — zmiana server_url (vigil.toml) / signing_key (signing.dat)
        -Mode reinstall      — uninstall + install

    Wymaga uruchomienia jako Administrator. Zaprojektowany jako idempotentny:
    `install` na już zainstalowanym systemie nadpisuje config i restartuje service.

.PARAMETER Mode
    install | uninstall | update-config | reinstall

.PARAMETER SourceDir
    Default: bieżący katalog skryptu.

.PARAMETER ServerUrl
    Adres servera np. http://192.168.0.2:8088. Wymagane dla install/update-config.


.PARAMETER SigningKey
    Signing key (base64 urlsafe bez paddingu) z panelu rodzica. Zwykle niepotrzebny: install
    paruje się z serwerem kodem (-PairingCode albo pytanie w trybie interaktywnym).
.PARAMETER PairingCode
    Jednorazowy kod parowania z panelu rodzica (ważny 10 min). Serwer w zamian wydaje nowy klucz.

.PARAMETER DisableWinRE
    Switch — opcjonalne wyłączenie Windows Recovery Environment (`reagentc /disable`).

.PARAMETER NoUserPolicies
    Switch — pomiń policy changes (DisableTaskMgr w Default User profile).
    Użyteczne do testów bez modyfikacji systemu globalnie.

.PARAMETER ChildUsername
    Nazwa konta dziecka na lokalnym PC (np. 'jasiek'). Jeśli podane, installer zaaplikuje
    policy (DisableTaskMgr, DisableRegistryTools, DisableLockWorkstation, NoLogoff,
    sticky-keys hot-key off) DIRECT do tego konta. Bez tego parametru — tylko Default
    profile (efekt dla NOWO tworzonych kont).

.EXAMPLE
    .\vigil_install.ps1 -Mode install `
        -SourceDir 'D:\pc-build' `
        -ServerUrl 'http://192.168.0.2:8088' `
        -SigningKey 'XyZ123...' `
        -ChildUsername 'jasiek'

.EXAMPLE
    .\vigil_install.ps1 -Mode uninstall

.EXAMPLE
    .\vigil_install.ps1 -Mode update-config `
        -ServerUrl 'http://192.168.0.5:8088' `
        -SigningKey 'newKey...'
#>

[CmdletBinding()]
param(
    [ValidateSet('install','uninstall','update-config','reinstall')]
    [string] $Mode = 'install',

    [string] $SourceDir = '',
    [string] $InstallDir = "C:\ProgramData\Vigil",
    [string] $ServiceName = "vigil_watchdog",
    [string] $TaskName = "Vigil Guardian",

    [string] $ServerUrl,
    [string] $SigningKey,
    [string] $PairingCode,
    [string] $ChildUsername,

    [switch] $DisableWinRE,
    [switch] $NoUserPolicies
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot w param() jest puste — obliczamy tu
if (-not $SourceDir) { $SourceDir = $PSScriptRoot }

# === Stałe ===

$watchdogExe   = Join-Path $InstallDir "vigil_watchdog.exe"
$lockscreenExe = Join-Path $InstallDir "vigil_lockscreen.exe"
$trayExe       = Join-Path $InstallDir "vigil_tray.exe"
$guardianPs1   = Join-Path $InstallDir "vigil_guardian.ps1"
$vigilToml     = Join-Path $InstallDir "vigil.toml"
$signingDat    = Join-Path $InstallDir "signing.dat"
$guardianDat   = Join-Path $InstallDir "guardian.dat"
$backupDir     = Join-Path $InstallDir "backup"

# Source files
$srcFiles = @{
    'vigil_watchdog.exe'    = (Join-Path $SourceDir 'vigil_watchdog.exe')
    'vigil_lockscreen.exe'  = (Join-Path $SourceDir 'vigil_lockscreen.exe')
    'vigil_tray.exe'        = (Join-Path $SourceDir 'vigil_tray.exe')
    'vigil_guardian.ps1'    = (Join-Path $SourceDir 'vigil_guardian.ps1')
}

# === Helpers ===

function Write-Step([string]$msg) {
    Write-Host "==> " -ForegroundColor Cyan -NoNewline
    Write-Host $msg
}

function Write-OK([string]$msg) {
    Write-Host "    ✓ " -ForegroundColor Green -NoNewline
    Write-Host $msg
}

function Write-Warn([string]$msg) {
    Write-Host "    ! " -ForegroundColor Yellow -NoNewline
    Write-Host $msg
}

function Write-Skip([string]$msg) {
    Write-Host "    - " -ForegroundColor DarkGray -NoNewline
    Write-Host $msg
}

function Test-IsAdmin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    if (-not (Test-IsAdmin)) {
        throw "Wymagane uruchomienie jako Administrator. Restart PowerShell przez 'Run as Administrator'."
    }
}

# DPAPI helper (LocalMachine scope) — dla signing.dat i guardian.dat
function Protect-LocalMachine {
    param([byte[]]$Data, [byte[]]$Entropy)
    Add-Type -AssemblyName System.Security
    return [System.Security.Cryptography.ProtectedData]::Protect(
        $Data, $Entropy,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
}

# Decode base64 urlsafe (no padding) — format z bootstrap logów servera.
# UWAGA: parametr nie nazywa się $Input bo to automatic variable w PS (pipeline collector) —
# powoduje że funkcja dostaje pustego stringa zamiast argumentu wywołującego.
function ConvertFrom-Base64UrlSafe {
    param([string]$InputString)
    $padded = $InputString.Replace('-','+').Replace('_','/')
    switch ($padded.Length % 4) {
        2 { $padded += '==' }
        3 { $padded += '=' }
    }
    return [System.Convert]::FromBase64String($padded)
}

# === Install steps ===

function Step-RequireAdmin {
    Write-Step "Sprawdzam uprawnienia admin"
    Assert-Admin
    Write-OK "Admin OK"
}

function Step-CheckSourceFiles {
    Write-Step "Sprawdzam pliki źródłowe w $SourceDir"
    $missing = @()
    foreach ($name in $srcFiles.Keys) {
        if (-not (Test-Path $srcFiles[$name])) { $missing += $name }
    }
    if ($missing.Count -gt 0) {
        throw "Brak plików w $SourceDir`: $($missing -join ', ')"
    }
    Write-OK "Wszystkie 4 pliki obecne"
}

function Normalize-ServerUrl {
    param([string]$raw)
    $u = $raw.Trim()
    if ($u -notmatch '^https?://') { $u = 'http://' + $u }
    # Dodaj port 8088 jeśli brak portu
    if ($u -match '^https?://[^/:]+$') { $u = $u + ':8088' }
    return $u.TrimEnd('/')
}

function Test-ServerReachable {
    param([string]$url)
    try {
        $resp = Invoke-WebRequest -Uri "$url/health" -TimeoutSec 6 -UseBasicParsing -ErrorAction Stop
        return $resp.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Get-CredentialsFromServer {
    param([string]$serverUrl, [string]$code)
    if (-not $code) {
        Write-Host ""
        Write-Host "  Kod parowania (panel rodzica -> 'Wygeneruj kod parowania'):"
        $code = (Read-Host "  >").Trim()
    }
    $digits = ($code -replace '[^0-9]', '')
    if ($digits.Length -ne 8) { throw "Kod parowania ma 8 cyfr." }

    Write-Step "Paruję komputer z serwerem"
    $body = @{ code = $digits } | ConvertTo-Json -Compress
    try {
        $resp = Invoke-RestMethod -Uri "$serverUrl/v1/pair" -Method Post -Body $body `
            -ContentType 'application/json' -TimeoutSec 10 -ErrorAction Stop
        # Zachowujemy adres wpisany przez użytkownika — serwer mógł zwrócić inny (np. za proxy).
        $script:ServerUrl  = $serverUrl
        $script:SigningKey = $resp.signing_key
        Write-OK "Sparowano — klucz podpisujący otrzymany"
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 404) {
            throw "Brak aktywnego kodu parowania (wygasł albo został użyty).`n  Wygeneruj nowy kod parowania w panelu rodzica."
        }
        if ($status -eq 401) {
            throw "Nieprawidłowy kod parowania. Po 5 błędnych próbach kod przestaje działać."
        }
        throw "Parowanie nie powiodło się: $_"
    }
}

function Select-ChildUsername {
    Write-Step "Wybór konta dziecka"
    # Lista lokalnych userów bez kont systemowych
    $excluded = @('Administrator','Gość','Guest','DefaultAccount','WDAGUtilityAccount','SYSTEM')
    $users = @(Get-LocalUser | Where-Object { $_.Name -notin $excluded -and $_.Enabled } | Select-Object -ExpandProperty Name)
    if (-not $users) {
        Write-Warn "Brak standardowych kont lokalnych. Wpisz nazwę konta ręcznie."
        $script:ChildUsername = (Read-Host "  Nazwa konta").Trim()
        return
    }
    Write-Host ""
    for ($i = 0; $i -lt $users.Count; $i++) {
        Write-Host "  [$($i+1)] $($users[$i])"
    }
    Write-Host ""
    $choice = Read-Host "  Numer konta dziecka (Enter = pomiń)"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        Write-Warn "Pominięto wybór konta — polityki zostaną zastosowane tylko dla Default User profile."
        return
    }
    $idx = [int]$choice - 1
    if ($idx -lt 0 -or $idx -ge $users.Count) { throw "Nieprawidłowy numer konta." }
    $script:ChildUsername = $users[$idx]
    Write-OK "Konto: $($script:ChildUsername)"
}

function Step-ValidateInstallParams {
    # Jeśli wszystkie parametry podane z linii poleceń — klasyczna walidacja bez pytań
    if ($ServerUrl -and $SigningKey) {
        $script:ServerUrl = Normalize-ServerUrl $ServerUrl
        # Walidacja signing key
        try {
            $keyBytes = ConvertFrom-Base64UrlSafe $SigningKey
            if ($keyBytes.Length -lt 16) { throw "Za krótki po dekodowaniu ($($keyBytes.Length) bajtów)" }
        } catch {
            throw "SigningKey nieprawidłowy: $_"
        }
        Write-OK "Parametry OK (server=$($script:ServerUrl))"
        return
    }

    # Tryb interaktywny
    Write-Host ""
    Write-Host "  [ Tryb interaktywny ]" -ForegroundColor Cyan
    Write-Host "  Nie podano wszystkich parametrów — przeprowadzę przez konfigurację." -ForegroundColor DarkGray
    Write-Host ""

    # ServerUrl
    if (-not $ServerUrl) {
        Write-Host "  Adres serwera (np. 192.168.1.5 lub http://192.168.1.5:8088):"
        $raw = (Read-Host "  >").Trim()
        if ([string]::IsNullOrWhiteSpace($raw)) { throw "Adres serwera jest wymagany." }
        $script:ServerUrl = Normalize-ServerUrl $raw
    } else {
        $script:ServerUrl = Normalize-ServerUrl $ServerUrl
    }

    # Test połączenia
    Write-Host "  Sprawdzam połączenie z $($script:ServerUrl)..." -NoNewline
    if (-not (Test-ServerReachable $script:ServerUrl)) {
        Write-Host " BŁĄD" -ForegroundColor Red
        throw "Nie można połączyć się z serwerem $($script:ServerUrl).`n  Sprawdź:`n    1. Czy docker compose up jest uruchomiony?`n    2. Czy adres IP jest poprawny?`n    3. Czy firewall nie blokuje portu 8088?"
    }
    Write-Host " OK" -ForegroundColor Green

    # Pobierz credentials z serwera (jeśli nie podane parametrami)
    if (-not $SigningKey) {
        Get-CredentialsFromServer -serverUrl $script:ServerUrl -code $PairingCode
    } else {
        $script:SigningKey = $SigningKey
    }

    # ChildUsername — interaktywnie jeśli nie podane
    if (-not $ChildUsername) {
        Select-ChildUsername
    }

    Write-OK "Konfiguracja zebrana (server=$($script:ServerUrl))"
}

function Step-CreateInstallDir {
    Write-Step "Tworzę katalog $InstallDir"
    if (-not (Test-Path $InstallDir)) {
        New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null
    }
    if (-not (Test-Path $backupDir)) {
        New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
    }
    # Restrykcyjne ACL ustawiamy na końcu (Step-LockDownPermissions) ŻEBY nie odebrać
    # samemu sobie prawa do zapisu w trakcie kopiowania plików i DPAPI dat-ów.
    Write-OK "Katalog gotowy"
}

function Step-LockDownPermissions {
    # Wywoływane na końcu instalacji — po wszystkich Copy-Item, WriteAllBytes etc.
    # Ustawia restrykcyjne ACL: SYSTEM full, Administrators read+execute, AuthUsers read+execute.
    # Cel: dziecko (Authenticated User) i ewentualny atakujący z lokalnym admin nie mogą
    # podmienić binarek ani dat-ów. Tylko SYSTEM ma write — watchdog działa jako SYSTEM więc
    # może updateować swój własny stack przy potrzebie.
    Write-Step "Ustawiam restrykcyjne ACL na $InstallDir"
    try {
        $acl = Get-Acl $InstallDir
        $acl.SetAccessRuleProtection($true, $false)
        $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

        # SYSTEM full
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier(
                [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)),
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            ('ContainerInherit,ObjectInherit'),
            'None',
            'Allow')
        $acl.AddAccessRule($rule)

        # Administrators Modify (read+write+delete files, ale NIE zmiana permissions/ownership).
        # Bez Modify nie można robić maintenance (update-config, podmiana binarek przy update,
        # rotacja kluczy). Modify zamiast FullControl chroni przed pomyłką typu "skasowanie ACL".
        # Dziecko nie ma admin praw więc to nie jest osłabienie security wobec niego.
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier(
                [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)),
            [System.Security.AccessControl.FileSystemRights]::Modify,
            ('ContainerInherit,ObjectInherit'),
            'None',
            'Allow')
        $acl.AddAccessRule($rule)

        # Authenticated Users read+execute (child user też tu wpada — żeby vigil_lockscreen.exe
        # mógł być spawnowany przez SYSTEM w user session)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier(
                [System.Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)),
            [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
            ('ContainerInherit,ObjectInherit'),
            'None',
            'Allow')
        $acl.AddAccessRule($rule)

        Set-Acl -Path $InstallDir -AclObject $acl
        Write-OK "ACL ustawione (SYSTEM full, Admin modify, AuthUsers r+x)"
    } catch {
        Write-Warn "Nie udało się ustawić ACL: $_ — kontynuuję z default"
    }
}

function Step-CopyBinaries {
    Write-Step "Kopiuję binarki do $InstallDir"
    foreach ($name in $srcFiles.Keys) {
        $dst = Join-Path $InstallDir $name
        Copy-Item -Path $srcFiles[$name] -Destination $dst -Force
        Write-OK $name
    }

    # Backup vigil_watchdog.exe — guardian używa do restore przy hash mismatch
    Copy-Item -Path $watchdogExe -Destination (Join-Path $backupDir 'vigil_watchdog.exe') -Force
    Write-OK "backup\vigil_watchdog.exe"
}

function Step-WriteGuardianHash {
    Write-Step "Generuję reference hash dla guardian.dat"
    $hash = (Get-FileHash -Path $watchdogExe -Algorithm SHA256).Hash.ToLower()
    $hashBytes = [System.Text.Encoding]::UTF8.GetBytes($hash)
    $encrypted = Protect-LocalMachine -Data $hashBytes -Entropy $null
    [System.IO.File]::WriteAllBytes($guardianDat, $encrypted)
    Write-OK "SHA256 ($($hash.Substring(0,16))...) zapisany w guardian.dat (DPAPI)"
}

function Step-WriteVigilToml {
    Write-Step "Tworzę vigil.toml"
    $content = @"
# Vigil config. Brak sekretów: klucz podpisu jest w signing.dat (DPAPI, tylko SYSTEM).

server_url = "$ServerUrl"
"@
    Set-Content -Path $vigilToml -Value $content -Encoding UTF8 -Force
    Write-OK "vigil.toml zapisany"
}

function Step-WriteSigningDat {
    Write-Step "Szyfruję signing_key przez DPAPI LocalMachine"
    # Entropy zgodne z DpapiKeyStore w vigil_watchdog (Configuration/DpapiKeyStore.cs)
    $entropy = [System.Text.Encoding]::UTF8.GetBytes("Vigil-SigningKey-v1")
    $keyBytes = ConvertFrom-Base64UrlSafe $SigningKey
    $encrypted = Protect-LocalMachine -Data $keyBytes -Entropy $entropy
    [System.IO.File]::WriteAllBytes($signingDat, $encrypted)

    # Wyczyść keyBytes z pamięci (best effort, GC może mieć kopie)
    [System.Array]::Clear($keyBytes, 0, $keyBytes.Length)

    Write-OK "signing.dat zapisany ($($encrypted.Length) bajtów encrypted)"
}

function Step-RemoveLegacyEmergencyFiles {
    # Before v0.5 offline unlock used an emergency password (emergency.dat, readable by the child)
    # and a 24 h bypass.flag in C:\Users\Public\Vigil (forgeable). Both are gone (#9): the parent's
    # TOTP code now works offline too. Remove leftovers from older installs.
    $legacy = @((Join-Path $InstallDir "emergency.dat"), "$env:PUBLIC\Vigil")
    foreach ($path in $legacy) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
            Write-OK "Usunięto stary plik odblokowania awaryjnego: $path"
        }
    }
}

function Step-RegisterService {
    Write-Step "Rejestruję service $ServiceName"

    # Idempotencja: jeśli istnieje, delete najpierw
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        # Stop przez multi-strategy — Stop-Service zawodzi gdy SDDL jest już ustawiony
        $null = Stop-VigilService
        # sc.exe delete też może wymagać SYSTEM (SDDL nie daje Admin SD) — przy błędzie logujemy
        & sc.exe delete $ServiceName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # Próba przez SYSTEM task
            $delTask = "_pc_del_$(Get-Random)"
            & schtasks.exe /Create /TN $delTask /SC ONCE /ST 23:59 /RU SYSTEM /TR "sc.exe delete $ServiceName" /F 2>&1 | Out-Null
            & schtasks.exe /Run /TN $delTask 2>&1 | Out-Null
            Start-Sleep -Seconds 3
            & schtasks.exe /Delete /TN $delTask /F 2>&1 | Out-Null
        }
        Start-Sleep -Seconds 1
    }

    # Format sc.exe: "key= value" ze spacją PO znaku =, bez przed.
    & sc.exe create $ServiceName binPath= "`"$watchdogExe`"" start= auto displayname= "Vigil Watchdog" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc create failed (exit $LASTEXITCODE)" }

    & sc.exe description $ServiceName "Service for parental time control" | Out-Null
    # Recovery: restart×3 z 60s opóźnieniem, BEZ reboota komputera — bug w agencie
    # (np. crash przy starcie) nie powinien prowadzić do reboot loopa. Po 3 fails zostaw
    # failed state, administrator zdiagnozuje przez Event Log.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    # FailureFlag=1: recovery działa też przy non-zero exit (nie tylko crash)
    & sc.exe failureflag $ServiceName 1 | Out-Null

    Write-OK "Service $ServiceName zarejestrowany (auto-start, restart×3 recovery, no reboot)"
}

function Step-SetServiceDacl {
    Write-Step "Ograniczam DACL service'u (Admin: query/start/stop, no delete)"
    # SDDL z spec'a:
    #   D: = Discretionary ACL
    #   (A;;CCLCSWRPLOCRRC;;;BA)      — Admin: Query, Start, Stop, Read (no Delete, no Modify config)
    #   (A;;CCLCSWRPLOCRRC;;;IU)      — Interactive User: Query, Start, Stop, Read
    #   (A;;CCLCSWRPLOCRRC;;;SU)      — Service: Query, Start, Stop, Read
    #   (A;;GA;;;SY)                  — SYSTEM: Generic All
    # Admin musi mieć Stop, inaczej maintenance/update-config niemożliwy. Dziecko bez admin
    # praw i tak nie ma dostępu do "Interactive User" w tym kontekście (UAC blokuje).
    # Brak Delete i ChangeConfig oznacza że nawet z admin ktoś nie skasuje service'u
    # bez explicit takeown — trzymamy minimalny security gain.
    $sd = "D:(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWRPLOCRRC;;;SU)(A;;GA;;;SY)"
    & sc.exe sdset $ServiceName $sd | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "sc sdset failed (exit $LASTEXITCODE) — service działa, ale Admin nadal może go zatrzymać"
    } else {
        Write-OK "Service DACL ustawione"
    }
}

function Step-AddSafeBootEntries {
    Write-Step "Dodaję SafeBoot entries"
    $keys = @(
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\vigil_watchdog',
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
    )
    foreach ($key in $keys) {
        if (-not (Test-Path $key)) {
            New-Item -Path $key -Force | Out-Null
        }
        # Default value w rejestrze przez Set-Item (nie Set-ItemProperty z Name='(Default)')
        Set-Item -Path $key -Value 'Service' -Force
    }
    Write-OK "SafeBoot Minimal + Network skonfigurowane"
}

function Step-RegisterGuardianTask {
    Write-Step "Tworzę Scheduled Task '$TaskName'"

    # Usuń istniejący jeśli jest (idempotencja)
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    $action = New-ScheduledTaskAction `
        -Execute 'powershell.exe' `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$guardianPs1`""

    # Trigger 1: At startup
    $tBoot = New-ScheduledTaskTrigger -AtStartup
    # Trigger 2: co 5 minut (Once + RepetitionInterval)
    $tCycle = New-ScheduledTaskTrigger -Once -At (Get-Date) `
        -RepetitionInterval (New-TimeSpan -Minutes 5)

    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

    Register-ScheduledTask -TaskName $TaskName `
        -Action $action -Trigger @($tBoot, $tCycle) `
        -Principal $principal -Settings $settings -Force | Out-Null

    Write-OK "Scheduled Task '$TaskName' (boot + co 5 min)"
}
function Step-RegisterTrayTask {
    # Tray uruchamia się przez Scheduled Task (at logon) zamiast HKCU\Run:
    #   - Task jest widoczny tylko w Task Scheduler, nie w Settings → Startup
    #   - Child nie ma uprawnień kasowania zadań systemowych (tylko Admin/SYSTEM)
    #   - Guardian może sprawdzić istnienie taska i alarmować gdy brakuje
    if (-not $ChildUsername) {
        Write-Warn "Brak -ChildUsername — pomijam rejestrację Vigil Tray task"
        Write-Warn "  Dziecko nie będzie miało ikonki pauzy w zasobniku do czasu reinstall z -ChildUsername"
        return
    }
    Write-Step "Tworzę Scheduled Task 'Vigil Tray' (autostart ikonki dla $ChildUsername)"

    $trayTaskName = "Vigil Tray"
    $existing = Get-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $trayTaskName -Confirm:$false
    }

    $action    = New-ScheduledTaskAction -Execute "`"$trayExe`""
    $trigger   = New-ScheduledTaskTrigger -AtLogon -User $ChildUsername
    $principal = New-ScheduledTaskPrincipal -UserId $ChildUsername `
                     -RunLevel Limited -LogonType Interactive
    $settings  = New-ScheduledTaskSettingsSet `
                     -AllowStartIfOnBatteries `
                     -DontStopIfGoingOnBatteries `
                     -ExecutionTimeLimit 0   # brak limitu — tray działa cały czas

    Register-ScheduledTask -TaskName $trayTaskName `
        -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null

    Write-OK "Task 'Vigil Tray' zarejestrowany (at logon user=$ChildUsername)"
}

function Step-RemoveTrayTask {
    $trayTaskName = "Vigil Tray"
    $existing = Get-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $trayTaskName -Confirm:$false
        Write-OK "Task 'Vigil Tray' usunięty"
    } else {
        Write-Skip "Task 'Vigil Tray' nie istnieje"
    }
}



function Step-DisableF8 {
    Write-Step "Disable F8 boot menu (bcdedit bootmenupolicy Standard)"
    try {
        & bcdedit /set "{current}" bootmenupolicy Standard | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-OK "F8 boot menu wyłączone (Standard policy)"
        } else {
            Write-Warn "bcdedit zwrócił exit $LASTEXITCODE"
        }
    } catch {
        Write-Warn "bcdedit failed: $_"
    }
}

function Step-DisableWinRE {
    if (-not $DisableWinRE) {
        Write-Step "Pomijam wyłączenie WinRE (-DisableWinRE nie podane)"
        return
    }
    Write-Step "Wyłączam Windows Recovery Environment (reagentc /disable)"
    try {
        & reagentc /disable | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-OK "WinRE wyłączone"
        } else {
            Write-Warn "reagentc zwrócił exit $LASTEXITCODE"
        }
    } catch {
        Write-Warn "reagentc failed: $_"
    }
}

function Get-UserSid {
    param([string]$Username)
    try {
        $acc = New-Object System.Security.Principal.NTAccount($Username)
        return $acc.Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        return $null
    }
}

function Set-PoliciesForRegistryHive {
    param(
        [string]$BasePath,  # np. "Registry::HKEY_USERS\S-1-5-21-..."  lub  "Registry::HKEY_LOCAL_MACHINE\PC_DefaultUser"
        [string]$Label      # tylko do logowania
    )

    # Software\Microsoft\Windows\CurrentVersion\Policies\System
    $policiesSystem = "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\System"
    if (-not (Test-Path $policiesSystem)) { New-Item -Path $policiesSystem -Force | Out-Null }
    Set-ItemProperty -Path $policiesSystem -Name 'DisableTaskMgr'        -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $policiesSystem -Name 'DisableRegistryTools'  -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $policiesSystem -Name 'DisableLockWorkstation' -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $policiesSystem -Name 'DisableChangePassword'  -Value 1 -Type DWord -Force

    # Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
    $policiesExplorer = "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
    if (-not (Test-Path $policiesExplorer)) { New-Item -Path $policiesExplorer -Force | Out-Null }
    # NoLogoff = 1 → ukryj "Wyloguj" w menu start (też SAS) — żeby dziecko nie sign-out'owało
    Set-ItemProperty -Path $policiesExplorer -Name 'NoLogoff' -Value 1 -Type DWord -Force

    # Wyłącz Sticky Keys / Filter Keys / Toggle Keys / Mouse Keys ACTIVATION SHORTCUTS.
    # Bez tego: 5×Shift = sticky keys dialog, 8s Shift hold = filter keys dialog, mogą zasłonić lockscreen
    # albo skierować focus do dialogu. Wyłącza tylko skróty aktywacyjne — sama funkcja accessibility
    # nadal może być włączona ręcznie z Control Panel.
    foreach ($accKey in @(
        @{Path="$BasePath\Control Panel\Accessibility\StickyKeys";       Value=506},
        @{Path="$BasePath\Control Panel\Accessibility\Keyboard Response"; Value=122},
        @{Path="$BasePath\Control Panel\Accessibility\ToggleKeys";        Value=58},
        @{Path="$BasePath\Control Panel\Accessibility\MouseKeys";         Value=58}
    )) {
        if (-not (Test-Path $accKey.Path)) { New-Item -Path $accKey.Path -Force | Out-Null }
        # "Flags" = 506: sticky on=2, available=512 wyłączone, hot-key off — kombinacja konkretnych bitów
        Set-ItemProperty -Path $accKey.Path -Name 'Flags' -Value $accKey.Value -Type String -Force
    }

    # Precision touchpad multi-finger gestures: 3-finger swipe up = Task View, 4-finger = Action Center.
    # KeyboardBlocker nie łapie tych (idą przez touch input subsystem). Wyłączamy w registry per-user.
    $touchpadPath = "$BasePath\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"
    if (-not (Test-Path $touchpadPath)) { New-Item -Path $touchpadPath -Force | Out-Null }
    Set-ItemProperty -Path $touchpadPath -Name 'ThreeFingerSlideEnabled' -Value 0 -Type DWord -Force
    Set-ItemProperty -Path $touchpadPath -Name 'FourFingerSlideEnabled'  -Value 0 -Type DWord -Force
    Set-ItemProperty -Path $touchpadPath -Name 'ThreeFingerTapEnabled'   -Value 0 -Type DWord -Force
    Set-ItemProperty -Path $touchpadPath -Name 'FourFingerTapEnabled'    -Value 0 -Type DWord -Force

    Write-OK "${Label}: DisableTaskMgr/RegistryTools/LockWorkstation/ChangePassword, NoLogoff, sticky/filter keys off, touchpad gestures off"
}

function Step-ApplyUserPolicies {
    if ($NoUserPolicies) {
        Write-Step "Pomijam policy changes (-NoUserPolicies)"
        return
    }
    Write-Step "Aplikuję user policies"

    # Strategia 1: Default User profile (NTUSER.DAT) — wpływa na NOWO TWORZONE konta.
    $defaultProfile = "$env:SystemDrive\Users\Default\NTUSER.DAT"
    if (-not (Test-Path $defaultProfile)) {
        Write-Warn "Default profile nie istnieje pod $defaultProfile — pomijam"
    } else {
        $tempKey = "HKLM\PC_DefaultUser"
        try {
            if ((Invoke-RegExe load $tempKey $defaultProfile) -ne 0) { throw "reg load failed" }

            Set-PoliciesForRegistryHive `
                -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_DefaultUser" `
                -Label "Default profile"
        } catch {
            Write-Warn "Default profile policy failed: $_"
        } finally {
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds 500
            [void](Invoke-RegExe unload $tempKey)
        }
    }

    # Strategia 2: jeśli podano -ChildUsername, aplikuj policy DIRECT do tego konta przez HKU\{SID}.
    # Wymaga że user jest zalogowany ALBO mountujemy jego NTUSER.DAT. Tu prościej: jeśli zalogowany,
    # ULOAD jest dostępny w HKEY_USERS. Jeśli nie zalogowany — mountujemy plik.
    if ($ChildUsername) {
        $sid = Get-UserSid $ChildUsername
        if (-not $sid) {
            Write-Warn "Nie udało się rozpoznać SID dla użytkownika '$ChildUsername' — pomijam direct apply"
        } else {
            $hkuPath = "Registry::HKEY_USERS\$sid"
            if (Test-Path $hkuPath) {
                # User zalogowany — HKU jest live, można pisać bezpośrednio
                try {
                    Set-PoliciesForRegistryHive -BasePath $hkuPath -Label "User '$ChildUsername' (live HKU)"
                } catch {
                    Write-Warn "Policy apply dla zalogowanego $ChildUsername failed: $_"
                }
            } else {
                # User niezalogowany — mountujemy jego NTUSER.DAT
                $childProfile = "$env:SystemDrive\Users\$ChildUsername\NTUSER.DAT"
                if (-not (Test-Path $childProfile)) {
                    Write-Warn "Profile $ChildUsername nie istnieje pod $childProfile — pomijam direct apply"
                } else {
                    $tempChildKey = "HKLM\PC_ChildUser"
                    try {
                        if ((Invoke-RegExe load $tempChildKey $childProfile) -ne 0) { throw "reg load failed" }
                        Set-PoliciesForRegistryHive `
                            -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_ChildUser" `
                            -Label "User '$ChildUsername' (offline NTUSER.DAT)"
                    } catch {
                        Write-Warn "Child profile policy failed: $_"
                    } finally {
                        [gc]::Collect()
                        [gc]::WaitForPendingFinalizers()
                        Start-Sleep -Milliseconds 500
                        [void](Invoke-RegExe unload $tempChildKey)
                    }
                }
            }
        }
    } else {
        Write-Warn "Brak -ChildUsername — policy zaaplikowane TYLKO w Default profile (efekt dla NOWYCH kont)."
        Write-Warn "Dla istniejącego konta dziecka uruchom installer ponownie z -ChildUsername <nazwa> lub zaloguj się jako dziecko i wykonaj policy ręcznie."
    }
}

# === Multi-strategy service control helpers ===
#
# Restrykcyjny SDDL na service (Step-SetServiceDacl) potrafi blokować Admin'a mimo że
# teoretycznie zezwala. Każda metoda stop/start może niezależnie zawieść (PS native, sc.exe,
# Stop-Process, PPL/AV). Fallback: SYSTEM Scheduled Task (DACL daje SY: GenericAll).
# Strategie próbujemy po kolei — każda kolejna mocniejsza, używamy pierwszej która zadziała.

function Stop-VigilService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-OK "Service nie istnieje — pomijam stop"
        return $true
    }
    if ($svc.Status -eq 'Stopped') {
        Write-OK "Service już Stopped"
        return $true
    }

    Write-Step "Zatrzymuję service $ServiceName (multi-strategy)"

    # 1. sc.exe stop + polling (max 12s).
    # Stop-Service (PS native) celowo pominięty: z naszym SDDL zawsze dostaje ACCESS_DENIED
    # (PS wymaga więcej praw niż samo SERVICE_STOP). sc.exe używa tej samej maski co SDDL.
    # Dajemy 12s bo vigil_watchdog ma shutdown handler (graceful disconnect od lockscreen, IPC cleanup).
    try {
        & sc.exe stop $ServiceName 2>&1 | Out-Null
        $deadline = (Get-Date).AddSeconds(12)
        do {
            Start-Sleep -Milliseconds 500
            $status = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
        } while ($status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        if ($status -eq 'Stopped') {
            Write-OK "Stopped via sc.exe stop"
            return $true
        }
    } catch { Write-Host "    sc.exe stop: $_" -ForegroundColor DarkGray }

    # 2. Kill process bezpośrednio (Admin może z SeDebugPrivilege)
    try {
        $proc = Get-Process -Name vigil_watchdog -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
            # SCM potrzebuje chwili żeby zarejestrować śmierć procesu
            $deadline = (Get-Date).AddSeconds(8)
            do { Start-Sleep -Milliseconds 500 }
            while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
            if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq 'Stopped') {
                Write-OK "Stopped via Stop-Process (kill)"
                return $true
            }
        }
    } catch { Write-Host "    Stop-Process: $_" -ForegroundColor DarkGray }

    # 3. SYSTEM scheduled task — omija wszystkie DACL (ostatnia deska ratunku)
    try {
        $taskName = "_pc_stop_$(Get-Random)"
        & schtasks.exe /Create /TN $taskName /SC ONCE /ST 23:59 /RU SYSTEM /TR "sc.exe stop $ServiceName" /F 2>&1 | Out-Null
        & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null
        $deadline = (Get-Date).AddSeconds(10)
        do { Start-Sleep -Milliseconds 500 }
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
        if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq 'Stopped') {
            Write-OK "Stopped via SYSTEM scheduled task"
            return $true
        }
    } catch { Write-Host "    SYSTEM task: $_" -ForegroundColor DarkGray }

    throw "Nie udało się zatrzymać $ServiceName (próbowano 3 strategie). Manual: reboot komputera."
}

function Start-VigilService {
    Write-Step "Uruchamiam service $ServiceName (multi-strategy)"

    # 1. sc.exe start + polling
    try {
        & sc.exe start $ServiceName 2>&1 | Out-Null
        $deadline = (Get-Date).AddSeconds(12)
        do { Start-Sleep -Milliseconds 500 }
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running' -and (Get-Date) -lt $deadline)
        if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq 'Running') {
            Write-OK "Started via sc.exe start"
            return $true
        }
    } catch { Write-Host "    sc.exe start: $_" -ForegroundColor DarkGray }

    # 2. Start-Service (PS native)
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds(10)
        do { Start-Sleep -Milliseconds 500 }
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running' -and (Get-Date) -lt $deadline)
        if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq 'Running') {
            Write-OK "Started via Start-Service"
            return $true
        }
    } catch { Write-Host "    Start-Service: $_" -ForegroundColor DarkGray }

    # 3. SYSTEM scheduled task
    try {
        $taskName = "_pc_start_$(Get-Random)"
        & schtasks.exe /Create /TN $taskName /SC ONCE /ST 23:59 /RU SYSTEM /TR "sc.exe start $ServiceName" /F 2>&1 | Out-Null
        & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null
        $deadline = (Get-Date).AddSeconds(12)
        do { Start-Sleep -Milliseconds 500 }
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running' -and (Get-Date) -lt $deadline)
        & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
        if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq 'Running') {
            Write-OK "Started via SYSTEM scheduled task"
            return $true
        }
    } catch { Write-Host "    SYSTEM task: $_" -ForegroundColor DarkGray }

    Write-Warn "Service nie wstał — sprawdź Event Log (Vigil Watchdog)"
    return $false
}

function Step-UnlockPermissions {
    # Tymczasowe — daje Adminowi pełen dostęp do $InstallDir żeby update-config mógł pisać.
    # Po zakończeniu update wraca Step-LockDownPermissions z restrykcyjnym ACL.
    Write-Step "Tymczasowo otwieram ACL na czas update"
    try {
        & takeown.exe /F $InstallDir /R /D Y 2>&1 | Out-Null
        & icacls.exe $InstallDir /grant '*S-1-5-32-544:(F)' /T /C 2>&1 | Out-Null
        Write-OK "ACL otwarte (Admin Full tymczasowo)"
    } catch {
        Write-Warn "Nie udało się otworzyć ACL: $_ — może update będzie failować"
    }
}

function Step-StartService {
    if (-not (Start-VigilService)) {
        throw "Service nie wstał po wszystkich strategiach"
    }
}

function Step-SmokeTest {
    Write-Step "Smoke test: sprawdzam status service (15s wait na startup)"
    Start-Sleep -Seconds 15

    # Podstawowy check: service musi być Running
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-OK "Service $ServiceName działa (Running)"
    } else {
        $status = if ($svc) { $svc.Status } else { 'nie istnieje' }
        Write-Warn "Service $ServiceName nie Running (status: $status)"
        Write-Warn "Sprawdź: Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Vigil Watchdog'} -MaxEvents 20"
        return
    }

    # Opcjonalnie: event log (source może jeszcze nie istnieć jeśli watchdog nie zdążył zalogować)
    # Get-WinEvent z ProviderName rzuca gdy source nie jest zarejestrowany — dlatego try/catch
    try {
        $events = Get-WinEvent -FilterHashtable @{
            LogName      = 'Application'
            ProviderName = 'Vigil Watchdog'
            StartTime    = (Get-Date).AddMinutes(-2)
        } -MaxEvents 5 -ErrorAction Stop

        $startupEvent = $events | Where-Object { $_.Message -match 'Vigil uruchomiony' } | Select-Object -First 1
        if ($startupEvent) {
            Write-OK "Vigil zalogował startup w Event Log: $($startupEvent.TimeCreated)"
        }
    } catch {
        # Source 'Vigil Watchdog' może jeszcze nie istnieć — service Running wystarczy do oceny
    }
}

# === Uninstall steps ===

function Step-StopAndRemoveService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-OK "Service nie istnieje — pomijam"
        return
    }

    # Stop przez multi-strategy (DACL service'u może blokować klasyczny stop)
    $null = Stop-VigilService

    Write-Step "Usuwam service $ServiceName"
    # sc.exe delete też może blokować przez DACL — fallback na SYSTEM task
    & sc.exe delete $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        # SYSTEM task fallback
        try {
            $taskName = "_pc_del_$(Get-Random)"
            & schtasks.exe /Create /TN $taskName /SC ONCE /ST 23:59 /RU SYSTEM /TR "sc.exe delete $ServiceName" /F 2>&1 | Out-Null
            & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null
            Start-Sleep -Seconds 4
            & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
        } catch { }
        Start-Sleep -Seconds 2
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Warn "Service $ServiceName dalej istnieje po próbie usunięcia — może wymagać reboota"
            return
        }
    }
    Write-OK "Service usunięty"
}

function Step-UnregisterGuardianTask {
    Write-Step "Usuwam Scheduled Task '$TaskName'"
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-OK "Task usunięty"
    } else {
        Write-OK "Task nie istnieje — pomijam"
    }
}

function Step-RemoveSafeBoot {
    Write-Step "Usuwam SafeBoot entries"
    $keys = @(
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\vigil_watchdog',
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\vigil_watchdog'
    )
    foreach ($key in $keys) {
        if (Test-Path $key) {
            Remove-Item -Path $key -Force -Recurse -ErrorAction SilentlyContinue
        }
    }
    Write-OK "SafeBoot entries usunięte"
}

function Step-RemoveInstallDir {
    Write-Step "Usuwam $InstallDir"
    if (Test-Path $InstallDir) {
        # Najpierw przejmij ownership i grant Admin full, bo ACL z install nie pozwala usunąć.
        # UWAGA: używamy SID *S-1-5-32-544 zamiast nazwy 'Administrators', bo na polskim
        # Windowsie grupa nazywa się 'Administratorzy' i icacls /grant zwraca "Nie zostało
        # wykonane mapowanie między nazwami kont a identyfikatorami zabezpieczeń".
        # SID jest uniwersalny niezależnie od języka systemu.
        try {
            & takeown.exe /F $InstallDir /R /D Y 2>&1 | Out-Null
            & icacls.exe $InstallDir /grant '*S-1-5-32-544:(F)' /T /C 2>&1 | Out-Null
            # Resetuj inheritance żeby nadać świeże ACL z nowymi prawami
            & icacls.exe $InstallDir /inheritance:e /T /C 2>&1 | Out-Null
        } catch {
            Write-Warn "takeown/icacls failed: $_"
        }
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $InstallDir) {
            Write-Warn "$InstallDir nadal istnieje — manual cleanup wymagany"
        } else {
            Write-OK "$InstallDir usunięty"
        }
    } else {
        Write-OK "$InstallDir nie istnieje — pomijam"
    }
}

function Clear-PoliciesFromRegistryHive {
    param([string]$BasePath)

    $policiesSystem = "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\System"
    if (Test-Path $policiesSystem) {
        # DisableRegistryTools NAJPIERW — żeby jego usunięcie odblokuje ewentualne dalsze operacje
        foreach ($name in @('DisableRegistryTools','DisableTaskMgr','DisableLockWorkstation','DisableChangePassword')) {
            Remove-ItemProperty -Path $policiesSystem -Name $name -ErrorAction SilentlyContinue
        }
    }
    $policiesExplorer = "$BasePath\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
    if (Test-Path $policiesExplorer) {
        Remove-ItemProperty -Path $policiesExplorer -Name 'NoLogoff' -ErrorAction SilentlyContinue
    }
    # Sticky/Filter/Toggle/Mouse keys flags — przywracamy domyślne wartości Windows
    foreach ($accKey in @(
        @{Path="$BasePath\Control Panel\Accessibility\StickyKeys";        Default=510},
        @{Path="$BasePath\Control Panel\Accessibility\Keyboard Response"; Default=126},
        @{Path="$BasePath\Control Panel\Accessibility\ToggleKeys";        Default=62},
        @{Path="$BasePath\Control Panel\Accessibility\MouseKeys";         Default=62}
    )) {
        if (Test-Path $accKey.Path) {
            Set-ItemProperty -Path $accKey.Path -Name 'Flags' -Value $accKey.Default -Type String -ErrorAction SilentlyContinue
        }
    }
    # Touchpad gestures — przywracamy 1 (default Windows)
    $touchpadPath = "$BasePath\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"
    if (Test-Path $touchpadPath) {
        foreach ($name in @('ThreeFingerSlideEnabled','FourFingerSlideEnabled','ThreeFingerTapEnabled','FourFingerTapEnabled')) {
            Set-ItemProperty -Path $touchpadPath -Name $name -Value 1 -Type DWord -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-RegExe {
    # reg.exe reports errors on stderr. Under $ErrorActionPreference='Stop', PS 5.1 turns redirected
    # native stderr into a terminating error — fatal inside 'finally'. Returns the exit code instead.
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $RegArgs)
    $ErrorActionPreference = 'Continue'
    $out = & reg.exe @RegArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "reg.exe $($RegArgs[0]) $($RegArgs[1]) failed (exit $LASTEXITCODE): $(($out | Out-String).Trim())"
    }
    return $LASTEXITCODE
}

function Get-LoadedUserHives {
    # HKEY_USERS has a subkey per loaded hive; real users are S-1-5-21-*, '_Classes' are not profiles.
    Get-ChildItem -Path 'Registry::HKEY_USERS\' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^S-1-5-21-' -and $_.PSChildName -notmatch '_Classes$' } |
        ForEach-Object { $_.PSChildName }
}

function Step-ReverseUserPolicies {
    if ($NoUserPolicies) {
        Write-Step "Pomijam reverse policy (-NoUserPolicies)"
        return
    }
    Write-Step "Cofam user policies"

    # 1. Loaded hives first, through the PowerShell registry provider (not subject to
    #    DisableRegistryTools). With one account (D12) the user running this script is the child,
    #    so this also lifts DisableRegistryTools for ourselves — reg.exe below needs that.
    foreach ($sid in @(Get-LoadedUserHives)) {
        Clear-PoliciesFromRegistryHive -BasePath "Registry::HKEY_USERS\$sid"
        Write-OK "Live HKU ${sid}: policies removed"
    }

    # 2. Default profile (affects accounts created later).
    $defaultProfile = "$env:SystemDrive\Users\Default\NTUSER.DAT"
    if (Test-Path $defaultProfile) {
        $tempKey = "HKLM\PC_DefaultUser"
        if ((Invoke-RegExe load $tempKey $defaultProfile) -eq 0) {
            try {
                Clear-PoliciesFromRegistryHive -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_DefaultUser"
                Write-OK "Default profile: policies removed"
            } finally {
                [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Milliseconds 500
                [void](Invoke-RegExe unload $tempKey)
            }
        }
    }

    # 3. Named child account that is not logged in (offline NTUSER.DAT).
    if ($ChildUsername) {
        $sid = Get-UserSid $ChildUsername
        $childProfile = "$env:SystemDrive\Users\$ChildUsername\NTUSER.DAT"
        if ($sid -and -not (Test-Path "Registry::HKEY_USERS\$sid") -and (Test-Path $childProfile)) {
            $tempChildKey = "HKLM\PC_ChildUser"
            if ((Invoke-RegExe load $tempChildKey $childProfile) -eq 0) {
                try {
                    Clear-PoliciesFromRegistryHive -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_ChildUser"
                    Write-OK "User '$ChildUsername' (offline): policies removed"
                } finally {
                    [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Milliseconds 500
                    [void](Invoke-RegExe unload $tempChildKey)
                }
            }
        }
    }
}

# === Mode dispatch ===

function Invoke-Install {
    Write-Host "`n[ INSTALL ]`n" -ForegroundColor Magenta

    Step-RequireAdmin
    Step-CheckSourceFiles
    Step-ValidateInstallParams
    Step-CreateInstallDir
    Step-CopyBinaries
    Step-WriteGuardianHash
    Step-WriteVigilToml
    Step-WriteSigningDat
    Step-RemoveLegacyEmergencyFiles
    Step-RegisterService
    Step-SetServiceDacl
    Step-AddSafeBootEntries
    Step-RegisterGuardianTask
    Step-RegisterTrayTask
    Step-DisableF8
    Step-DisableWinRE
    Step-ApplyUserPolicies
    # Po wszystkich operacjach które piszą do $InstallDir — restrykcyjne ACL.
    # Po tym kroku tylko SYSTEM ma write; Administrators i users tylko read+execute.
    Step-LockDownPermissions
    Step-StartService
    Step-SmokeTest

    Write-Host "`n[ INSTALL DONE ]`n" -ForegroundColor Green
}

function Invoke-Uninstall {
    Write-Host "`n[ UNINSTALL ]`n" -ForegroundColor Magenta

    Step-RequireAdmin
    Step-StopAndRemoveService
    Step-UnregisterGuardianTask
    Step-RemoveTrayTask
    Step-RemoveSafeBoot
    Step-ReverseUserPolicies
    Step-RemoveInstallDir

    Write-Host "`n[ UNINSTALL DONE ]`n" -ForegroundColor Green
    Write-Warn "F8 boot menu i WinRE NIE są przywracane — Standard bootmenupolicy to default na Win10+, a WinRE zostawiamy na wszelki wypadek (przywróć ręcznie przez 'reagentc /enable' jeśli było wyłączone)"
}

function Invoke-UpdateConfig {
    Write-Host "`n[ UPDATE-CONFIG ]`n" -ForegroundColor Magenta

    Step-RequireAdmin
    if (-not (Test-Path $InstallDir)) {
        throw "$InstallDir nie istnieje — najpierw -Mode install"
    }
    if (-not $ServerUrl -and -not $SigningKey) {
        throw "Podaj co najmniej jeden z: -ServerUrl, -SigningKey"
    }

    # Załaduj istniejące values z vigil.toml gdy parametry nie podane
    $existingToml = if (Test-Path $vigilToml) { Get-Content $vigilToml -Raw } else { '' }
    if (-not $ServerUrl) {
        if ($existingToml -match 'server_url\s*=\s*"([^"]*)"') { $script:ServerUrl = $matches[1] }
        else { throw "ServerUrl nie podany i nie znaleziony w vigil.toml" }
    } else { $script:ServerUrl = $ServerUrl }

    $null = Stop-VigilService

    # Tymczasowo otwórz ACL żeby kolejne kroki mogły pisać
    Step-UnlockPermissions

    try {
        # Zawsze przepisz vigil.toml: usuwa bearer_token ze starych instalacji (protokół v1).
        Step-WriteVigilToml
        if ($SigningKey) {
            $keyBytes = ConvertFrom-Base64UrlSafe $SigningKey
            if ($keyBytes.Length -lt 16) {
                throw "SigningKey po dekodowaniu ma tylko $($keyBytes.Length) bajtów"
            }
            Step-WriteSigningDat
        }
        Step-RemoveLegacyEmergencyFiles
    } finally {
        # Niezależnie od success/fail — przywróć restrykcyjne ACL
        Step-LockDownPermissions
    }

    $started = Start-VigilService
    if (-not $started) {
        Write-Warn "Service nie wstał po update-config — sprawdź Event Log"
    }

    Write-Host "`n[ UPDATE-CONFIG DONE ]`n" -ForegroundColor Green
}

function Invoke-Reinstall {
    Invoke-Uninstall
    Invoke-Install
}

# === Main ===

try {
    switch ($Mode) {
        'install'       { Invoke-Install }
        'uninstall'     { Invoke-Uninstall }
        'update-config' { Invoke-UpdateConfig }
        'reinstall'     { Invoke-Reinstall }
    }
} catch {
    Write-Host "`n[ FAILED ]`n" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
