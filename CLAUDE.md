# Vigil — notes for Claude

Self-hosted parental screen-time control for Windows. Be direct, skip basics, fix root causes, and apply a fix
everywhere the same problem occurs, not only where it was reported.

## Layout
- `server/` — FastAPI + SQLite, one server for many children and their PCs (D7). Entry `app/main.py`,
  time/lock rules `app/lock_logic.py` + `app/endpoints/heartbeat.py`. Admin UI = static HTML in `app/static/`;
  `pages.py` inlines the shared scripts (`i18n.js`, `schedule.js`: the weekly grid of the panel and the wizard).
- `client/` — .NET 8 (`Vigil.sln`): `watchdog` (Windows Service, SYSTEM), `ui` (WPF `vigil_ui`: tray + lock overlay,
  child's session, started and kept alive by the watchdog), `setup` (`vigil_setup.exe`, the C# installer of D22:
  console, trimmed, `requireAdministrator`; core in `Core/` never touches the console, `IReport` gets the output;
  without parameters `Screens/` shows the wizard or the status + menu on Spectre.Console, with `-Mode ...` the
  parameter mode for scripts and CI — both only call the core; `vigil_setup.exe guardian` is the guardian task,
  run as SYSTEM from the copy the installer puts in `C:\ProgramData\Vigil`, see `docs/guardian.md`),
  `shared/` linked into the projects that need it: `IpcMessages.cs` (watchdog + ui), `VigilLayout.cs` (paths,
  file names, entropy, service, SDDL, guardian event IDs, per-user policy table) and `FileAcls.cs` (watchdog + setup).
- `build-package.ps1 -Version` → `dist\vigil_setup.exe`, the whole client in one file (#76: the watchdog,
  vigil_ui and its native WPF libraries appended, format in `client/setup/Core/Package.cs`) + `build\parts\`
  (the files on their own). `Build.cmd` runs it. `docs/installer.md` describes the installer, the README
  the emergency path from WinRE.
- `e2e/installer-e2e.ps1` — installer end-to-end test: CI runs it on the throwaway `windows-latest` runner
  (server of the same commit on localhost; install → update → one-file package checks → broken installs →
  re-pair → uninstall → install → six guardian repairs reported to the journal → folder deleted "from WinRE" →
  uninstall, then the wizard and the menu through a key script). Never on a real PC.
- `docs/architecture.md` — protocol, IPC, file paths, time accounting. Read it before touching those.
- `docs/decisions.md` — the design decisions D1–D26 the code refers to. `docs/installer.md`, `docs/guardian.md`
  describe those parts. When a doc and the code disagree, the code wins: fix the doc.

## Verify before saying "done"
- Server: `cd server && pip install -r requirements-dev.txt && ruff check . && pytest -q`
- Client: cannot build on Linux; CI (`.github/workflows/ci.yml`, windows-latest) compiles it
  (path-filtered on PRs; every push to main builds everything and is never cancelled). The client job also runs
  for `server/app/**`: its installer e2e drives the server's API (#104 — a wizard change broke it on main only).
  Push the branch and read the CI result instead of guessing. CI uploads `dist/` of a PR as an artifact (kept
  one day; Re-run jobs brings it back) — that package is what runtime tests on a Windows VM use.
- **One PR with client changes per VM test session.** VM tests are expensive: batch every client change waiting
  for a runtime test into one PR and hand over one test checklist with the CI artifact; the server runs from the
  same PR branch. What the e2e cannot see (Windows 10/11 Home, Polish Windows, the child's session: vigil_ui,
  the lock, the console wizard) waits for that session. Server-only changes go one issue per PR. No stacked PRs:
  every PR targets `main`.
- New server behaviour gets a test. Known bugs are `xfail(strict=True)` — remove the marker when fixed.

## Invariants (changing any of these is a design decision — ask first)
- **Fail-closed**: server unreachable > grace (30 s; 90 s after service start) ⇒ lock. Never fail-open.
- **uvicorn `--workers 1`**: time accounting relies on a single process + `asyncio.Lock` in `get_db()`.
- **Time is server-side**: delta between a device's heartbeats, clamped to `heartbeat_delta_max`, zero after a gap,
  added to its child (D7: time = sum over the child's devices; locks and settings are per child).
  No time accrues while a lock is active (pre-lock check happens before adding the delta).
- Lock priority: `manual > night > limit > pause`. `manual_lock` carries over midnight.
- Weekly schedule (D19): limit and night per weekday (`child_schedule`). A night belongs to the evening of its day
  (`night_start` before 12:00 = after midnight); `lock_logic.night_window` is the only place that resolves it.
  `daily_limit` 0 = no limit that day (`lock_logic.NO_LIMIT`, #106); a day without the PC is the manual lock.
- **DPAPI entropy strings** (`Vigil-SigningKey-v1`, `Vigil-Totp-v1`) and file
  names under `C:\ProgramData\Vigil\` are a compatibility contract with existing installs. They live in
  `client/shared/VigilLayout.cs`. No compatibility with installs from before v0.8 (D23).
- **Threat model**: the child MAY have an administrator account. Against an admin nothing on the PC is
  tamper-proof, so every mechanism is one of: protection (holds even against admin, e.g. server-side state),
  friction (stops a non-elevated process / casual attempt), or detection (reports tampering to the parent panel).
  Say explicitly which one a change is. Secrets on the PC are SYSTEM-only, knowing an admin can still read them.
- **Target**: Windows 10/11 x64, **Home and Pro**. No Pro-only features (gpedit, AppLocker, secpol, BitLocker
  as a requirement); policies only as registry values, which Home honours.
- **One account**: the child's account is the only interactive account on the PC and is an administrator.
  The parent maintains the PC through that same account, so per-user policies (HKCU) hit the parent too.
- Service DACL (`sc sdset`, `VigilLayout.ServiceSddl`) gives admins query/start only — no stop (WP), no delete (SD).
  Stop/delete through a one-off SYSTEM task (`ServiceControl.ScAsSystem`): a normal stop, so the watchdog says goodbye.
  Admin `sc.exe stop`/`Stop-Service` always fail; killing the process skips the goodbye (server: restart alert).

## Gotchas that already cost time
- Claude's cloud session: `git push` works for branches only — no tags, no branch deletion. The maintainer
  merges and creates releases/tags in the GitHub UI (merged branches are deleted automatically); open PRs with
  `gh api` (REST).
- GHCR: a `vigil-server` package created outside the release workflow is not writable by it — the manifest push
  ends with `denied`. Fix: package settings → Manage Actions access → this repository with the Write role,
  then re-run the failed job.
- PowerShell 5.1: `.ps1` must be UTF-8 **with BOM** + CRLF (see `.editorconfig`), otherwise non-ASCII text breaks.
- PowerShell: `$PSScriptRoot` is empty inside `param()` defaults — compute it in the body.
- PowerShell: `"$var:"` parses as a drive-scoped variable — write `"${var}:"`.
- PowerShell: variable names are case-insensitive (a local `$want` overwrites a `[hashtable]$Want` parameter).
- PowerShell 5.1 + `$ErrorActionPreference='Stop'`: `& native.exe 2>&1` turns stderr into a terminating error.
- One account (D12): per-user policies the installer sets in HKCU hit the parent and the installer too; it edits
  hives through the registry API, never `reg.exe`.
- `.ps1` syntax is checked in CI with Windows PowerShell 5.1; locally a Linux `pwsh` can run the same parser.
- C#: never filter with `when (ex is not OperationCanceledException)` — an `HttpClient` timeout is a
  `TaskCanceledException` and slipped past the fail-closed check (#31). Filter on the caller's token:
  `when (!ct.IsCancellationRequested)`.
- XAML `x:Class` must match the code-behind namespace (`Vigil.Ui.*`).
- `vigil_setup` reads only language-independent output (Polish Windows localizes tool output): registry values,
  APIs, task XML, CSV columns by position, exit codes. The SCM keeps its own copy of a service's config until a
  reboot: a `Start` edited in the registry is not what `Get-Service` shows.
- `vigil_setup` is trimmed: no built-in COM (Task Scheduler goes through `schtasks.exe`), no `dynamic`.
- The package (#76) is data appended after vigil_setup.exe's .NET bundle (the host ignores it), not a managed
  resource: the bundle is compressed, and a ~100 MB resource would be unpacked at every guardian start. The
  installer writes only the part before the payload to ProgramData; that copy has no package by design.
- The guardian task runs `C:\ProgramData\Vigil\vigil_setup.exe` (holding it open, up to 75 s on a stopped service)
  and would "repair" a service being replaced: install/update end and delete the task before copying files,
  uninstall before deleting the folder. Anything else that deletes the folder on purpose (the e2e's emergency
  path) disables the task first.
- `vigil_setup` screens: Spectre's SelectionPrompt and live displays need ANSI, and its ReadLine spins forever on
  a null key. Menus and fields go through `Screens/Prompts.cs` (digits work without ANSI too); the CI drives
  them with `-Keys <file>` (`ScriptedConsole`: plain-text output, throws when the script runs out — exit 3).
  Windows 10 conhost (Consolas, no font fallback) has no ✓/✗, braille spinners or rounded borders: `Theme` uses
  √/× and the line spinner unless `WT_SESSION` is set, square borders everywhere.
- PowerShell: an MSBuild property with a dot after a dash in a splatted array (`-p:Version=0.0.0-ci.89`) reached
  dotnet mangled (MSB1001). Pass such arguments as one quoted string: `"-p:Version=$Version"`.
- `client/ui` has `UseWindowsForms` (Screen, NotifyIcon) with the implicit `System.Windows.Forms` using
  removed: it clashes with WPF (`Application`, `KeyEventArgs`). Alias what you need (`using Forms = …`).
- WTS session states: `WTSConnected` is the console at the logon screen with nobody signed in, not a background
  session (that is `WTSDisconnected`). Counting it charged the child for a PC left at the logon screen (#87);
  `SessionDetector` takes `WTSActive` only, and a logon wakes the worker (`VigilServiceLifetime.SessionLoggedOn`).
- The watchdog never uses the PC's wall clock (#92): an admin can set it, a fresh VM may run an hour off. Real time
  (request timestamp, offline TOTP check) = `ServerClock.UtcNow` (PC clock + the server's `X-Server-Time`);
  durations = `ServerClock.MonotonicMs`. A wall-clock duration let a clock set back defeat fail-closed. Only
  `current_time_utc` in the heartbeat is the raw clock, so the server can journal `clock_drift`.
- Session 0 isolation: the service cannot touch user-session windows — anything UI runs via
  `CreateProcessAsUser` (`UiProcessLauncher`).
- vigil_ui's environment (D24) is built by `UiProcessLauncher` from a fixed list, never the child's own
  (`CreateEnvironmentBlock` read `HKCU\Environment`, which a standard user writes: `DOTNET_STARTUP_HOOKS`, a
  profiler, a test-mode switch), with `DOTNET_EnableDiagnostics=0`. No project extracts native libraries at
  run time (a planted DLL in the child's `%TEMP%` would load): vigil_ui's sit next to it, listed in
  `VigilLayout.UiNativeLibraries` and `build-package.ps1`, which fails when the publish output differs.
  `--standalone-test` works only from the command line.
- Windows logs the session off (closing vigil_ui) before it stops services, also on shutdown, and the service
  hears of it only from the SCM (`VigilServiceLifetime.OnSessionChange`/`OnShutdown`), maybe after vigil_ui is
  gone. So `UiManager` respawns an exit at once but judges it as a kill only 10 s later (restarts gave
  "exited before connecting"; locked, 1 s ticks could reach 3 exits = a false `lockscreen_killed`).
- vigil_ui runs with a process DACL that denies the user `PROCESS_TERMINATE`/`WRITE_DAC`: never open the
  own process by PID inside it (e.g. `Process.GetCurrentProcess().MainModule`) — use pseudo-handles or
  `GetModuleHandle(null)`.

## Conventions
- Code, comments, logs, commit messages: English.
- i18n (D21): each UI speaks its environment's language (Polish or English); the server and the watchdog send
  codes and numbers, never sentences. Panel: Polish source text marked `t('…')` / `tn(n, '…')` / `data-i18n`,
  English in `server/app/static/i18n.js` — `tests/test_i18n.py` fails on a missing or unused entry.
  `vigil_setup` speaks Windows' display language (`CurrentUICulture`), every text in `client/setup/Strings.cs`.
- Versions: one for every client binary, `build-package.ps1 -Version` (release: the tag without "v"; CI:
  `0.0.0-ci.<run>`; local: `0.0.0-dev`).
- Server log (`app/logs.py`, #100): the server's own state and its errors only — start/stop, migrations,
  warnings, unhandled errors with the request. What happens on the PCs goes to `events` (the panel journal), never
  to the log; HTTP requests are not logged (uvicorn's access log is off, its INFO lines replaced by the
  lifespan's). A condition that repeats (healthcheck every 30 s) is logged when it starts and when it ends.
- Python: plain `sqlite3` via `get_db()`; request bodies are Pydantic models (`app/models.py` for `/v1`, strict;
  panel bodies next to their endpoints); config through `get_config_value()`/`get_config_values()`, defaults in
  `config.CONFIG_DEFAULTS`, no caches. Schema changes = a new entry at the end of `migrations.MIGRATIONS`
  (never edit an applied one; migration 1 is the v0.8 baseline, D26) + a test that migrates an older database.
- C#: async I/O, `ILogger<T>`, P/Invoke in `Native/`.
- PowerShell: `Write-Step/OK/Warn/Skip` helpers, `$ErrorActionPreference = 'Stop'`.
- One issue = one branch = one PR, except client changes batched for one VM session (see above).
  Update this file in the same PR when an invariant or gotcha changes.
- User-visible changes get a line under `## [Unreleased]` in `CHANGELOG.md` in the same PR; at a release the
  maintainer moves it under the version, and the release notes are written from that section.
- `README.md` and `README.pl.md` carry the same content; change both. The "How it works" diagram is hand-written
  SVG, `docs/images/how-it-works.svg` and `how-it-works.pl.svg` (Mermaid was scaled to ~9 px text in the README
  column): change both when the architecture changes; keep them 900 px wide with text of 13 px or more.
  `CONTRIBUTING.md` is the human version of this file's rules — keep them in step. Nothing from the maintainer's environment (addresses, host names, paths,
  test data) goes into the repo, issues, PRs or release notes.
