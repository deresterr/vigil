<#
.SYNOPSIS
    Installer dla Vigil.

.DESCRIPTION
    Tryby:
        -Mode install        — fresh install (wymaga binarek; bez parametrów wchodzi w tryb interaktywny)
        -Mode uninstall      — remove all (service, task, folder, policies)
        -Mode update-config  — zmiana adresu serwera (vigil.toml) i/lub ponowne parowanie kodem
        -Mode reinstall      — uninstall + install
        -Mode verify         — sprawdza instalację (PASS/FAIL, kod wyjścia 1 przy błędzie); bez instalacji
                               sprawdza, czy deinstalacja nic nie zostawiła. Niczego nie zmienia.

    Wymaga uruchomienia jako Administrator. Zaprojektowany jako idempotentny:
    `install` na już zainstalowanym systemie nadpisuje config i restartuje service.

.PARAMETER Mode
    install | uninstall | update-config | reinstall | verify

.PARAMETER SourceDir
    Default: bieżący katalog skryptu.

.PARAMETER ServerUrl
    Adres servera np. http://192.168.0.2:8088. Bez niego (i bez -PairingCode) install pyta interaktywnie.

.PARAMETER PairingCode
    Jednorazowy kod parowania z panelu rodzica (ważny 10 min): Ustawienia → „Dodaj komputer” dla nowego
    PC, „Sparuj ponownie” przy istniejącym. Serwer w zamian wydaje klucz podpisujący tego komputera.

.PARAMETER DisableWinRE
    Switch — opcjonalne wyłączenie Windows Recovery Environment (`reagentc /disable`).

.PARAMETER NoUserPolicies
    Switch — pomiń policy changes (DisableTaskMgr w Default User profile).
    Użyteczne do testów bez modyfikacji systemu globalnie.

.PARAMETER ChildUsername
    Nazwa konta dziecka, gdy na PC jest kilka kont. Zwykle niepotrzebna: installer sam znajduje jedyne
    konto użytkownika z profilem (D12) i na nie nakłada policy (DisableTaskMgr, DisableChangePassword,
    skróty ułatwień dostępu, gesty touchpada). Przy kilku kontach pyta (tryb interaktywny).

.EXAMPLE
    .\vigil_install.ps1 -Mode install `
        -SourceDir 'D:\pc-build' `
        -ServerUrl 'http://192.168.0.2:8088' `
        -PairingCode '1234 5678'

.EXAMPLE
    .\vigil_install.ps1 -Mode uninstall

.EXAMPLE
    .\vigil_install.ps1 -Mode update-config `
        -ServerUrl 'http://192.168.0.5:8088' `
        -PairingCode '1234 5678'
#>

[CmdletBinding()]
param(
    [ValidateSet('install','uninstall','update-config','reinstall','verify')]
    [string] $Mode = 'install',

    [string] $SourceDir = '',
    [string] $InstallDir = "C:\ProgramData\Vigil",
    [string] $ServiceName = "vigil_watchdog",
    [string] $TaskName = "Vigil Guardian",

    [string] $ServerUrl,
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
$uiExe         = Join-Path $InstallDir "vigil_ui.exe"
$guardianPs1   = Join-Path $InstallDir "vigil_guardian.ps1"
$vigilToml     = Join-Path $InstallDir "vigil.toml"
$signingDat    = Join-Path $InstallDir "signing.dat"
$guardianDat   = Join-Path $InstallDir "guardian.dat"
$backupDir     = Join-Path $InstallDir "backup"

# DPAPI secrets: own ACL, see Set-InstallFileAcls (totp.dat is written by the watchdog)
$SecretFiles   = @('signing.dat', 'totp.dat')

# Service DACL, see Step-SetServiceDacl
$ServiceSddl   = "D:(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWRPLOCRRC;;;SU)(A;;GA;;;SY)"

# Per-user registry values: set on install, checked by -Mode verify, reset on uninstall.
# Key is relative to the hive root; Reset is the uninstall value ($null = remove the value).
# Only what closes a way out of the lock screen (#37). The parent maintains the PC through the same
# account (D12), so each of these hits the parent too; against an elevated child they are friction only.
$UserPolicyValues = @(
    # Ctrl+Alt+Del -> Task Manager would run programs over the overlay
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name = 'DisableTaskMgr';        Type = 'DWord'; Value = 1; Reset = $null }
    # the child must not change the password of the shared account
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name = 'DisableChangePassword'; Type = 'DWord'; Value = 1; Reset = $null }
    # Activation shortcuts of Sticky/Filter/Toggle/Mouse Keys (5x Shift, 8 s Shift...) open dialogs that
    # can cover the lock screen or take focus. Only the shortcuts; the features stay in Control Panel.
    @{ Key = 'Control Panel\Accessibility\StickyKeys';        Name = 'Flags'; Type = 'String'; Value = '506'; Reset = '510' }
    @{ Key = 'Control Panel\Accessibility\Keyboard Response'; Name = 'Flags'; Type = 'String'; Value = '122'; Reset = '126' }
    @{ Key = 'Control Panel\Accessibility\ToggleKeys';        Name = 'Flags'; Type = 'String'; Value = '58';  Reset = '62' }
    @{ Key = 'Control Panel\Accessibility\MouseKeys';         Name = 'Flags'; Type = 'String'; Value = '58';  Reset = '62' }
    # Precision touchpad gestures (Task View, Action Center) bypass the keyboard hook
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad'; Name = 'ThreeFingerSlideEnabled'; Type = 'DWord'; Value = 0; Reset = 1 }
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad'; Name = 'FourFingerSlideEnabled';  Type = 'DWord'; Value = 0; Reset = 1 }
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad'; Name = 'ThreeFingerTapEnabled';   Type = 'DWord'; Value = 0; Reset = 1 }
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad'; Name = 'FourFingerTapEnabled';    Type = 'DWord'; Value = 0; Reset = 1 }
)
# Set by versions before v0.6, dropped in #37 (no escape from the overlay, only a nuisance for the
# parent): removed on install and uninstall. DisableRegistryTools comes first: it blocks reg.exe.
$LegacyUserPolicies = @(
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableRegistryTools' }
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableLockWorkstation' }
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoLogoff' }
)

# Source files
$srcFiles = @{
    'vigil_watchdog.exe'    = (Join-Path $SourceDir 'vigil_watchdog.exe')
    'vigil_ui.exe'          = (Join-Path $SourceDir 'vigil_ui.exe')
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
        Write-Host "  Kod parowania (panel rodzica -> Ustawienia -> 'Dodaj komputer' albo 'Sparuj ponownie'):"
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
            throw "Brak aktywnego kodu parowania (wygasł albo został użyty).`n  Wygeneruj nowy kod w panelu rodzica (Ustawienia -> 'Dodaj komputer')."
        }
        if ($status -eq 401) {
            throw "Nieprawidłowy kod parowania. Po 5 błędnych próbach kod przestaje działać."
        }
        throw "Parowanie nie powiodło się: $_"
    }
}

function Get-InteractiveAccounts {
    # Accounts with a profile on this PC: S-1-5-21-* in ProfileList, minus the built-ins by RID (500
    # Administrator, 501 Guest, 503 DefaultAccount, 504 WDAGUtilityAccount). By SID, never by name: Polish
    # Windows calls Guest "Gość", and a Microsoft account's profile folder is not its user name.
    $list = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    foreach ($key in @(Get-ChildItem -Path $list -ErrorAction SilentlyContinue)) {
        $sid = $key.PSChildName
        if ($sid -notmatch '^S-1-5-21-[\d-]+-(\d+)$') { continue }
        if ([int]$matches[1] -in 500, 501, 503, 504) { continue }
        # A disabled local account (e.g. a leftover defaultuser0 from Windows setup) is nobody's.
        $local = Get-LocalUser -SID $sid -ErrorAction SilentlyContinue
        if ($local -and -not $local.Enabled) { continue }
        $profilePath = (Get-ItemProperty -Path $key.PSPath -Name ProfileImagePath -ErrorAction SilentlyContinue).ProfileImagePath
        if (-not $profilePath -or -not (Test-Path (Join-Path $profilePath 'NTUSER.DAT'))) { continue }
        $name = try {
            (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate([System.Security.Principal.NTAccount]).Value
        } catch { $sid }
        [pscustomobject]@{ Sid = $sid; Name = $name; Profile = $profilePath }
    }
}

function Resolve-ChildAccount {
    # Sets $script:ChildAccount (Sid, Name, Profile) or leaves it $null (policies then only in the
    # Default profile). D12: the child's account is the only interactive one, so usually no question.
    param([switch]$AllowPrompt)
    Write-Step "Konto dziecka"
    $accounts = @(Get-InteractiveAccounts)
    if ($ChildUsername) {
        $sid = Get-UserSid $ChildUsername
        $script:ChildAccount = $accounts | Where-Object { $_.Sid -eq $sid } | Select-Object -First 1
        if (-not $script:ChildAccount) {
            throw "Konto '$ChildUsername' nie istnieje albo nie ma profilu na tym komputerze (zaloguj się na nie raz)."
        }
    } elseif ($accounts.Count -eq 1) {
        $script:ChildAccount = $accounts[0]
    } elseif ($accounts.Count -eq 0) {
        Write-Warn "Nie znaleziono konta użytkownika z profilem — policy tylko w Default profile (nowe konta)."
        return
    } elseif ($AllowPrompt) {
        Write-Host "  Na komputerze jest kilka kont:"
        for ($i = 0; $i -lt $accounts.Count; $i++) { Write-Host "  [$($i+1)] $($accounts[$i].Name)" }
        $choice = Read-Host "  Numer konta dziecka"
        $idx = 0
        if (-not [int]::TryParse($choice, [ref]$idx) -or $idx -lt 1 -or $idx -gt $accounts.Count) {
            throw "Nieprawidłowy numer konta."
        }
        $script:ChildAccount = $accounts[$idx - 1]
    } else {
        Write-Warn "Kilka kont użytkowników, a nie podano -ChildUsername — policy tylko w Default profile."
        return
    }
    Write-OK "Konto dziecka: $($script:ChildAccount.Name)"
}

function Step-ValidateInstallParams {
    # Everything on the command line: no questions (the account is resolved, never asked for).
    if ($ServerUrl -and $PairingCode) {
        $script:ServerUrl = Normalize-ServerUrl $ServerUrl
        if (-not (Test-ServerReachable $script:ServerUrl)) {
            throw "Nie można połączyć się z serwerem $($script:ServerUrl)."
        }
        Get-CredentialsFromServer -serverUrl $script:ServerUrl -code $PairingCode
        Resolve-ChildAccount
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

    Get-CredentialsFromServer -serverUrl $script:ServerUrl -code $PairingCode
    Resolve-ChildAccount -AllowPrompt

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

        # Authenticated Users read+execute (the child too: SYSTEM spawns vigil_ui.exe in the
        # child's session with the child's token)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier(
                [System.Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)),
            [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
            ('ContainerInherit,ObjectInherit'),
            'None',
            'Allow')
        $acl.AddAccessRule($rule)

        Set-Acl -Path $InstallDir -AclObject $acl
        Set-InstallFileAcls
        Write-OK "ACL ustawione (SYSTEM full, Admin modify, AuthUsers r+x; sekrety: SYSTEM full, Admin read)"
    } catch {
        Write-Warn "Nie udało się ustawić ACL: $_ — kontynuuję z default"
    }
}

function Set-InstallFileAcls {
    # Everything below $InstallDir: inherited ACEs only — Step-UnlockPermissions (update-config) granted
    # Admin Full on every file explicitly, and Set-Acl on the folder does not touch that. Except the
    # DPAPI secrets ($SecretFiles): LocalMachine DPAPI is decryptable by any local account, so the file
    # ACL is what keeps them from a non-elevated process — SYSTEM full, Administrators read, as the
    # watchdog's RestrictFileAcl writes them. Friction: an elevated admin can still read them.
    $system = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $admins = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    foreach ($item in @(Get-ChildItem -Path $InstallDir -Recurse -Force)) {
        $acl = Get-Acl $item.FullName
        foreach ($rule in @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
            $acl.RemoveAccessRuleSpecific($rule)
        }
        if (-not $item.PSIsContainer -and $item.Name -in $SecretFiles) {
            $acl.SetAccessRuleProtection($true, $false)
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system,
                [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow)))
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins,
                [System.Security.AccessControl.FileSystemRights]::Read, [System.Security.AccessControl.AccessControlType]::Allow)))
        } else {
            $acl.SetAccessRuleProtection($false, $false)
        }
        Set-Acl -Path $item.FullName -AclObject $acl
    }
}

function Step-CopyBinaries {
    Write-Step "Kopiuję binarki do $InstallDir"
    Stop-VigilUiProcesses  # a running vigil_ui keeps its exe locked
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

function Stop-VigilUiProcesses {
    # vigil_ui runs in the child's session and holds its exe open; also the separate lockscreen/tray
    # of versions before D8. An elevated installer may kill them despite their process DACL (BA full).
    foreach ($name in @('vigil_ui', 'vigil_lockscreen', 'vigil_tray')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            Write-OK "Zatrzymano proces $name (pid $($_.Id))"
        }
    }
}

function Step-StopUiProcesses {
    Write-Step "Zatrzymuję procesy UI Vigila w sesji użytkownika"
    Stop-VigilUiProcesses
}

function Step-RemoveLegacyFiles {
    # Leftovers of older installs:
    #  - before v0.5 offline unlock used an emergency password (emergency.dat, readable by the child)
    #    and a 24 h bypass.flag in C:\Users\Public\Vigil (forgeable). Gone (#9): the parent's TOTP
    #    code works offline too.
    #  - before D8 (#35) lock screen and tray were two exes; vigil_ui.exe replaced both, and the
    #    watchdog starts it instead of the "Vigil Tray" task (removed in Step-RemoveTrayTask).
    $legacy = @((Join-Path $InstallDir "emergency.dat"), "$env:PUBLIC\Vigil")
    # Old exes only once the new binaries are in place (update-config keeps the installed ones).
    if (Test-Path $uiExe) {
        Stop-VigilUiProcesses
        $legacy += (Join-Path $InstallDir "vigil_lockscreen.exe"), (Join-Path $InstallDir "vigil_tray.exe")
    }
    foreach ($path in $legacy) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
            Write-OK "Usunięto pozostałość starszej wersji: $path"
        }
    }
}

function Step-RegisterService {
    Write-Step "Rejestruję service $ServiceName"

    # Idempotencja: jeśli istnieje, delete najpierw
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        $null = Stop-VigilService
        if (-not (Remove-VigilServiceEntry)) {
            throw "Nie udało się usunąć istniejącego service $ServiceName — reboot i ponów instalację."
        }
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
    Write-Step "Ograniczam DACL service'u (Admin: query/start, bez stop i delete)"
    # $ServiceSddl: BA, IU, SU get CC LC SW RP LO CR RC = query config/status, enumerate dependents,
    # start, interrogate, user control, read. No WP (stop), DT (pause), DC (change config), SD (delete),
    # WD/WO. SYSTEM: GenericAll. Friction only: an admin can kill the process or act as SYSTEM, which is
    # exactly what this installer does (Invoke-ScAsSystem). Detection is the server's job (tamper.py).
    & sc.exe sdset $ServiceName $ServiceSddl | Out-Null
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
function Step-RemoveTrayTask {
    # Before D8 (#35) the tray was started by this task at logon; the watchdog starts vigil_ui now.
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

    # Values and reasons: $UserPolicyValues / $LegacyUserPolicies at the top.
    foreach ($p in $UserPolicyValues) {
        $path = "$BasePath\$($p.Key)"
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name $p.Name -Value $p.Value -Type $p.Type -Force
    }
    foreach ($p in $LegacyUserPolicies) {
        $path = "$BasePath\$($p.Key)"
        if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $p.Name -ErrorAction SilentlyContinue }
    }

    Write-OK "${Label}: DisableTaskMgr, DisableChangePassword, skróty sticky/filter keys off, gesty touchpada off"
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

    # Strategia 2: the child's account (Resolve-ChildAccount) directly: its live hive when it is logged
    # on, otherwise its NTUSER.DAT mounted from the profile folder ProfileList names.
    if ($script:ChildAccount) {
        $acc = $script:ChildAccount
        $hkuPath = "Registry::HKEY_USERS\$($acc.Sid)"
        if (Test-Path $hkuPath) {
            try {
                Set-PoliciesForRegistryHive -BasePath $hkuPath -Label "User '$($acc.Name)' (live HKU)"
            } catch {
                Write-Warn "Policy apply dla zalogowanego $($acc.Name) failed: $_"
            }
        } else {
            $tempChildKey = "HKLM\PC_ChildUser"
            try {
                if ((Invoke-RegExe load $tempChildKey (Join-Path $acc.Profile 'NTUSER.DAT')) -ne 0) { throw "reg load failed" }
                Set-PoliciesForRegistryHive `
                    -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_ChildUser" `
                    -Label "User '$($acc.Name)' (offline NTUSER.DAT)"
            } catch {
                Write-Warn "Child profile policy failed: $_"
            } finally {
                [gc]::Collect()
                [gc]::WaitForPendingFinalizers()
                Start-Sleep -Milliseconds 500
                [void](Invoke-RegExe unload $tempChildKey)
            }
        }
    } else {
        Write-Warn "Brak konta dziecka — policy zaaplikowane TYLKO w Default profile (efekt dla NOWYCH kont)."
    }
}

# === Service control helpers ===
#
# The service DACL ($ServiceSddl) gives administrators query/start only: no stop (WP), no delete (SD).
# Stop and delete therefore go through a one-off SYSTEM task (SY: GenericAll). That is a normal SCM
# stop, so the watchdog says goodbye ("service_stop") and the server logs tamper_service_stopped
# instead of tamper_watchdog_restarted; killing the process is only the fallback.

function Wait-ServiceStatus {
    param([string]$Status, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $current = if ($svc) { [string]$svc.Status } else { 'Missing' }
        if ($current -eq $Status) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Invoke-ScAsSystem {
    # Runs "sc.exe <ScArgs>" as SYSTEM through a one-off scheduled task and removes the task.
    # Returns when the task has finished (sc.exe itself returns at once; callers wait for the state).
    param([string]$ScArgs)
    $name = "_vigil_sc_$(Get-Random)"
    $action = New-ScheduledTaskAction -Execute 'sc.exe' -Argument $ScArgs
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
    # Default task settings refuse to start on battery power: a laptop would fall through to kill.
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds(10)
        do { Start-Sleep -Milliseconds 300 }
        while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $deadline)
    } finally {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
}

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

    Write-Step "Zatrzymuję service $ServiceName"

    # 1. Normal stop as SYSTEM. 15 s: the watchdog's goodbye waits up to 3 s for the server.
    try {
        Invoke-ScAsSystem "stop $ServiceName"
        if (Wait-ServiceStatus 'Stopped' 15) {
            Write-OK "Zatrzymany (stop jako SYSTEM, watchdog wysłał pożegnanie)"
            return $true
        }
    } catch { Write-Host "    stop jako SYSTEM: $_" -ForegroundColor DarkGray }

    # 2. Kill (no goodbye: the server sees a restart without one). Admins may kill a SYSTEM process.
    try {
        Get-Process -Name vigil_watchdog -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop
        if (Wait-ServiceStatus 'Stopped' 8) {
            Write-Warn "Zatrzymany przez kill procesu (stop jako SYSTEM nie zadziałał)"
            return $true
        }
    } catch { Write-Host "    Stop-Process: $_" -ForegroundColor DarkGray }

    throw "Nie udało się zatrzymać $ServiceName. Manual: reboot komputera."
}

function Remove-VigilServiceEntry {
    # sc.exe delete as SYSTEM (admins lack SD). True when the service is gone.
    try { Invoke-ScAsSystem "delete $ServiceName" } catch { Write-Host "    delete jako SYSTEM: $_" -ForegroundColor DarkGray }
    return (Wait-ServiceStatus 'Missing' 10)
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
        Invoke-ScAsSystem "start $ServiceName"
        if (Wait-ServiceStatus 'Running' 12) {
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

    $null = Stop-VigilService

    Write-Step "Usuwam service $ServiceName"
    if (-not (Remove-VigilServiceEntry)) {
        Write-Warn "Service $ServiceName dalej istnieje po próbie usunięcia — może wymagać reboota"
        return
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

    # Legacy first: DisableRegistryTools (before v0.6) blocks reg.exe for the rest of the uninstall.
    foreach ($p in @($LegacyUserPolicies) + @($UserPolicyValues)) {
        $path = "$BasePath\$($p.Key)"
        if (-not (Test-Path $path)) { continue }
        if ($null -eq $p.Reset) {
            Remove-ItemProperty -Path $path -Name $p.Name -ErrorAction SilentlyContinue
        } else {
            Set-ItemProperty -Path $path -Name $p.Name -Value $p.Reset -Type $p.Type -ErrorAction SilentlyContinue
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

    # 3. Every account that is not logged in (offline NTUSER.DAT): whichever got the policies, also
    #    when the installer guessed or was told a different account than this run would.
    $loaded = @(Get-LoadedUserHives)
    foreach ($acc in @(Get-InteractiveAccounts)) {
        if ($acc.Sid -in $loaded) { continue }
        $tempChildKey = "HKLM\PC_ChildUser"
        if ((Invoke-RegExe load $tempChildKey (Join-Path $acc.Profile 'NTUSER.DAT')) -eq 0) {
            try {
                Clear-PoliciesFromRegistryHive -BasePath "Registry::HKEY_LOCAL_MACHINE\PC_ChildUser"
                Write-OK "User '$($acc.Name)' (offline): policies removed"
            } finally {
                [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Milliseconds 500
                [void](Invoke-RegExe unload $tempChildKey)
            }
        }
    }
}

# === Verify (-Mode verify, #45) ===
#
# Reads the installed state and prints one line per check; changes nothing (a user hive that is not
# loaded is mounted read-only for the check and unmounted). What it cannot see — the lock overlay,
# the tray, keyboard blocking — stays a manual test. A diagnostic for the parent, not a protection.

$script:VerifyCounts = @{ Fail = 0; Warn = 0; Pass = 0 }

function Write-Check {
    # Level: Pass | Fail | Warn
    param([string]$Level, [string]$Msg)
    $script:VerifyCounts[$Level]++
    switch ($Level) {
        'Pass' { Write-OK $Msg }
        'Warn' { Write-Warn $Msg }
        'Fail' {
            Write-Host "    ✗ " -ForegroundColor Red -NoNewline
            Write-Host $Msg
        }
    }
}

function Test-Check {
    param([bool]$Ok, [string]$Msg, [string]$Otherwise = 'Fail')
    Write-Check $(if ($Ok) { 'Pass' } else { $Otherwise }) $Msg
}

function Invoke-OnUserHive {
    # Calls $Action with the registry path of a user's hive: the live one under HKU when loaded,
    # otherwise $HiveFile mounted under HKLM for the call. Returns $null when the hive can't be read.
    param([string]$Sid, [string]$HiveFile, [scriptblock]$Action)
    if ($Sid -and (Test-Path "Registry::HKEY_USERS\$Sid")) {
        return , @(& $Action "Registry::HKEY_USERS\$Sid")
    }
    if (-not $HiveFile -or -not (Test-Path $HiveFile)) { return $null }
    $temp = 'HKLM\PC_VerifyHive'
    if ((Invoke-RegExe load $temp $HiveFile) -ne 0) { return $null }
    try {
        return , @(& $Action 'Registry::HKEY_LOCAL_MACHINE\PC_VerifyHive')
    } finally {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers(); Start-Sleep -Milliseconds 500
        [void](Invoke-RegExe unload $temp)
    }
}

function Get-UserPolicyState {
    # One row per value: State 'set' (installer's value), 'clear' (absent or the uninstall value),
    # 'other' (something else); legacy values: 'legacy' when present.
    param([string]$BasePath)
    foreach ($p in $UserPolicyValues) {
        $v = (Get-ItemProperty -Path "$BasePath\$($p.Key)" -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
        $state = if ($null -ne $v -and "$v" -eq "$($p.Value)") { 'set' }
                 elseif ($null -eq $v -or "$v" -eq "$($p.Reset)") { 'clear' }
                 else { 'other' }
        [pscustomobject]@{ Name = "$(($p.Key -split '\\')[-1])\$($p.Name)"; State = $state; Legacy = $false }
    }
    foreach ($p in $LegacyUserPolicies) {
        $v = (Get-ItemProperty -Path "$BasePath\$($p.Key)" -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
        [pscustomobject]@{ Name = $p.Name; State = $(if ($null -ne $v) { 'legacy' } else { 'clear' }); Legacy = $true }
    }
}

function Test-UserPolicies {
    # Installed: every value set, no legacy one. Removed: nothing of ours left.
    param([string]$Label, [string]$Sid, [string]$HiveFile, [switch]$Removed)
    $rows = Invoke-OnUserHive -Sid $Sid -HiveFile $HiveFile -Action { param($base) Get-UserPolicyState $base }
    if ($null -eq $rows) { Write-Check 'Warn' "${Label}: nie da się odczytać rejestru"; return }
    $bad = if ($Removed) { @($rows | Where-Object { $_.State -in 'set', 'legacy' }) }
           else { @($rows | Where-Object { ($_.Legacy -and $_.State -eq 'legacy') -or (-not $_.Legacy -and $_.State -ne 'set') }) }
    if ($bad.Count -eq 0) {
        Write-Check 'Pass' $(if ($Removed) { "${Label}: policy usunięte" } else { "${Label}: policy ustawione ($($UserPolicyValues.Count) wartości)" })
    } else {
        Write-Check 'Fail' "${Label}: $(($bad | ForEach-Object { "$($_.Name)=$($_.State)" }) -join ', ')"
    }
}

function Get-VigilLeftoverTasks {
    # One-off SYSTEM tasks of this installer (now and before #46) and of vigil-cleanup.ps1.
    @(Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskName -like '_vigil_sc_*' -or $_.TaskName -like '_pc_*' -or $_.TaskName -like 'VigilCleanup_*' })
}

function Test-AclRules {
    # True when $Acl's explicit rules are exactly $Want (SID -> FileSystemRights name), all Allow.
    # Returns the differences as text in $script:AclDiff.
    param($Acl, [hashtable]$Want)
    $sync = [int][System.Security.AccessControl.FileSystemRights]::Synchronize
    $rules = @($Acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))
    $diff = @()
    foreach ($r in $rules) {
        $sid = $r.IdentityReference.Value
        $want = if ($Want.ContainsKey($sid)) { [int][System.Security.AccessControl.FileSystemRights]$Want[$sid] -band -bnot $sync } else { $null }
        if ($null -eq $want -or $r.AccessControlType -ne 'Allow' -or ([int]$r.FileSystemRights -band -bnot $sync) -ne $want) {
            $diff += "$sid=$($r.AccessControlType):$($r.FileSystemRights)"
        }
    }
    $present = @($rules | ForEach-Object { $_.IdentityReference.Value })
    $diff += @($Want.Keys | Where-Object { $_ -notin $present } | ForEach-Object { "brak $_" })
    $script:AclDiff = $diff -join '; '
    return $diff.Count -eq 0
}

function Test-ExpectedAcl {
    # The folder: protected, SYSTEM Full / Administrators Modify / Authenticated Users R+X.
    # Below it: inherited only, except the DPAPI secrets: protected, SYSTEM Full / Administrators Read.
    $acl = Get-Acl $InstallDir
    $ok = $acl.AreAccessRulesProtected -and
          (Test-AclRules $acl @{ 'S-1-5-18' = 'FullControl'; 'S-1-5-32-544' = 'Modify'; 'S-1-5-11' = 'ReadAndExecute' })
    Test-Check $ok "ACL katalogu (SYSTEM full, Admin modify, AuthUsers r+x)$(if (-not $ok) { " — chronione=$($acl.AreAccessRulesProtected) $script:AclDiff" })"

    $wrong = @()
    foreach ($item in @(Get-ChildItem -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue)) {
        $a = Get-Acl $item.FullName
        if (-not $item.PSIsContainer -and $item.Name -in $SecretFiles) {
            if (-not ($a.AreAccessRulesProtected -and (Test-AclRules $a @{ 'S-1-5-18' = 'FullControl'; 'S-1-5-32-544' = 'Read' }))) {
                $wrong += "$($item.Name) ($script:AclDiff)"
            }
        } elseif ($a.AreAccessRulesProtected -or -not (Test-AclRules $a @{})) {
            $wrong += "$($item.Name) (jawne: $script:AclDiff)"
        }
    }
    Test-Check ($wrong.Count -eq 0) "ACL plików: dziedziczone, sekrety tylko SYSTEM + odczyt Admin$(if ($wrong.Count) { ' — ' + ($wrong -join ', ') })"
}

function Read-FailureActions {
    # SERVICE_FAILURE_ACTIONS from the service key (language-independent, unlike sc.exe qfailure):
    # reset period, reboot msg ptr, command ptr, action count, actions ptr, then {type, delay} pairs.
    $raw = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name FailureActions -ErrorAction SilentlyContinue).FailureActions
    if (-not $raw -or $raw.Length -lt 20) { return @() }
    $count = [BitConverter]::ToInt32($raw, 12)
    for ($i = 0; $i -lt $count -and (20 + 8 * $i + 8) -le $raw.Length; $i++) {
        [pscustomobject]@{ Type = [BitConverter]::ToInt32($raw, 20 + 8 * $i); DelayMs = [BitConverter]::ToInt32($raw, 24 + 8 * $i) }
    }
}

function Unprotect-LocalMachine {
    param([byte[]]$Data, [byte[]]$Entropy)
    Add-Type -AssemblyName System.Security
    return [System.Security.Cryptography.ProtectedData]::Unprotect(
        $Data, $Entropy, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
}

function Invoke-VerifyInstalled {
    Write-Step "Usługa $ServiceName"
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Test-Check ([bool]$svc) "Zarejestrowana"
    if ($svc) {
        Test-Check ($svc.Status -eq 'Running') "Stan: $($svc.Status)"
        Test-Check ($svc.StartType -eq 'Automatic') "Start: $($svc.StartType)"
        $cim = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
        Test-Check ($cim -and $cim.PathName -eq "`"$watchdogExe`"") "Ścieżka: $($cim.PathName)"
        $actions = @(Read-FailureActions)
        $nonCrash = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name FailureActionsOnNonCrashFailures -ErrorAction SilentlyContinue).FailureActionsOnNonCrashFailures
        Test-Check ($actions.Count -eq 3 -and @($actions | Where-Object { $_.Type -ne 1 }).Count -eq 0 -and $nonCrash -eq 1) `
            "Recovery: restart×3$(if ($nonCrash -ne 1) { ', bez FailureActionsOnNonCrashFailures' })"
        # DACL: what matters is that admins have no stop (WP) and no delete (SD).
        $sddl = (& sc.exe sdshow $ServiceName) -join ''
        $ba = if ($sddl -match '\(A;;([A-Z]+);;;BA\)') { $matches[1] } else { $null }
        Test-Check ($ba -and $ba -notmatch 'WP' -and $ba -notmatch 'SD') "DACL: Admin bez stop i delete$(if (-not $ba) { ' — brak wpisu BA' } elseif ($ba -match 'WP|SD') { " — BA=$ba" })"
    }

    Write-Step "Pliki w $InstallDir"
    $files = @($watchdogExe, $uiExe, $guardianPs1, $vigilToml, $signingDat, $guardianDat, (Join-Path $backupDir 'vigil_watchdog.exe'))
    $missing = @($files | Where-Object { -not (Test-Path $_) })
    Test-Check ($missing.Count -eq 0) "Komplet plików$(if ($missing.Count) { ' — brak: ' + (($missing | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') })"
    $legacy = @((Join-Path $InstallDir 'emergency.dat'), (Join-Path $InstallDir 'vigil_lockscreen.exe'),
                (Join-Path $InstallDir 'vigil_tray.exe'), "$env:PUBLIC\Vigil") | Where-Object { Test-Path $_ }
    Test-Check (@($legacy).Count -eq 0) "Brak pozostałości starszych wersji$(if (@($legacy).Count) { ': ' + ($legacy -join ', ') })"

    if (Test-Path $watchdogExe) {
        $hash = (Get-FileHash $watchdogExe -Algorithm SHA256).Hash.ToLower()
        $backup = Join-Path $backupDir 'vigil_watchdog.exe'
        if (Test-Path $backup) {
            Test-Check ((Get-FileHash $backup -Algorithm SHA256).Hash.ToLower() -eq $hash) "backup\vigil_watchdog.exe = vigil_watchdog.exe"
        }
        if (Test-Path $guardianDat) {
            try {
                $ref = [System.Text.Encoding]::UTF8.GetString((Unprotect-LocalMachine ([IO.File]::ReadAllBytes($guardianDat)) $null))
                Test-Check ($ref -eq $hash) "guardian.dat: hash zgodny z vigil_watchdog.exe"
            } catch { Write-Check 'Fail' "guardian.dat nie odszyfrowuje się: $($_.Exception.Message)" }
        }
        # The package this script came from: is it the one installed?
        $pkg = Join-Path $SourceDir 'vigil_watchdog.exe'
        if ((Test-Path $pkg) -and ((Resolve-Path $pkg).Path -ne (Resolve-Path $watchdogExe).Path)) {
            $same = @('vigil_watchdog.exe', 'vigil_ui.exe', 'vigil_guardian.ps1') | Where-Object {
                (Test-Path (Join-Path $SourceDir $_)) -and (Test-Path (Join-Path $InstallDir $_)) -and
                (Get-FileHash (Join-Path $SourceDir $_)).Hash -eq (Get-FileHash (Join-Path $InstallDir $_)).Hash
            }
            Test-Check (@($same).Count -eq 3) "Zainstalowane binarki = paczka w $SourceDir" -Otherwise 'Warn'
        }
    }
    if (Test-Path $signingDat) {
        try {
            $key = Unprotect-LocalMachine ([IO.File]::ReadAllBytes($signingDat)) ([Text.Encoding]::UTF8.GetBytes('Vigil-SigningKey-v1'))
            Test-Check ($key.Length -ge 16) "signing.dat: klucz odszyfrowany ($($key.Length) B)"
            [Array]::Clear($key, 0, $key.Length)
        } catch { Write-Check 'Fail' "signing.dat nie odszyfrowuje się: $($_.Exception.Message)" }
    }
    $serverUrl = $null
    if (Test-Path $vigilToml) {
        $toml = Get-Content $vigilToml -Raw
        if ($toml -match 'server_url\s*=\s*"([^"]+)"') { $serverUrl = $matches[1] }
        Test-Check ([bool]$serverUrl) "vigil.toml: server_url = $serverUrl"
        Test-Check ($toml -notmatch 'bearer_token') "vigil.toml: bez bearer_token (protokół v1)"
    }
    if (Test-Path $InstallDir) { Test-ExpectedAcl }

    Write-Step "Zadania i rozruch"
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Test-Check ([bool]$task) "Zadanie '$TaskName'"
    if ($task) {
        Test-Check ($task.Principal.UserId -in 'SYSTEM', 'S-1-5-18' -and $task.Principal.RunLevel -eq 'Highest') "  jako SYSTEM, najwyższe uprawnienia"
        Test-Check ($task.State -ne 'Disabled') "  stan: $($task.State)"
        $boot = @($task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' }).Count
        $every5 = @($task.Triggers | Where-Object { $_.Repetition.Interval -eq 'PT5M' }).Count
        Test-Check ($boot -ge 1 -and $every5 -ge 1) "  wyzwalacze: przy starcie + co 5 min"
        Test-Check ("$($task.Actions[0].Arguments)" -like "*$guardianPs1*") "  uruchamia $guardianPs1"
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        # 0x41303 = has not run yet (right after install)
        if ($info) { Test-Check ($info.LastTaskResult -in 0, 0x41303) "  ostatni wynik: 0x$('{0:X}' -f $info.LastTaskResult) ($($info.LastRunTime))" -Otherwise 'Warn' }
    }
    Test-Check (-not (Get-ScheduledTask -TaskName 'Vigil Tray' -ErrorAction SilentlyContinue)) "Brak starego zadania 'Vigil Tray'"
    $left = Get-VigilLeftoverTasks
    Test-Check ($left.Count -eq 0) "Brak tymczasowych zadań SYSTEM$(if ($left.Count) { ': ' + (($left | ForEach-Object { $_.TaskName }) -join ', ') })"
    foreach ($mode in 'Minimal', 'Network') {
        $key = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
        Test-Check ((Test-Path $key) -and (Get-Item $key).GetValue('') -eq 'Service') "SafeBoot $mode"
    }
    $bootMenu = (& bcdedit.exe /enum '{current}') | Where-Object { $_ -match '^bootmenupolicy\s+(\S+)' } | ForEach-Object { $matches[1] }
    Test-Check ($bootMenu -eq 'Standard') "Menu F8 wyłączone (bootmenupolicy $bootMenu)" -Otherwise 'Warn'

    Write-Step "Policy użytkownika"
    Resolve-ChildAccount
    if ($script:ChildAccount) {
        Test-UserPolicies -Label "Konto $($script:ChildAccount.Name)" -Sid $script:ChildAccount.Sid `
            -HiveFile (Join-Path $script:ChildAccount.Profile 'NTUSER.DAT')
    } else {
        Write-Check 'Warn' "Nie ustalono konta dziecka — sprawdzam tylko Default profile"
    }
    Test-UserPolicies -Label 'Default profile' -HiveFile "$env:SystemDrive\Users\Default\NTUSER.DAT"

    Write-Step "Działanie"
    # Someone is logged on (explorer runs) ⇒ the watchdog must have started vigil_ui in that session.
    if (Get-Process -Name explorer -ErrorAction SilentlyContinue) {
        Test-Check ([bool](Get-Process -Name vigil_ui -ErrorAction SilentlyContinue)) "vigil_ui działa w sesji użytkownika"
    } else {
        Write-Skip "Nikt nie jest zalogowany — vigil_ui nie musi działać"
    }
    if ($serverUrl) { Test-Check (Test-ServerReachable $serverUrl) "Serwer odpowiada: $serverUrl/health" }
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Vigil Watchdog'
                                                     Level = 1, 2, 3; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction Stop)
    } catch { $events = @() }  # no matching events also throws
    Test-Check ($events.Count -eq 0) "Event Log watchdoga: $($events.Count) ostrzeżeń/błędów w 15 min$(if ($events.Count) { ' — ostatni: ' + ($events[0].Message -split "`n")[0] })" -Otherwise 'Warn'
}

function Invoke-VerifyRemoved {
    Write-Step "Vigil nie jest zainstalowany — sprawdzam, czy nic nie zostało"
    Test-Check (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) "Brak usługi $ServiceName"
    Test-Check (-not (Test-Path $InstallDir)) "Brak $InstallDir"
    foreach ($t in $TaskName, 'Vigil Tray') {
        Test-Check (-not (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue)) "Brak zadania '$t'"
    }
    $left = Get-VigilLeftoverTasks
    Test-Check ($left.Count -eq 0) "Brak tymczasowych zadań SYSTEM$(if ($left.Count) { ': ' + (($left | ForEach-Object { $_.TaskName }) -join ', ') })"
    foreach ($mode in 'Minimal', 'Network') {
        Test-Check (-not (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName")) "Brak SafeBoot $mode"
    }
    $procs = @(Get-Process -Name vigil_watchdog, vigil_ui, vigil_lockscreen, vigil_tray -ErrorAction SilentlyContinue)
    Test-Check ($procs.Count -eq 0) "Brak procesów Vigila$(if ($procs.Count) { ': ' + (($procs | ForEach-Object { $_.Name }) -join ', ') })"
    foreach ($acc in @(Get-InteractiveAccounts)) {
        Test-UserPolicies -Label "Konto $($acc.Name)" -Sid $acc.Sid -HiveFile (Join-Path $acc.Profile 'NTUSER.DAT') -Removed
    }
    Test-UserPolicies -Label 'Default profile' -HiveFile "$env:SystemDrive\Users\Default\NTUSER.DAT" -Removed
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
    Step-RemoveLegacyFiles
    Step-RegisterService
    Step-SetServiceDacl
    Step-AddSafeBootEntries
    Step-RegisterGuardianTask
    Step-RemoveTrayTask
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
    Step-StopUiProcesses
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
    if (-not $ServerUrl -and -not $PairingCode) {
        throw "Podaj co najmniej jeden z: -ServerUrl, -PairingCode"
    }

    # Załaduj istniejące values z vigil.toml gdy parametry nie podane
    $existingToml = if (Test-Path $vigilToml) { Get-Content $vigilToml -Raw } else { '' }
    if (-not $ServerUrl) {
        if ($existingToml -match 'server_url\s*=\s*"([^"]*)"') { $script:ServerUrl = $matches[1] }
        else { throw "ServerUrl nie podany i nie znaleziony w vigil.toml" }
    } else { $script:ServerUrl = Normalize-ServerUrl $ServerUrl }

    # Re-pair before stopping anything: a wrong or expired code leaves the install as it was.
    if ($PairingCode) {
        Get-CredentialsFromServer -serverUrl $script:ServerUrl -code $PairingCode
    }

    $null = Stop-VigilService

    # Tymczasowo otwórz ACL żeby kolejne kroki mogły pisać
    Step-UnlockPermissions

    try {
        # Zawsze przepisz vigil.toml: usuwa bearer_token ze starych instalacji (protokół v1).
        Step-WriteVigilToml
        if ($PairingCode) { Step-WriteSigningDat }
        Step-RemoveLegacyFiles
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

function Invoke-Verify {
    Write-Host "`n[ VERIFY ]`n" -ForegroundColor Magenta
    Step-RequireAdmin
    if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -or (Test-Path $InstallDir)) {
        Invoke-VerifyInstalled
    } else {
        Invoke-VerifyRemoved
    }
    $c = $script:VerifyCounts
    $color = if ($c.Fail) { 'Red' } elseif ($c.Warn) { 'Yellow' } else { 'Green' }
    Write-Host "`n[ VERIFY: $($c.Pass) OK, $($c.Fail) błędów, $($c.Warn) ostrzeżeń ]`n" -ForegroundColor $color
    if ($c.Fail) { exit 1 }
}

# === Main ===

try {
    switch ($Mode) {
        'install'       { Invoke-Install }
        'uninstall'     { Invoke-Uninstall }
        'update-config' { Invoke-UpdateConfig }
        'reinstall'     { Invoke-Reinstall }
        'verify'        { Invoke-Verify }
    }
} catch {
    Write-Host "`n[ FAILED ]`n" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
