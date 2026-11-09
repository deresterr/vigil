# Architecture (as of v0.6)

Describes the system as it is. Planned changes live in `review-2026-11.md`.

```
 Parent: browser (/admin) + TOTP app on phone
              │
 ┌────────────▼─────────────┐   HTTP, LAN/VPN     ┌─────────────────────────────────────────────┐
 │ server (Docker)          │◄───────────────────►│ vigil_watchdog  (Windows Service, SYSTEM)   │
 │ FastAPI + SQLite (WAL)   │  /v1/* every 1–60 s │   │ \\.\pipe\VigilUI                          │
 │ children + their PCs (D7)│                     │   ▼                                         │
 └──────────────────────────┘                     │ vigil_ui (WPF, child's session): tray icon   │
                                                  │   always, lock overlay on demand; started    │
                                                  │   and kept alive by the watchdog             │
                                                  │ vigil_setup.exe guardian (task "Vigil        │
                                                  │   Guardian", SYSTEM, boot + 5 min)           │
                                                  └─────────────────────────────────────────────┘
```

## Server ↔ watchdog protocol (`/v1/*`)

Protocol v2. Request headers: `X-Timestamp` (unix ms), `X-Nonce` (16 random bytes, base64url),
`X-Signature = base64(HMAC-SHA256(signing_key, METHOD\nPATH\nTIMESTAMP\nNONCE\nhex(sha256(body))))`,
where PATH is the API route (`/v1/heartbeat`) without any reverse-proxy prefix. Server rejects (401, unsigned):
bad signature, age > `max_request_age_seconds` (60), > 5 s in the future, reused nonce (`used_nonces`,
pruned after `nonce_cache_seconds`). A rejected request never stores its nonce. No bearer token (removed in v2).
Against an admin child this is friction: the key is in `signing.dat`, readable by an elevated process.

Which device (D7): every paired PC has its own key (`devices.signing_key`). Requests carry no device id; the
server checks the HMAC with every device's key (all of them, no early exit) and the match is the sender
(`auth.Device`: id, child_id). A key speaks only for its own device and child.

Response header `X-Signature = base64(HMAC-SHA256(signing_key, nonce_ascii + body))`, added by
`HmacSigningMiddleware` only after `authenticate_request` succeeded. The watchdog treats a missing/invalid
signature like "server unreachable".

| Endpoint | Body | Notes |
|---|---|---|
| `POST /v1/heartbeat` | `session_active, user_active, current_time_utc, uptime_seconds` | → `lock_required, lock_reason, minutes_used, minutes_remaining, next_check_seconds, server_time_utc, ui_state` |
| `POST /v1/goodbye` | `instance_id, reason` (`service_stop`\|`shutdown`) | sent from the service's StopAsync; see Tamper detection |
| `POST /v1/totp_attempt` | `code`, optional `minutes_to_grant` (default 60) | mode chosen by server from current lock: limit→`extend_limit`, night→`extend_night`, manual→`manual_unlock`; response `mode`, `minutes`, `until_local` (night). every 3rd failure locks for 1 min / 5 min / 15 min (cap); a code's time step can be used once (`totp_last_step`) |
| `POST /v1/pause_toggle` | `{}` | refused while a higher-priority lock is active |
| `POST /v1/pair` | `code` | **unauthenticated**; one-time 8-digit code from the panel (10 min, 5 attempts) → new `signing_key` + `server_url`. The code is made for a new device of a child, or for re-pairing a device (its key is replaced) |
| `POST /v1/tamper_event` | `type` `lockscreen_killed` \| `guardian_repaired` | see Tamper detection |

Request bodies are Pydantic models in `app/models.py` (strict types, unknown fields ignored) — a contract with
`HeartbeatModels.cs`.

`ui_state` (only when locked): `show_totp_field`, `show_resume_button`, `show_retry_button`, `show_shutdown_button`,
`current_time_display`.

**Language (i18n, #52):** the server sends codes and numbers, never sentences: `lock_reason`, TOTP `mode` with
`minutes` and (night) `until_local`. Each UI words them in its own environment's language — the panel by the
browser (`static/i18n.js`), the lock screen and tray by the child's Windows (`vigil_ui`, `Strings.cs`; the watchdog
runs as SYSTEM and does not know it, so the IPC carries codes too: `lock_reason` + `show_totp_field`, TOTP `mode`,
`minutes`, `until_local`). Polish and English; the installer stays Polish until it is rewritten.

## Children, devices and time accounting (`heartbeat.py`, D7)

One server, many children, each with any number of PCs. The weekly schedule (`child_schedule`, #48) and all
locks (limit, night, manual, pause) are **per child**: a lock or pause applies to every PC of the child. Time is the **sum over the child's devices** (two PCs at once count twice).

Per heartbeat of a device:
1. `delta = now − devices.last_heartbeat_utc`, clamped to `0..heartbeat_delta_max` (15 s).
2. Counted only if this request has `session_active && user_active` **and** the device's previous active
   heartbeat (`devices.last_active_utc`) was ≤ `gap_detection_threshold` (30 s) ago.
3. If the child is already locked (pre-lock check) nothing is counted.
4. The seconds go to the child's `child_daily.seconds_used` (atomic increment) and the device's `seconds_today`.
5. Lock reason recomputed with the new total. Priority `manual > night > limit > pause` (`lock_logic.py`).
   `lock_started` is logged once per child (`children.last_lock_reason`), not once per device.

Tables: `children` (name, `last_lock_reason`), `child_schedule` (one row per child and weekday, Monday = 0:
`daily_limit, night_start, night_end`), `devices` (`child_id`, name, `signing_key`, last heartbeat /
activity, `seconds_today`, tamper baseline `wd_*`), `child_daily` (one row per child and local date:
`seconds_used, limit_override_minutes, night_extension_until_utc, manual_lock, pause_active`). A new day's row
inherits `manual_lock` and a still-active `night_extension_until_utc` from the child's previous row. The limit
is the schedule row of the local date's weekday. A night **belongs to the evening of its day** (D19): it starts
at that day's `night_start` — on the next day when `night_start` is before 12:00 (after midnight) — and ends at
the first `night_end` after that, so "Friday 23:00–09:00" covers Friday 23:00 to Saturday 09:00
(`lock_logic.night_window`). A night grant counts from the start of the night in progress or the next one.
`user_active` is just `session_active` (no idle detection, D16).
One TOTP secret for all children (one `totp_lockout` row, one `totp_last_step`).

Schema changes only through `migrations.py` (`PRAGMA user_version` = migrations applied, one transaction
each; a database newer than the code is refused). Config is read from SQLite on every request, no cache;
missing keys fall back to `CONFIG_DEFAULTS`.

Other tables: `config` (server-wide key/value incl. secrets: `totp_secret`,
`admin_session_key`, `parent_password_hash`), `events` (audit log, pruned after `events_retention_days`),
`totp_lockout` (single row), `used_nonces`. `events.child_id`/`device_id` say whom an event concerns.

## Tamper detection (`tamper.py`)

Heartbeats also carry `instance_id` (new per watchdog process), `awake_ms` (QueryUnbiasedInterruptTime:
system time without sleep/hibernate, restarts near 0 on boot), `awake_ms_at_start`, `failed_attempts`.
Compared with the same device's previous heartbeat (`devices.wd_*`, reset when it is re-paired):

| Situation | Event |
|---|---|
| new instance, same boot, no goodbye | `tamper_watchdog_restarted` (`down_awake_seconds`) |
| goodbye `service_stop` | `tamper_service_stopped` |
| same instance, awake gap > 90 s, 0 failed attempts | `tamper_watchdog_silent` (suspended/frozen) |
| same instance, awake gap > 90 s, retries | `watchdog_offline` (info) |
| new boot / goodbye `shutdown` | `watchdog_started` / `watchdog_shutdown` (info) |
| vigil_ui we spawned exits on its own 3× within 60 s (kill loop), locked or not | session logged off (`WTSLogoffSession`) + `tamper_lockscreen_killed` via `POST /v1/tamper_event` |

| the guardian repaired something (Event Log 7001–7006) | `tamper_guardian_repaired` (`check`, `repaired`), stamped with the repair time; sent by `GuardianReporter` every minute, deduped by record id + time |

`tamper_*` = alerts for the parent. Not detected: a watchdog that is stopped and never comes back
(the panel only shows "offline"), and anything an admin forges with the signing key.

## Server lifecycle

First start: `bootstrap.py` generates secrets; `setup_complete=0` ⇒ `setup_guard` middleware redirects
everything to `/setup` (unauthenticated wizard: first child's name and limits, parent password, TOTP, server URL).
In the panel the parent pairs PCs: a code for "new PC of child X named Y" or "re-pair device Z" (only its
hash is stored, with its target: `pairing_*` config keys); `vigil_setup.exe` sends it to `/v1/pair` and stores the
returned key in `signing.dat`. Re-pairing replaces that device's key (the old install fails closed); "Odłącz"
deletes the device row, so its key stops working. There is no way to read a device key in the panel.

Migration from a single-PC database (v0.4/v0.5, `migrations._m002_children_devices`): it becomes child 1
(named after its `device_name`) with device 1 (its old key, if the PC ever connected); production needs a
re-pair anyway, because v0.4 speaks protocol v1.

Journal tab (`endpoints/journal.py`, `GET /admin/api/journal?days=&before=&child_id=`): per local day, newest first
by `ts_utc` (not insertion order: `watchdog_offline` and `offline_unlock` arrive on reconnect and carry the
time they happened) —
PC on/off (`watchdog_*`), lock starts (`lock_started`, logged by the heartbeat when `last_lock_reason` changes
to limit/night), pause (on/off paired into one entry), parent actions that change locking, and alerts.
Alerts = `tamper_*` + `totp_lockout`, `admin_login_locked`, `pairing_code_invalidated`; unconfirmed ones are
highlighted and counted on the tab (`alerts_unacked` in `/status`), confirmed via `POST /admin/api/alerts/ack`
(`events.acked_utc`). Polish wording and filters live in `admin.html`.

Admin: `/admin` (static SPA) + `/admin/api/*` behind an itsdangerous-signed cookie `vigil_session`
(24 h, HttpOnly, SameSite=Strict, path `/admin`). Password: argon2id.

## Watchdog loop (`Worker.cs`)

Every 5 s (1 s while locked, up to 60 s if the server says so):
1. `UiManager.EnsureRunningAsync` — vigil_ui running and connected in the active session (below).
2. `SessionDetector` — any WTS session ≠ 0 in Active/Connected state.
3. Heartbeat → `EnsureLockedAsync` / `DismissAsync`, tray state.
4. No valid response for > grace ⇒ lock with reason `server_unreachable` (local message), unless an
   offline grant is active (see Offline unlock). Also on request timeouts and unexpected exceptions.

vigil_ui (D8) is spawned in the active session (`WTSQueryUserToken` → `CreateProcessAsUser`, desktop
`WinSta0\Default`) with a process/thread DACL that gives the user no terminate, suspend or WRITE_DAC
(`OW` caps the owner's implicit rights; SYSTEM and BA full) — friction: an elevated admin can still
kill it. The watchdog creates the pipe first, then waits ≤ 10 s for the connect (or less, if the process
dies) and accepts only the PID it spawned. After a respawn it re-sends the lock and tray state, because
a new UI starts unlocked. Lock = `set_state`, unlock = `unlock`; the UI hides the taskbar while locked
and restores it on unlock and on start (an instance killed while locked leaves it hidden). Orphans in
the session (previous watchdog instance; `vigil_lockscreen`/`vigil_tray` of versions before v0.6) are
killed before a spawn. The UI quits when the pipe connect fails and 30 s after losing the pipe.
Diagnostics: `%LOCALAPPDATA%\Vigil\ui.log`.

The TOTP field accepts only digits (typed or pasted, max 6) and regains focus after each check.

Lock overlay hardening: fullscreen on every monitor, `TopmostEnforcer` (500 ms), `KeyboardBlocker`
(WH_KEYBOARD_LL: Win, Alt+Tab, Alt+F4, Ctrl+Esc), `TaskbarHider`. Ctrl+Alt+Del cannot be blocked;
the installer's per-user policies close what it offers: `DisableTaskMgr` (Task Manager over the overlay)
and `DisableChangePassword` (the shared account's password), plus accessibility hotkeys and touchpad
gestures off. Nothing else (D12: they hit the parent too); older installs' `DisableRegistryTools`,
`DisableLockWorkstation` and `NoLogoff` are removed on install.

## IPC (JSON, one message per line)

| Pipe | Server | Client | Messages |
|---|---|---|---|
| `VigilUI` | watchdog | vigil_ui | → `set_state`, `unlock`, `tray_state` (`paused`, `can_pause`, `minutes_remaining`), `totp_response`, `exit`; ← `totp_attempt`, `retry_now`, `resume_from_pause`, `pause_toggle`, `shutdown_request` |

Messages are defined once in `client/shared/IpcMessages.cs`, linked into both projects.
Pipe DACL: SYSTEM + Administrators full, Authenticated Users read/write; the client PID check keeps
other programs out.

## Offline unlock (`offline.py`, `Offline/OfflineUnlock.cs`)

When the watchdog has failed closed (`server_unreachable`), a TOTP code typed on the lock overlay is
verified by the watchdog itself: ±1 step, each step once, every 3rd failure locks 60/300/900 s (cap),
grant ≤ 120 min, ended as soon as the server answers. The secret comes from the server in the signed
heartbeat response whenever the watchdog's `totp_fp` (sha256[:16]) differs — after pairing or a TOTP
regeneration — and is stored in `totp.dat` (DPAPI LocalMachine, entropy `Vigil-Totp-v1`, SYSTEM full /
Administrators read). Grants are queued and sent as `offline_grants` with the next successful heartbeat;
the server logs `offline_unlock` (journal) and burns the step. Friction + detection: an admin child can
read `totp.dat`. Installs before v0.5 used `emergency.dat` + `C:\Users\Public\Vigil\bypass.flag`;
the installer removes both.

## Windows footprint

```
C:\ProgramData\Vigil\   vigil_watchdog.exe, vigil_ui.exe, vigil_setup.exe (the guardian, #65),
                        vigil.toml (server_url only), signing.dat, totp.dat, guardian.dat,
                        backup\vigil_watchdog.exe
                        ACL: SYSTEM full, Administrators modify, Authenticated Users read+execute (inherited)
Service        vigil_watchdog ("Vigil Watchdog"), auto start, restart ×3, restricted DACL
Tasks          "Vigil Guardian" (SYSTEM, boot + 5 min): vigil_setup.exe guardian
Registry       HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\{Minimal,Network}\vigil_watchdog
Event Log      sources "Vigil Watchdog", "Vigil Guardian"
```

Guardian (`docs/guardian.md`): binary hash (restore from backup), IFEO debugger, automatic start,
recovery actions, SafeBoot keys, service running; each repair is an Event Log entry the watchdog reports.
