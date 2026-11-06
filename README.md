# Vigil — system kontroli rodzicielskiej Win10/11

Self-hosted system kontroli czasu PC dla dziecka. Serwer FastAPI w Dockerze,
Vigil Windows Service na PC dziecka. Sterowanie przez TOTP (rodzic z telefonu)
oraz admin UI (przeglądarka).

## Architektura

```
┌─────────────────────────────┐    HMAC-signed HTTP    ┌─────────────────────────────┐
│  HOME LAN (192.168.0.0/24)  │  ◄──────────────────►  │  CHILD PC (Windows 10/11)   │
├─────────────────────────────┤                        ├─────────────────────────────┤
│                             │                        │                             │
│  Linux server (Docker)      │                        │  vigil_watchdog.exe (Service)│
│  ┌──────────────────────┐   │                        │  ├─ HMAC heartbeat client   │
│  │  vigil               │   │                        │  └─ Lockscreen orchestracja │
│  │  (FastAPI + SQLite)  │   │                        │           │                 │
│  │                      │   │                        │           │ named pipe IPC  │
│  │  - /v1/heartbeat     │   │                        │           ▼                 │
│  │  - /v1/totp_attempt  │   │                        │  vigil_lockscreen.exe       │
│  │  - /admin/* (UI+API) │   │                        │  (WPF fullscreen + KB hook) │
│  └──────────────────────┘   │                        │                             │
│           ▲                  │                        │  vigil_tray.exe             │
│           │                  │                        │  (WinForms, zasobnik)       │
│  Rodzic w przeglądarce       │                        │                             │
│  Aegis na telefonie (TOTP)   │                        │  vigil_guardian.ps1         │
│                             │                        │  (Scheduled Task watchdog)  │
└─────────────────────────────┘                        └─────────────────────────────┘
```

## Stan komponentów

| # | Komponent | Język | Katalog |
|---|-----------|-------|---------|
| 1 | vigil-server | Python 3.12 + FastAPI | `vigil-server/` |
| 2 | vigil-watchdog | C# .NET 8 (Windows Service) | `vigil-watchdog/` |
| 3 | vigil-lockscreen | C# .NET 8 + WPF | `vigil-lockscreen/` |
| 4 | vigil-tray | C# .NET 8 + WinForms | `vigil-tray/` |
| 5 | vigil-guardian | PowerShell | `vigil-guardian/` |
| 6 | vigil-installer | PowerShell | `vigil-installer/` |

## Kluczowe decyzje architektoniczne

1. **Sekrety tylko po stronie serwera** — HMAC signing_key, TOTP secret, hash hasła rodzica
   nigdy nie trafiają na PC dziecka w plain text. SigningKey przekazywany do `vigil_watchdog`
   raz przy instalacji (DPAPI LocalMachine).

2. **Lockscreen NIE komunikuje się z serwerem bezpośrednio** — tylko z watchdogiem przez named
   pipe `\\.\pipe\VigilUI`. Eliminuje konieczność trzymania bearera/signing_key
   w user-mode binarce.

3. **Każda odpowiedź serwera jest podpisana HMAC-SHA256** (key + nonce + body).
   Watchdog weryfikuje przed dispatch — chroni przed fake server attack.

4. **Każdy request zawiera nonce + timestamp** — serwer odrzuca duplicate nonces (replay
   attack) i requesty starsze niż 60s (clock drift attack).

5. **State trzymany na serwerze w SQLite** — lokalny PC nie ma żadnego state, więc replay
   attack lokalnie niemożliwy. Reboot/reinstall watchdoga nic nie zmienia.

6. **Lockscreen na "Default" desktopie + KeyboardBlocker** — WPF wymaga DWM, którego custom
   `CreateDesktop()` nie ma (lockscreen by crashował). Bezpieczeństwo zapewnia low-level
   keyboard hook blokujący Win/Alt+Tab/Alt+F4/Ctrl+Esc. Threat model: 8-letnie dziecko
   bez admin password.

7. **vigil_guardian** — Scheduled Task SYSTEM, integrity check vigil_watchdog
   co 5 min, restore z backup przy hash mismatch, restart przy crash.

8. **Hasło awaryjne — bypass lokalny** (`emergency.dat` + `bypass.flag`). Gdy padnie sieć
   albo serwer, rodzic musi mieć sposób zdjąć lockscreen bez kontaktu z `/v1/totp_attempt`.
   Hotkey `Ctrl+Shift+Alt+E` → modal `EmergencyWindow` → DPAPI-zaszyfrowany hash sprawdzany
   lokalnie. Sukces = `bypass.flag` (też DPAPI) ważny 24h w `C:\Users\Public\Vigil\`,
   widziany przez watchdog co tick.

9. **Tray icon przez Scheduled Task** — `vigil_tray.exe` uruchamia się przez Scheduled Task
   "Vigil Tray" (AtLogon, konto dziecka). Chronione przed usunięciem przez dziecko z poziomu
   Settings → Startup (widoczne tylko w Task Scheduler).

## Deployment — step-by-step

### Krok 1: Serwer (Linux, Docker)

```bash
cd vigil-server
cp .env.example .env
# Edytuj .env: ustaw VIGIL_CHILD (nazwa dziecka) i VIGIL_PORT (domyślnie 8088)
docker compose up -d
docker compose logs -f vigil
```

Przy pierwszym uruchomieniu serwer startuje w trybie setup (`setup_complete=0`).
Otwórz `http://<ip-serwera>:8088/setup` i przejdź przez kreator — ustawi hasło rodzica,
TOTP, limity czasu i wygeneruje sekrety kryptograficzne. Po zakończeniu setup zamknij stronę
i zaloguj się do `http://<ip-serwera>:8088/admin/` hasłem ustawionym w kreatorze.

### Krok 2: Build binarek (Windows z .NET 8 Desktop SDK)

```powershell
# Z root katalogu projektu (jako zwykły user):
.\Build.cmd
# Binarki + skrypty lądują w .\dist\
```

Lub ręcznie per projekt:
```powershell
foreach ($p in @('vigil-watchdog','vigil-lockscreen','vigil-tray')) {
    dotnet publish $p -c Release -r win-x64 `
        -p:PublishSingleFile=true -p:SelfContained=true
}
```

### Krok 3: Install na PC dziecka

**Tryb interaktywny** (zalecany przy pierwszym razie — pyta o wszystkie parametry):
```
# Jako Administrator (dwuklik lub prawy klik → Uruchom jako administrator)
Install.cmd
```

**Tryb klasyczny z parametrami:**
```powershell
Install.cmd -Mode install `
    -ServerUrl 'http://192.168.0.2:8088' `
    -BearerToken '<bearer_token z /admin/ → Bezpieczeństwo>' `
    -SigningKey '<signing_key z /admin/ → Bezpieczeństwo>' `
    -ChildUsername 'jasiek' `
    -EmergencyPassword '<silne hasło — rodzic je zapamiętuje>'
```

Installer wykonuje m.in.:
1. Tworzy `C:\ProgramData\Vigil\` z restrykcyjnym ACL
2. Kopiuje binarki i skrypty
3. Szyfruje `signing_key` przez DPAPI LocalMachine → `signing.dat`
4. Szyfruje `EmergencyPassword` przez DPAPI LocalMachine → `emergency.dat`
5. Rejestruje Windows Service `vigil_watchdog` (SDDL: Admin może query/start, nie stop/delete)
6. SafeBoot entries (Minimal + Network)
7. Scheduled Task "Vigil Guardian" (boot + co 5 min, SYSTEM)
8. Scheduled Task "Vigil Tray" (AtLogon, konto dziecka)
9. `bcdedit bootmenupolicy Standard` — wyłącza F8 menu
10. User policies (DisableTaskMgr, DisableRegistryTools, sticky keys off itp.) w Default
    profile i opcjonalnie bezpośrednio na koncie `-ChildUsername`

### Krok 4: Smoke test end-to-end

1. Zaloguj się jako dziecko, odczekaj chwilę → admin UI powinien pokazywać rosnący `minutes_used`
2. W admin UI ustaw manualną blokadę → lockscreen powinien się pojawić w ciągu 5s
3. Na lockscreen wpisz TOTP z Aegisa → lockscreen znika, grant +60 min
4. Awaryjny bypass (test): `Ctrl+Shift+Alt+E` na lockscreen → wpisz `EmergencyPassword`

## Limity i znane ograniczenia

- **Threat model: 8-letnie dziecko bez admin password.** System NIE jest odporny na rodzica/admina który celowo coś psuje.
- **Jeden kontener = jedno dziecko.** Wiele dzieci = wiele kontenerów na różnych portach (`VIGIL_CHILD`, `VIGIL_PORT` w `.env`).
- **Brak idle tracking** — serwer akumuluje czas gdy sesja jest aktywna. Win+L i odejście od PC nie zatrzymuje licznika.
- **„Weekend\" = Pt–Nd** (`weekday() >= 4`). Piątkowe popołudnie traktowane jak weekend. Admin UI odzwierciedla to etykietami "Pon–Czw" / "Pt–Nd".
- **Fail-closed** — serwer niedostępny >30s grace → `lock_reason="server_unreachable"`. Brak sieci = ekran blokady.

## Update / uninstall

```powershell
# Zmiana adresu serwera:
Install.cmd -Mode update-config -ServerUrl 'http://192.168.0.5:8088'

# Pełen reset:
Install.cmd -Mode reinstall -ServerUrl '...' -BearerToken '...' -SigningKey '...'

# Uninstall:
Cleanup.cmd
# lub:
Install.cmd -Mode uninstall
# Lub offline (z WinRE / trybu awaryjnego):
.\vigil-cleanup.ps1
```

## Troubleshooting

```powershell
# Stan serwisu:
Get-Service vigil_watchdog

# Logi watchdoga:
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Vigil Watchdog'} -MaxEvents 20

# Logi guardiana:
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Vigil Guardian'} -MaxEvents 20

# Test połączenia z serwerem:
Test-NetConnection 192.168.0.2 -Port 8088
```

Częste przyczyny braku heartbeatu: `vigil.toml` lub `signing.dat` nie istnieją (przerwana instalacja),
serwer niedostępny w sieci, HMAC mismatch (bearer/signing_key niezgodny z serwerem → reinstall).

## Licencja

System dla użytku własnego — serwer w prywatnym LAN, dane dziecka nie wychodzą poza sieć domową.
Brak telemetrii, brak zewnętrznych zależności poza Docker base images i NuGet packages.
