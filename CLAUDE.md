# Vigil — notes for Claude

Self-hosted parental screen-time control for Windows. Owner: Mateusz. **Talk to him in Polish**;
he knows the stack deeply — be direct, skip basics, fix root causes, apply a fix everywhere it applies.

## Layout
- `server/` — FastAPI + SQLite, one Docker container per child PC. Entry `app/main.py`,
  time/lock rules `app/lock_logic.py` + `app/endpoints/heartbeat.py`. Admin UI = static HTML in `app/static/`.
- `client/` — .NET 8 (`Vigil.sln`): `watchdog` (Windows Service, SYSTEM), `lockscreen` (WPF, user session),
  `tray` (WinForms, user session).
- `installer/` — PowerShell install/cleanup + `vigil_guardian.ps1`. `build-package.ps1` → `dist/`.
- `docs/architecture.md` — protocol, IPC, file paths, time accounting. Read it before touching those.
- `docs/review-2026-11.md` — known defects, decisions (D1–D13), roadmap (M1 done, M2–M4).
  Other `docs/*.md` are older component notes; code wins.

## Verify before saying "done"
- Server: `cd server && pip install -r requirements-dev.txt && ruff check . && pytest -q`
- Client: cannot build on Linux; CI (`.github/workflows/ci.yml`, windows-latest) compiles it
  (path-filtered on PRs; every push to main builds everything and is never cancelled).
  Push the branch and read the CI result instead of guessing. CI uploads `dist/` as an artifact —
  that package is what Mateusz runs on the Windows VM for runtime tests.
- **One PR with client changes per VM test session.** VM tests are expensive for Mateusz: batch every client
  change waiting for a runtime test into one PR (merge the issue branches into it, as #29 did for #9 + #10)
  and hand over one test checklist with the CI artifact. He runs the server from the same PR branch.
  Server-only changes still go one issue per PR. No stacked PRs: every PR targets `main`.
- New server behaviour gets a test. Known bugs are `xfail(strict=True)` — remove the marker when fixed.

## Invariants (changing any of these is a design decision — ask first)
- **Fail-closed**: server unreachable > grace (30 s; 90 s after service start) ⇒ lock. Never fail-open.
- **uvicorn `--workers 1`**: time accounting relies on a single process + `asyncio.Lock` in `get_db()`.
- **Time is server-side**: delta between heartbeats, clamped to `heartbeat_delta_max`, zero after a gap.
  No time accrues while a lock is active (pre-lock check happens before adding the delta).
- Lock priority: `manual > night > limit > pause`. `manual_lock` carries over midnight.
- **DPAPI entropy strings** (`Vigil-SigningKey-v1`, `Vigil-Totp-v1`) and file
  names under `C:\ProgramData\Vigil\` are a compatibility contract with existing installs.
- **Threat model**: the child MAY have an administrator account. Against an admin nothing on the PC is
  tamper-proof, so every mechanism is one of: protection (holds even against admin, e.g. server-side state),
  friction (stops a non-elevated process / casual attempt), or detection (reports tampering to the parent panel).
  Say explicitly which one a change is. Secrets on the PC are SYSTEM-only, knowing an admin can still read them.
- **Target**: Windows 10/11 x64, **Home and Pro**. No Pro-only features (gpedit, AppLocker, secpol, BitLocker
  as a requirement); policies only as registry values, which Home honours.
- **One account**: the child's account is the only interactive account on the PC and is an administrator.
  The parent maintains the PC through that same account, so per-user policies (HKCU) hit the parent too.
- Service DACL (`sc sdset`) denies Admin stop/delete: use `sc.exe stop` + poll, or a SYSTEM scheduled task.
  `Stop-Service` always fails with ACCESS_DENIED.

## Gotchas that already cost time
- PowerShell 5.1: `.ps1` must be UTF-8 **with BOM** + CRLF (see `.editorconfig`), otherwise Polish text breaks.
- PowerShell: `$PSScriptRoot` is empty inside `param()` defaults — compute it in the body.
- PowerShell: `"$var:"` parses as a drive-scoped variable — write `"${var}:"`.
- PowerShell/icacls: use SIDs (`*S-1-5-32-544`), never group names — Polish Windows localizes them.
- PowerShell 5.1 + `$ErrorActionPreference='Stop'`: `& native.exe 2>&1` turns stderr into a terminating error
  (fatal in `finally`). Call `reg.exe` only via `Invoke-RegExe` in `vigil_install.ps1`.
- One account (D12): policies the installer sets in HKCU (e.g. `DisableRegistryTools` → blocks `reg.exe`) also
  hit the installer itself on uninstall/reinstall. Edit user hives via the PowerShell registry provider first.
- `.ps1` syntax is checked in CI with Windows PowerShell 5.1; locally a Linux `pwsh` can run the same parser.
- C#: never filter with `when (ex is not OperationCanceledException)` — an `HttpClient` timeout is a
  `TaskCanceledException` and slipped past the fail-closed check (#31). Filter on the caller's token:
  `when (!ct.IsCancellationRequested)`.
- `client/tray` has `ImplicitUsings=disable`: add `System.IO`, `System.IO.Pipes`, `System.Text`,
  `System.Text.Json` explicitly (`System.IO.Pipes` does not bring `System.IO`).
- XAML `x:Class` must match the code-behind namespace (`Vigil.Lockscreen.*`).
- Session 0 isolation: the service cannot touch user-session windows — anything UI runs via
  `CreateProcessAsUser` (`LockscreenProcessLauncher`), e.g. the `--restore-taskbar-only` helper.
- `IpcMessages.cs` exists in both watchdog and lockscreen — keep them identical.

## Conventions
- New code, comments, commit messages: English. Existing Polish comments: leave unless touching the code.
  User-facing strings (UI, installer output): Polish for now.
- Python: plain `sqlite3` via `get_db()`; config through `get_config_value()`, defaults in `config.CONFIG_DEFAULTS`.
- C#: async I/O, `ILogger<T>`, P/Invoke in `Native/`.
- PowerShell: `Write-Step/OK/Warn/Skip` helpers, `$ErrorActionPreference = 'Stop'`.
- One issue = one branch = one PR, except client changes batched for one VM session (see above).
  Update this file in the same PR when an invariant or gotcha changes.
