# vigil_watchdog

Worker Service .NET 8. Watchdog na PC dziecka:
- HMAC-signed heartbeat client (co 5s do servera)
- Lockscreen orchestration przez named pipe IPC do vigil_lockscreen.exe
- Tray icon IPC (vigil_tray.exe) — pauza + pozostały czas

## Komponenty

| Folder | Co |
|--------|-----|
| `Configuration/` | `VigilConfig` (TOML loader) + `DpapiKeyStore` (signing.dat) |
| `Server/` | `HeartbeatClient` (HMAC, nonce, timestamp), POCO requests/responses |
| `Native/` | P/Invoke deklaracje (Win32 API: WTS, tokens, CreateProcessAsUser) |
| `Detection/` | `SessionDetector` (WTS enumerate, pierwszy WTSActive/WTSConnected) |
| `Lockscreen/` | `RealLockscreenManager`, `NamedPipeIpcServer`, `LockscreenProcessLauncher`, `IpcMessages` |
## Co robi co heartbeat

- Detekcja: czy ktoś jest zalogowany (session_active = WTSActive lub WTSConnected)
- Buduje `HeartbeatRequest`: `session_active`, `user_active`, `current_time_utc`, `uptime_seconds`
- HMAC-signed POST `/v1/heartbeat` z nonce/timestamp
- Weryfikacja podpisu odpowiedzi (X-Signature header)
- Jeśli `lock_required: true` → spawn/update lockscreen przez pipe
- Fail-closed: brak heartbeat > grace (90s fresh boot, 30s steady) → lockscreen `server_unreachable`

## Build

```powershell
cd vigil-watchdog
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o publish
```

Output: `publish/vigil_watchdog.exe` (~80MB self-contained). Installer kopiuje do
`C:\ProgramData\Vigil\` razem z pozostałymi binarkami.

## Pierwsze uruchomienie (DPAPI key bootstrap)

Pierwsze uruchomienie wymaga signing_key z env lub argv:

```powershell
vigil_watchdog.exe --signing-key=<base64-urlsafe-key-z-bootstrap-servera>
# lub
$env:PC_SIGNING_KEY = '<key>'
vigil_watchdog.exe
```

Watchdog szyfruje klucz przez DPAPI LocalMachine, zapisuje `signing.dat`, kolejne starty
nie wymagają już parametru. Installer (`Install.cmd`) robi to automatycznie podczas instalacji.

## Ograniczenia

- **Win+L** nie zatrzymuje akumulacji czasu — system nie wykrywa zalockowanej sesji.
  Mała wartość bezpieczeństwa (Win+L wymaga ponownego logowania), nie ucieczka.
- **Multi-user PC** — `SessionDetector` zwraca pierwszą `WTSActive`. PC dziecka z założenia
  jedno konto.
