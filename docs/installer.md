# vigil-installer

PowerShell installer dla wszystkich PC components: `vigil_watchdog.exe`, `vigil_lockscreen.exe`,
`vigil_tray.exe`, `vigil_guardian.ps1`.

## Pliki

- `vigil_install.ps1` / `Install.cmd` — main installer z 4 trybami (install / uninstall / update-config / reinstall)
- `vigil-cleanup.ps1` — nuclear-option uninstall, działa też offline z Windows Recovery
  (gdy Windows nie startuje albo `Cleanup.cmd` z jakiegoś powodu nie idzie).
  Patrz sekcja "Cleanup z Recovery" poniżej.

## Tryby

| Mode | Opis | Wymaga binarek? |
|------|------|------------------|
| `install` | Fresh install | Tak (z `-SourceDir`) |
| `uninstall` | Remove all | Nie |
| `update-config` | Update server_url (vigil.toml) / signing_key (signing.dat) | Nie |
| `reinstall` | Uninstall + install | Tak |

## Quick start

### Fresh install (pełen, z hasłem awaryjnym i nazwą konta dziecka)

```powershell
# Skopiuj na docelowy PC: zawartość dist\ na PC dziecka

# Signing key z panelu rodzica (Bezpieczeństwo → Klucz podpisujący)
Install.cmd -Mode install `
    -ServerUrl 'http://192.168.0.2:8088' `
    -SigningKey 'XyZ123abc...' `
    -ChildUsername 'jasiek' `
    -EmergencyPassword 'silne_haslo_zapamietane_przez_rodzica' `
    -DisableWinRE
```

### Minimalny install (bez awaryjnego i bez per-user policies — tylko Default profile)

```powershell
Install.cmd -Mode install `
    -ServerUrl 'http://192.168.0.2:8088' `
    -SigningKey 'XyZ123abc...'
```

W tym wariancie hasło awaryjne nie działa (brak `emergency.dat`), a policies idą tylko do
Default User profile (skutek dla nowo utworzonych kont).

### Uninstall

```powershell
Cleanup.cmd
```

### Zmiana adresu servera (np. po zmianie static IP)

```powershell
Install.cmd -Mode update-config -ServerUrl 'http://192.168.0.5:8088'
```

### Pełny reset (uninstall + install)

```powershell
Install.cmd -Mode reinstall -ServerUrl '...' -SigningKey '...'
```

## Parametry

### Wymagane dla `install`
- `-ServerUrl http://...` — adres FastAPI servera w LAN
- `-SigningKey ...` — z admin UI (zakładka „Bezpieczeństwo" → klucz podpisujący); bez niego installer pobiera klucz z serwera w trybie inicjalizacji

### Opcjonalne
- `-SourceDir <path>` — gdzie szukać binarek, default `$PSScriptRoot`
- `-InstallDir 'C:\ProgramData\Vigil'` — default
- `-ChildUsername 'jasiek'` — nazwa konta dziecka. Bez tego policies (DisableTaskMgr,
  DisableRegistryTools, DisableLockWorkstation, DisableChangePassword, NoLogoff, sticky keys,
  touchpad gestures) trafiają **tylko do Default User profile** — czyli nowo tworzonych kont.
  Z `-ChildUsername`: installer rezolwuje SID i pisze policies wprost do hive'u tego użytkownika
  (live `HKEY_USERS\<SID>` jeśli zalogowany, `reg load` jego `NTUSER.DAT` jeśli nie). Plus
  zacieśnia ACL na `emergency.dat` — Read tylko dla tego konta (zamiast `BUILTIN\Users`).
- `-EmergencyPassword '<min. 6 znaków>'` — hasło awaryjne (DPAPI-encrypted → `emergency.dat`).
  Pozwala rodzicowi zdjąć lockscreen lokalnie (bez sieci/serwera) hotkeyem `Ctrl+Shift+Alt+E`.
  Opt-in: bez tego parametru hasło awaryjne po prostu nie działa (`EmergencyAuth.IsConfigured()`
  zwraca false → dialog się nie otwiera).
- `-DisableWinRE` — wyłącz Windows Recovery Environment (`reagentc /disable`). Opt-in, bo
  to nie do końca odwracalne (uninstall NIE robi `reagentc /enable`)
- `-NoUserPolicies` — pomiń aplikację policies w ogóle (ani Default profile, ani named user).
  Użyteczne do testów na własnym koncie / dry-runów.

## Co installer robi w trybie `install`

1. Sprawdza uprawnienia admin (fail-fast jeśli nie)
2. Sprawdza obecność 4 plików w `-SourceDir`
3. Waliduje parametry (signing_key min 16 bajtów po dekodowaniu, ServerUrl http/https,
   EmergencyPassword min 6 znaków jeśli podany)
4. Tworzy `C:\ProgramData\Vigil\` z ACL: SYSTEM full, Admin modify, AuthUsers r+x
5. Kopiuje 4 binarki + backup `vigil_watchdog.exe` do `backup\`
6. Generuje SHA256 hash, szyfruje DPAPI LocalMachine, zapisuje `guardian.dat` (dla vigil_guardian)
7. Tworzy `vigil.toml` z server_url (bez sekretów; signing_key idzie do `signing.dat`)
8. Szyfruje signing_key DPAPI LocalMachine z entropy `"Vigil-SigningKey-v1"`,
   zapisuje `signing.dat`
9. Jeśli podano `-EmergencyPassword`: szyfruje DPAPI z entropy `"Vigil-Emergency-v1"`,
   zapisuje `emergency.dat`. ACL: SYSTEM+Admins full, Read tylko dla `-ChildUsername`
   (lub `BUILTIN\Users` jako fallback gdy nie podano).
10. Rejestruje service `vigil_watchdog` (auto-start, recovery: restart×3 bez reboota)
11. Ustawia restrykcyjny DACL na service (Admin tylko query/start, nie stop/modify/delete)
12. Dodaje SafeBoot entries (Minimal + Network) — vigil_watchdog działa też w safe mode
13. Tworzy Scheduled Task `Vigil Guardian` (SYSTEM, AtStartup + co 5 min)
14. `bcdedit bootmenupolicy Standard` — wyłącza F8 menu
15. `reagentc /disable` (opt-in przez `-DisableWinRE`)
16. Aplikuje user policies (chyba że `-NoUserPolicies`):
    - **Zawsze**: mount Default User profile (`C:\Users\Default\NTUSER.DAT`), set policies, unmount.
      Skutek dla NOWO TWORZONYCH kont.
    - **Jeśli `-ChildUsername` podany**: rezolwuje SID. Live HKU jeśli zalogowany,
      `reg load` NTUSER.DAT jeśli nie. Skutek dla istniejącego konta dziecka **od razu**.
    - Pisane klucze: `DisableTaskMgr`, `DisableRegistryTools`, `DisableLockWorkstation`,
      `DisableChangePassword`, `NoLogoff`, sticky keys hot-keys off, PrecisionTouchPad gesty off.
17. Start service
18. Smoke test — czeka 15s i sprawdza czy `vigil_watchdog` jest w stanie Running.
    Opcjonalnie loguje potwierdzenie z Event Log (`Vigil Watchdog`, source może jeszcze
    nie istnieć przy pierwszym uruchomieniu — to nie jest błąd).

## Co `uninstall` robi

1. Stop + delete service
2. Unregister Scheduled Task
3. Remove SafeBoot entries
4. Cofa user policies w Default User profile + (jeśli `-ChildUsername` podany przy uninstall)
   w hive tego użytkownika — usuwa wszystkie wartości pisane przy installu
   (`DisableTaskMgr`, `DisableRegistryTools`, `DisableLockWorkstation`, `DisableChangePassword`,
   `NoLogoff`, sticky keys, touchpad gestures). Pomijane jeśli `-NoUserPolicies`.
5. Remove `C:\ProgramData\Vigil\` w całości — w tym `emergency.dat` i `signing.dat`
   (z `takeown` + `icacls` żeby pokonać ACL z install)

**`uninstall` NIE robi:**
- `bcdedit` cofać (Standard policy to default na Win10+, no-op)
- `reagentc /enable` cofać (zostawiamy ręcznie jeśli było wyłączone)
- Policy w innych kontach niż Default i `-ChildUsername` — jeśli ktoś instalował kilka razy
  z różnymi userami, to `vigil-cleanup.ps1 -Force` przejdzie po wszystkich kontach.
- `bypass.flag` w `C:\Users\Public\Vigil\` — to znika razem z całym katalogiem przy następnym
  cleanupie Public, ale samo przez się zostaje (24h ważność, potem nieaktywny).

## Co `update-config` robi

1. Wymaga że `C:\ProgramData\Vigil\` istnieje
2. Czyta brakujące parametry z istniejącego `vigil.toml` (jeśli `-ServerUrl` nie podany,
   bierze poprzedni)
3. Zatrzymuje service (jeśli running)
4. Nadpisuje `vigil.toml`
5. Jeśli `-SigningKey` podany, też nadpisuje `signing.dat` (DPAPI)
6. Restart service

Można podać tylko jeden parametr (np. tylko `-ServerUrl`), reszta zostaje.

## Idempotencja

`install` może być uruchomiony ponownie — wszystkie kroki są idempotentne:
- Service istnieje → stop, delete, recreate
- Scheduled Task istnieje → unregister, register
- SafeBoot entries istnieją → overwrite
- ACL → SetAccessRuleProtection(true) wyklucza dziedziczenie i nadpisuje

## Walidacja

Skrypt walidowany na realnym Windowsie (install / reinstall / uninstall / update-config cycle).

## Ograniczenia

- **User policies dla istniejącego konta** wymagają `-ChildUsername` przy installu — bez tego
  installer modyfikuje tylko Default User profile (efekt dla nowych kont) i pokazuje warning
  z instrukcją. Patrz też „Krok 4" w głównym README.
- **Service DACL a Stop-Service** — `Stop-Service` (PS native) dostaje ACCESS_DENIED z restrykcyjnym
  DACL na serwisie, bo PS wymaga szerszego zestawu uprawnień niż same `SERVICE_STOP`. Installer
  używa `sc.exe stop` z polling loop (do 12s) jako głównej strategii, potem kill procesu,
  na końcu SYSTEM scheduled task. `sc sdset` może zwrócić warning na niektórych systemach —
  installer kontynuuje, ale stop będzie wymagał kill zamiast graceful shutdown.
- **`-DisableWinRE` nie jest cofane przy uninstall** — opt-in z założenia, opt-out manualny
  (`reagentc /enable`).
- **Backup binarki** (do restore przez guardian) jest single-version. Przy update binarki
  reinstalluje się też backup → traci się starą wersję jako recovery point.
- **`-EmergencyPassword` nie da się zmienić bez reinstall** — installer ustawia raz, później
  jedyna ścieżka to `update-config -EmergencyPassword 'nowe'` (lub ręczne usunięcie `emergency.dat`
  i ponowny install). Decyzja produktowa: hasło awaryjne to ostatnia deska ratunku, lepiej żeby
  zostawało niezmienne i pamiętane.

## Cleanup z Recovery (vigil-cleanup.ps1)

Skrypt który kasuje wszystkie ślady systemu Vigil z Windowsa — service, scheduled task,
katalogi, event log sources, user policies dla wszystkich kont. Działa w dwóch trybach:

### Online (Windows działa normalnie)

```powershell
# PowerShell jako Administrator
.\vigil-cleanup.ps1
# albo bez pytań:
.\vigil-cleanup.ps1 -Force
```

To samo co `Cleanup.cmd`, ale defensywne — nie wymaga że install dir
i registry są w spójnym stanie.

### Offline (z Windows Recovery / instalatora)

Gdy Windows nie boot'uje albo z innego powodu musisz wykasować Vigil z zewnątrz:

1. Boot z nośnika instalacyjnego Windows (USB) lub wybierz **"Repair your computer"** w
   menu zaawansowanego startupu (Shift+Restart → Troubleshoot → Advanced → Command Prompt).
2. W otwartym Command Prompt zorientuj się która litera dysku to docelowy Windows
   (zwykle `D:` lub `E:` — `C:` jest często zarezerwowane dla recovery image):
   ```cmd
   dir C:\Windows
   dir D:\Windows
   ```
3. Wgraj skrypt (USB stick, lub jeśli masz dostęp do sieci → `xcopy \\server\path X:\`)
4. Uruchom z explicit drive parameter:
   ```cmd
   powershell.exe -ExecutionPolicy Bypass -File X:\vigil-cleanup.ps1 -SystemDrive D: -Force
   ```

Skrypt zrobi mount NTUSER.DAT każdego konta usera, wyczyści policies, odmountuje, kasuje
pliki, kasuje service entry z SYSTEM hive.

### Parametry

| Parametr | Opis |
|----------|------|
| `-SystemDrive D:` | Litera dysku z Windows (offline mode). Online wykrywa automatycznie. |
| `-Force` | Bez interaktywnego potwierdzenia (dla automatów / skryptów wsadowych). |
| `-KeepUserPolicies` | Nie cofa user policies — useful przed reinstall Vigil. |
| `-WhatIf` | Standardowy PS dry-run — pokazuje co by usunął bez wykonywania. |

### Co konkretnie kasuje

1. **Service**: `vigil_watchdog` (z wieloma strategiami fallback gdy DACL blokuje)
2. **Scheduled Tasks**: `Vigil Guardian`, `Vigil Tray`
3. **Procesy**: `vigil_watchdog.exe`, `vigil_lockscreen.exe`, `vigil_tray.exe`
4. **Katalogi**:
   - `<drive>:\ProgramData\Vigil\`
   - `<drive>:\ProgramData\ParentalControl\` (stara nazwa)
   - `<drive>:\Windows\System32\Tasks\ParentalControl Guardian`
5. **Event Log sources**: `Vigil Watchdog`, `Vigil Guardian`
6. **SafeBoot entries**: `vigil_watchdog` w Minimal i Network
7. **User policies** (jeśli NIE podano `-KeepUserPolicies`) — dla każdego konta w `Users\`:
   - `DisableTaskMgr`, `DisableRegistryTools`, `DisableLockWorkstation`, `DisableChangePassword`
   - `NoLogoff`
   - Accessibility shortcuts (StickyKeys/Keyboard Response/ToggleKeys/MouseKeys) — defaulty Windows
   - PrecisionTouchPad (Three/FourFinger Slide/Tap) — defaulty Windows

### Czego NIE robi

- **`reagentc /enable`** — przywrócenie WinRE jeśli installer wyłączył przez `-DisableWinRE`.
  Opt-in. Uruchom ręcznie jeśli chcesz.
- **`bcdedit bootmenupolicy`** — Standard to default na Win10/11, nie ma czego cofać.
- **Cleanup hasła konta dziecka** — to nigdy nie było modyfikowane przez Vigil.
