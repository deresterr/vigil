# vigil_guardian

Drugi watchdog dla `vigil_watchdog`. PowerShell script uruchamiany przez Scheduled Task SYSTEM.

## Pliki

- `vigil_guardian.ps1` — główny script

## Co sprawdza

| # | Check | Reakcja |
|---|---|---|
| 1 | Service `vigil_watchdog` istnieje | Alert (reinstall to job installera) |
| 2 | Status == Running | Restart-Service (3× retry, potem reboot za 60s) |
| 3 | SHA256 `vigil_watchdog.exe` vs reference w `guardian.dat` (DPAPI LocalMachine) | Restore z `backup\vigil_watchdog.exe` |
| 4 | IFEO Debugger key na `vigil_watchdog.exe` (HKLM Image File Execution Options) | Usuń value `Debugger` |
| 5 | SafeBoot keys (Minimal + Network) | Recreate z value `Service` |
| 6 | Service Recovery config (`sc qfailure`) | Recreate przez `sc failure` |

## Event Log

Wszystkie akcje logowane do:
- **Source**: `Vigil Guardian`
- **Log**: `Application`

Event IDs:
- `1001` / `1002` — start/end check
- `1099` — service nie istnieje (krytyczne, manual recovery)
- `2xxx` — service restart (2002 retry, 2003 success, 2099 reboot triggered)
- `3xxx` — hash check (3009 mismatch, 3010 restore success, 3098 brak backup)
- `4xxx` — IFEO repair
- `5xxx` — SafeBoot repair
- `6xxx` — Service Recovery repair
- `9999` — guardian crash

Filtruj przez:

```powershell
Get-WinEvent -FilterHashtable @{
    LogName='Application'
    ProviderName='Vigil Guardian'
} -MaxEvents 50 | Format-Table TimeCreated, Id, LevelDisplayName, Message -Wrap
```

## Restart counter

`%ProgramData%\Vigil\guardian_restart_counter.txt` — persistent licznik kolejnych
restart attempts. Reset gdy service ostatecznie wstaje. Po `MaxRestartRetries` (3) →
`shutdown /r /t 60`. Counter wyzerowany przed reboot żeby po starcie nie wpadać w pętlę.

## Reference hash (`guardian.dat`)

DPAPI LocalMachine encrypted plain text z hashem SHA256 (hex string, lowercase).
Generowany przez installera — przykład:

```powershell
$hash = (Get-FileHash 'C:\ProgramData\Vigil\vigil_watchdog.exe' -Algorithm SHA256).Hash
$bytes = [System.Text.Encoding]::UTF8.GetBytes($hash)
$encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes, $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
[System.IO.File]::WriteAllBytes('C:\ProgramData\Vigil\guardian.dat', $encrypted)
```

Jeśli `guardian.dat` nie istnieje, check #3 pomija (logowane jako warning event 3003).
Powód: lepiej brak checka niż false-positive sabotage alert podczas migration/upgrade.

## Backup binary

`%ProgramData%\Vigil\backup\vigil_watchdog.exe` — kopia oryginalna z installera.
Używana do restore przy hash mismatch. Jeśli backup brak, alert (event 3098), manual recovery.

## Scheduled Task

Tworzony przez installer, tu instrukcja manual setup do testowania:

```powershell
$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\Vigil\vigil_guardian.ps1"'

$boot = New-ScheduledTaskTrigger -AtStartup
$cycle = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5)

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask -TaskName 'Vigil Guardian' `
    -Action $action -Trigger @($boot, $cycle) `
    -Principal $principal -Settings $settings -Force
```

## Test (DryRun, manualnie)

```powershell
# Pełen check, nic nie naprawia (-DryRun parameter)
.\vigil_guardian.ps1 -DryRun

# Pojedynczy check (np. tylko hash lub SafeBoot):
.\vigil_guardian.ps1 -DryRun   # wszystkie checki w trybie podglądu
```

## Co guardian NIE robi

- **Nie reinstalluje servicu jeśli go nie ma.** To job installera. Guardian alertuje (event 1099)
  i kończy. Dlaczego: reinstall vs. integrity-watch to dwie różne odpowiedzialności. Mieszanie
  ich w guardianie skomplikowałoby logikę i otworzyłoby vector na "ktoś kasuje service →
  guardian go odtwarza ze złym configiem".
- **Nie monituje vigil_lockscreen.** Może się crash'ować bez problemu — watchdog (vigil_watchdog) respawn'uje go przy następnym tickcie.
- **Nie sprawdza pliku `vigil.toml`.** Jego brak oznacza fail-fast w vigil_watchdog na startup,
  co i tak triggeruje guardian repair przez "service not running". Sprawdzanie zawartości
  vigil.toml byłoby duplikacją — vigil_watchdog ma własną walidację (`VigilConfig.LoadFromFile`).

## Walidacja

Skrypt walidowany na realnym Windowsie (DryRun + pełen install/uninstall cycle).
