# vigil_lockscreen

WPF lockscreen z named pipe IPC do vigil_watchdog. Watchdog spawnuje lockscreen w sesji dziecka
przez `CreateProcessAsUser`.

## Pliki

- `App.xaml(.cs)` — connect IPC na startup, dispatch IpcSetState/IpcExit do ViewModel,
  multi-monitor (jeden LockWindow per ekran), zegar, lifecycle TopmostEnforcer +
  publiczne `PauseTopmostEnforcer()` / `ResumeTopmostEnforcer()` (używane wokół dialogu awaryjnego)
- `LockWindow.xaml(.cs)` — fullscreen UI: główny komunikat, pole TOTP (6 cyfr), Retry,
  Wyłącz komputer. TOTP attempt = od razu grant +60 min (uproszczenie UX).
  Hotkey `Ctrl+Shift+Alt+E` (PreviewKeyDown) otwiera `EmergencyWindow`.
- `EmergencyWindow.xaml(.cs)` — modalny dialog awaryjnego odblokowania. PasswordBox
  + DPAPI verify przez `EmergencyAuth`, sukces zapisuje `bypass.flag`. Shutdown PC robi się
  z głównego `LockWindow` (osobny przycisk widoczny gdy `State.ShowShutdownButton`).
- `ViewModels/LockViewModel.cs` — state: `Phase` (Locked → GrantPending → GrantApplied),
  Reason, MainMessage, StatusMessage, CurrentTime
- `UiState.cs` — wire format z watchdoga (LockReason enum, ShowTotpField etc.)
- `IpcMessages.cs` — protokół IPC (JSON line-delimited) + serializer
- `NamedPipeIpcClient.cs` — async reader, request/response correlation dla TOTP
- `Native/KeyboardBlocker.cs` — WH_KEYBOARD_LL hook blokujący Win/Alt+Tab/Alt+F4/Ctrl+Esc/F1/F11/F12
- `Native/TopmostEnforcer.cs` — co 500 ms re-asserts Z-order + foreground na wszystkich LockWindow.
  `AttachThreadInput` hack żeby pominąć Windows foreground-lock. **Pauzowany w trakcie `EmergencyWindow`**,
  inaczej kradłby focus z PasswordBox co 500 ms.
- `Native/TaskbarHider.cs` — ukrywa Shell_TrayWnd i Shell_SecondaryTrayWnd na czas trwania
  lockscreen. Przywraca w `OnExit`.
- `Native/EmergencyAuth.cs` — DPAPI Protect/Unprotect dla `emergency.dat` (verify hasła)
  i `bypass.flag` (zapis flagi 24h dla watchdoga).

## Protokół IPC (line-delimited JSON, bidirectional)

**Watchdog → Lockscreen:**
- `set_state` — pełen stan UI (lock_reason, main_message, show_totp_field, etc.)
- `totp_response` — odpowiedź na totp_attempt (ok, mode, applied, reason, lockout_seconds)
- `exit` — lockscreen zamknij się

**Lockscreen → Watchdog:**
- `totp_attempt` — kod TOTP + minutes_to_grant (zawsze 60)
- `retry_now` — wymuś natychmiastowy heartbeat (po recovery sieci)
- `shutdown_request` — user kliknął "Wyłącz komputer" w `LockWindow`

`EmergencyWindow` świadomie **nie używa IPC** — jego sukces zapisuje wyłącznie lokalny
`bypass.flag`. Watchdog czyta flagę co tick i sam decyduje czy spawnować lockscreen. To pozwala
rodzicowi zdjąć blokadę nawet gdy IPC / watchdog / sieć leżą.

## Standalone test mode

Aktywuje przez argv `--standalone-test` lub env `VIGIL_LOCKSCREEN_STANDALONE=1`. W standalone:
- Pipe NIE jest tworzony
- TOTP "123456" akceptowany
- Każdy grant symulowany sukcesem po 500ms

W deployment zawsze IPC: brak watchdoga = lockscreen się zamyka po 5s timeout.

## Build (Windows + dotnet 8 SDK z Desktop workload)

```powershell
cd vigil-lockscreen
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o publish
```

Skopiuj `vigil_lockscreen.exe` do `C:\ProgramData\Vigil\` (installer robi to automatycznie).

## Flow end-to-end (normalny, TOTP)

```
[Watchdog: tick]                    [pipe]                         [Lockscreen]
   |                                                                |
   | EnsureLockedAsync(state)                                       |
   |─ launcher.Launch ──> CreateProcessAsUser ─────────────────────>| spawn
   |                                                                | App.OnStartup
   |                                                                | IPC.ConnectAsync
   |<───────────────────  pipe connect ─────────────────────────────|
   |─ pipe.SendAsync(IpcSetState) ─────────────────────────────────>| HandleIpcMessage
   |                                                                | ViewModel.State = ...
   |                                                                | LockWindow shown
   |                                                                |
   |                                                                | user wpisuje TOTP, klika Sprawdź
   |                                                                | LockWindow.ApplyGrant(60)
   |<──────────── IpcTotpAttempt {code, minutes=60} ────────────────| ipc.SendTotpAsync
   | OnTotpAttempt event                                            |
   | HeartbeatClient.SendTotpAttemptAsync                           |
   | (POST /v1/totp_attempt)                                        |
   | resp.ok=true                                                   |
   |─ IpcTotpResponse {ok, mode, applied} ────────────────────────> | TCS.SetResult
   |                                                                | Phase = GrantApplied
   |                                                                | "Dodano 60 minut."
   |                                                                |
   | [następny heartbeat: minutes_used < limit + override → unlock] |
   | _lockscreen.DismissAsync                                       |
   |─ IpcExit ────────────────────────────────────────────────────> | Shutdown(0)
   | (po 2s czeka, jeśli nadal działa: launcher.Kill())             |
```

## Flow awaryjny (Ctrl+Shift+Alt+E)

```
[Lockscreen aktywny]
   |
   | user: Ctrl+Shift+Alt+E       (PreviewKeyDown w LockWindow)
   | LockWindow.OpenEmergencyDialog()
   |   ├─ EmergencyAuth.IsConfigured()  → false? return (no-op)
   |   ├─ App.PauseTopmostEnforcer()    (żeby focus nie uciekał z PasswordBox)
   |   └─ new EmergencyWindow { Owner = this }.ShowDialog()
   |        |
   |        | user wpisuje hasło + Enter / klik Odblokuj
   |        | EmergencyAuth.TryVerify(pwd)   (DPAPI Unprotect emergency.dat, constant-time compare)
   |        |   ├─ false → "Hasło niepoprawne", clear input, focus
   |        |   └─ true  → EmergencyAuth.WriteBypassFlag(24h)  (DPAPI Protect → bypass.flag)
   |        |              Unlocked = true, Close()
   |
   | App.ResumeTopmostEnforcer()  (finally — niezależnie od wyniku)
   |
   | if (dlg.Unlocked)
   |   Application.Current.Shutdown(0)        ← lockscreen znika
   |
[Watchdog co tick]
   | reads bypass.flag → DPAPI Unprotect → parse valid_until_utc
   | now < valid_until → SKIP lockscreen spawn przez 24h
```

## Ograniczenia

- **Brak secure desktop** — lockscreen na `WinSta0\Default`. WPF wymaga DWM którego custom
  `CreateDesktop()` nie zapewnia. Bezpieczeństwo: KeyboardBlocker low-level hook. Threat
  model 8-letnie dziecko bez admin uprawnień — akceptowalne.
- **Pipe DACL** — SYSTEM full + Authenticated Users RW (z peer-process verification). PC dziecka z założenia jedno konto.
- **TopmostEnforcer pauzowany w dialogu awaryjnym** — przez czas wpisywania hasła (typowo kilka
  sekund) inne okno z `WS_EX_TOPMOST` mogłoby teoretycznie zasłonić lockscreen. W praktyce
  źródła takich okien (UAC, system update) nie występują u 8-latka grającego w gry, więc trade-off
  jest akceptowalny. KeyboardBlocker pozostaje aktywny przez cały czas.
