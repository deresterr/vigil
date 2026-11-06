# Architecture (as of v0.4)

Describes the system as it is. Planned changes live in `review-2026-11.md`.

```
 Parent: browser (/admin) + TOTP app on phone
              │
 ┌────────────▼─────────────┐   HTTP, LAN/VPN    ┌────────────────────────────────────────┐
 │ server (Docker)          │◄──────────────────►│ vigil_watchdog  (Windows Service, SYSTEM)│
 │ FastAPI + SQLite (WAL)   │  /v1/* every 1–30 s │   │ \\.\pipe\VigilUI      │ \\.\pipe\VigilTray
 │ 1 container = 1 child PC │                     │   ▼                       ▼              │
 └──────────────────────────┘                     │ vigil_lockscreen (WPF)  vigil_tray (WinForms)
                                                  │  spawned on lock via     started by task
                                                  │  CreateProcessAsUser     "Vigil Tray" at logon
                                                  │ vigil_guardian.ps1 (task "Vigil Guardian", SYSTEM, 5 min)
                                                  └────────────────────────────────────────┘
```

## Server ↔ watchdog protocol (`/v1/*`)

Protocol v2. Request headers: `X-Timestamp` (unix ms), `X-Nonce` (16 random bytes, base64url),
`X-Signature = base64(HMAC-SHA256(signing_key, METHOD\nPATH\nTIMESTAMP\nNONCE\nhex(sha256(body))))`,
where PATH is the API route (`/v1/heartbeat`) without any reverse-proxy prefix. Server rejects (401, unsigned):
bad signature, age > `max_request_age_seconds` (60), > 5 s in the future, reused nonce (`used_nonces`,
pruned after `nonce_cache_seconds`). A rejected request never stores its nonce. No bearer token (removed in v2).
Against an admin child this is friction: the key is in `signing.dat`, readable by an elevated process.

Response header `X-Signature = base64(HMAC-SHA256(signing_key, nonce_ascii + body))`, added by
`HmacSigningMiddleware` only after `authenticate_request` succeeded. The watchdog treats a missing/invalid
signature like "server unreachable".

| Endpoint | Body | Notes |
|---|---|---|
| `POST /v1/heartbeat` | `session_active, user_active, current_time_utc, uptime_seconds` | → `lock_required, lock_reason, minutes_used, minutes_remaining, next_check_seconds, server_time_utc, ui_state` |
| `POST /v1/totp_attempt` | `code`, optional `minutes_to_grant` (default 60) | mode chosen by server from current lock: limit→`extend_limit`, night→`extend_night`, manual→`manual_unlock`. every 3rd failure locks for 1 min / 5 min / 15 min (cap); a code's time step can be used once (`totp_last_step`) |
| `POST /v1/pause_toggle` | `{}` | refused while a higher-priority lock is active |
| `GET /v1/claim_credentials` | — | **unauthenticated**, only while `init_open=1`; returns signing key and server URL to the installer |

`ui_state` (only when locked): `main_message` (Polish, from server), `show_totp_field`, `show_resume_button`,
`show_retry_button`, `show_shutdown_button`, `current_time_display`.

## Time accounting (`heartbeat.py`)

1. `delta = now − last_heartbeat_utc`, clamped to `0..heartbeat_delta_max` (15 s).
2. Counted only if this request has `session_active && user_active` **and** the previous active heartbeat
   was ≤ `gap_detection_threshold` (30 s) ago (sleep/hibernate ⇒ nothing counted).
3. If a lock is already active (pre-lock check) nothing is counted.
4. Lock reason recomputed with the new total. Priority `manual > night > limit > pause` (`lock_logic.py`).

State is one row per local date in `daily_state`: `seconds_used, limit_override_minutes,
night_extension_until_utc, manual_lock, pause_active, last_heartbeat_utc, last_session_active_utc`.
A new day's row inherits `manual_lock` and a still-active `night_extension_until_utc` from the previous row. Weekend limit applies Fri–Sun (`is_weekend`).
`user_active` is currently just `session_active` (no idle detection).

Other tables: `config` (key/value incl. secrets: `signing_key`, `totp_secret`,
`admin_session_key`, `parent_password_hash`), `events` (audit log, pruned after `events_retention_days`),
`totp_lockout` (single row), `used_nonces`.

## Server lifecycle

First start: `bootstrap.py` generates secrets; `setup_complete=0` ⇒ `setup_guard` middleware redirects
everything to `/setup` (unauthenticated wizard: device name, parent password, TOTP, limits, server URL).
Finishing setup sets `init_open=1`; the installer calls `/v1/claim_credentials`; the first heartbeat closes
`init_open`. Admin can reopen it from the panel (same keys are handed out again).

Admin: `/admin` (static SPA) + `/admin/api/*` behind an itsdangerous-signed cookie `vigil_session`
(24 h, HttpOnly, SameSite=Strict, path `/admin`). Password: argon2id.

## Watchdog loop (`Worker.cs`)

Every 5 s (1 s while locked, up to 60 s if the server says so):
1. `BypassFlagChecker.IsActive()` — if a valid `bypass.flag` exists: dismiss lock, send heartbeat, stop.
2. `SessionDetector` — any WTS session ≠ 0 in Active/Connected state.
3. Heartbeat → `EnsureLockedAsync` / `DismissAsync`, push state to tray.
4. No valid response for > grace ⇒ lock with reason `server_unreachable` (local message).

Lock = spawn `vigil_lockscreen.exe` in the user session (`WTSQueryUserToken` → `CreateProcessAsUser`,
desktop `WinSta0\Default`), wait ≤ 10 s for it to connect to `VigilUI` (peer verified by exe path),
send `set_state`. Dismiss = `exit` message, kill after 1 s, then always spawn
`vigil_lockscreen.exe --restore-taskbar-only`.

Lockscreen hardening: fullscreen on every monitor, `TopmostEnforcer` (500 ms), `KeyboardBlocker`
(WH_KEYBOARD_LL: Win, Alt+Tab, Alt+F4, Ctrl+Esc), `TaskbarHider`. Ctrl+Alt+Del cannot be blocked;
installer policies (`DisableTaskMgr`, `DisableLockWorkstation`, `NoLogoff`, …) cover that path.

## IPC (JSON, one message per line)

| Pipe | Server | Client | Messages |
|---|---|---|---|
| `VigilUI` | watchdog | lockscreen | → `set_state`, `exit`, `totp_response`; ← `totp_attempt`, `retry_now`, `resume_from_pause`, `shutdown_request` |
| `VigilTray` | watchdog | tray | → `state_update` (`paused`, `can_pause`, `minutes_remaining`); ← `pause_toggle` |

Pipe DACL: SYSTEM + Administrators full, Authenticated Users read/write.

## Emergency unlock (offline)

Hotkey Ctrl+Shift+Alt+E in the lockscreen → password compared with `emergency.dat`
(DPAPI LocalMachine, plaintext password) → lockscreen writes `C:\Users\Public\Vigil\bypass.flag`
(DPAPI LocalMachine, `valid_until_utc`, 24 h). See review: both files are forgeable/readable by the child.

## Windows footprint

```
C:\ProgramData\Vigil\   vigil_watchdog.exe, vigil_lockscreen.exe, vigil_tray.exe, vigil_guardian.ps1,
                        vigil.toml (server_url only), signing.dat, emergency.dat, guardian.dat,
                        backup\vigil_watchdog.exe
                        ACL: SYSTEM full, Administrators modify, Authenticated Users read+execute (inherited)
C:\Users\Public\Vigil\  bypass.flag
Service        vigil_watchdog ("Vigil Watchdog"), auto start, restart ×3, restricted DACL
Tasks          "Vigil Guardian" (SYSTEM, boot + 5 min), "Vigil Tray" (child, at logon)
Registry       HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\{Minimal,Network}\vigil_watchdog
Event Log      sources "Vigil Watchdog", "Vigil Guardian"
```

Guardian checks: service exists, running, binary hash (restore from backup), IFEO debugger,
SafeBoot keys, recovery config, tray task.
