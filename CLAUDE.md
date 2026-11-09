# Vigil — notes for Claude

Self-hosted parental screen-time control for Windows. Owner: Mateusz. **Talk to him in Polish**;
he knows the stack deeply — be direct, skip basics, fix root causes, apply a fix everywhere it applies.

## Layout
- `server/` — FastAPI + SQLite, one server for many children and their PCs (D7). Entry `app/main.py`,
  time/lock rules `app/lock_logic.py` + `app/endpoints/heartbeat.py`. Admin UI = static HTML in `app/static/`.
- `client/` — .NET 8 (`Vigil.sln`): `watchdog` (Windows Service, SYSTEM), `ui` (WPF `vigil_ui`: tray + lock overlay,
  child's session, started and kept alive by the watchdog), `setup` (`vigil_setup.exe`, the C# installer of D22:
  console, trimmed, `requireAdministrator`; core in `Core/` never touches the console, `IReport` gets the output;
  without parameters `Screens/` shows the wizard or the status + menu on Spectre.Console, with `-Mode ...` the
  parameter mode for scripts and CI — both only call the core; `vigil_setup.exe guardian` is the guardian task,
  run as SYSTEM from the copy the installer puts in `C:\ProgramData\Vigil`, see `docs/guardian.md`),
  `shared/` linked into the projects that need it: `IpcMessages.cs` (watchdog + ui), `VigilLayout.cs` (paths,
  file names, entropy, service, SDDL, guardian event IDs, per-user policy table) and `FileAcls.cs` (watchdog + setup).
- `installer/` — `Install.cmd` (shortcut to `vigil_setup.exe`, same parameters), `vigil_install.ps1` +
  `vigil_guardian.ps1` (the old installer and guardian: only the e2e's "install from before D22", not in the
  package), `vigil-cleanup.ps1` + `Cleanup.cmd` (until #66). `build-package.ps1 -Version` → `dist/`.
- `e2e/installer-e2e.ps1` — installer end-to-end test: CI runs it on the throwaway `windows-latest` runner
  (server of the same commit on localhost; PowerShell install → vigil_setup update → broken installs → re-pair →
  uninstall → install → six guardian repairs reported to the journal → folder deleted "from WinRE" → uninstall,
  then the wizard and the menu through a key script). Never on a real PC.
- `docs/architecture.md` — protocol, IPC, file paths, time accounting. Read it before touching those.
- `docs/review-2026-11.md` — known defects, decisions (D1–D22), roadmap (M1–M2 done, M3, M4.1).
  Other `docs/*.md` are older component notes; code wins.

## Verify before saying "done"
- Server: `cd server && pip install -r requirements-dev.txt && ruff check . && pytest -q`
- Client: cannot build on Linux; CI (`.github/workflows/ci.yml`, windows-latest) compiles it
  (path-filtered on PRs; every push to main builds everything and is never cancelled).
  Push the branch and read the CI result instead of guessing. CI uploads `dist/` as an artifact —
  that package is what Mateusz runs on the Windows VM for runtime tests.
- **One PR with client changes per VM test session.** VM tests are expensive for Mateusz: batch every client
  change waiting for a runtime test into one PR (merge the issue branches into it, as #29 did for #9 + #10)
  and hand over one test checklist with the CI artifact. He runs the server from the same PR branch in the
  test stack (`docs/test-server.md`). Production (v0.4, container `vigil`, image `vigil:local`, port 8088) runs on
  the same host: test instructions must never reuse its container or image name.
  Server-only changes still go one issue per PR. No stacked PRs: every PR targets `main`.
- **Installer rewrite in C# (D22, #62–#66)** is the exception: each step is its own PR and merges once the CI
  installer e2e is green; VM sessions only at the checkpoints (after #64 and after #66). Anything the e2e
  cannot see (Windows 10/11 Home, Polish Windows, the child's session: vigil_ui, lock, the console wizard)
  waits for the next checkpoint. The old `vigil_install.ps1 -Mode verify` was the oracle until the C# one matched
  it at every e2e point (#63, #64); since #65 (C# guardian) the C# verify is the reference and the PowerShell one
  checks only the e2e's PowerShell install.
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
- **DPAPI entropy strings** (`Vigil-SigningKey-v1`, `Vigil-Totp-v1`) and file
  names under `C:\ProgramData\Vigil\` are a compatibility contract with existing installs. They live in
  `client/shared/VigilLayout.cs` (the PowerShell installer still has its own copy until #66).
- **Threat model**: the child MAY have an administrator account. Against an admin nothing on the PC is
  tamper-proof, so every mechanism is one of: protection (holds even against admin, e.g. server-side state),
  friction (stops a non-elevated process / casual attempt), or detection (reports tampering to the parent panel).
  Say explicitly which one a change is. Secrets on the PC are SYSTEM-only, knowing an admin can still read them.
- **Target**: Windows 10/11 x64, **Home and Pro**. No Pro-only features (gpedit, AppLocker, secpol, BitLocker
  as a requirement); policies only as registry values, which Home honours.
- **One account**: the child's account is the only interactive account on the PC and is an administrator.
  The parent maintains the PC through that same account, so per-user policies (HKCU) hit the parent too.
- Service DACL (`sc sdset`, `$ServiceSddl`) gives admins query/start only — no stop (WP), no delete (SD).
  Stop/delete through a one-off SYSTEM task (`Invoke-ScAsSystem`): a normal stop, so the watchdog says goodbye.
  Admin `sc.exe stop`/`Stop-Service` always fail; killing the process skips the goodbye (server: restart alert).

## Gotchas that already cost time
- Claude's cloud session: `git push` works for branches only — no tags, no branch deletion. Mateusz creates
  releases/tags in the GitHub UI (merged branches are deleted automatically); open PRs with `gh api` (REST).
- PowerShell 5.1: `.ps1` must be UTF-8 **with BOM** + CRLF (see `.editorconfig`), otherwise Polish text breaks.
- PowerShell: `$PSScriptRoot` is empty inside `param()` defaults — compute it in the body.
- PowerShell: `"$var:"` parses as a drive-scoped variable — write `"${var}:"`.
- PowerShell 5.1: embedded quotes in native arguments are lost (`sc.exe create binPath= "`"$exe`""` registered an
  unquoted path). Use the cmdlet/API (`New-Service`) or `\"` escaping. Variable names are case-insensitive:
  a local `$want` overwrites a `[hashtable]$Want` parameter.
- PowerShell/icacls: use SIDs (`*S-1-5-32-544`), never group names — Polish Windows localizes them.
- PowerShell 5.1 + `$ErrorActionPreference='Stop'`: `& native.exe 2>&1` turns stderr into a terminating error
  (fatal in `finally`). Call `reg.exe` only via `Invoke-RegExe` in `vigil_install.ps1`.
- One account (D12): policies the installer sets in HKCU (e.g. `DisableRegistryTools` → blocks `reg.exe`) also
  hit the installer itself on uninstall/reinstall. Edit user hives via the PowerShell registry provider first.
- `.ps1` syntax is checked in CI with Windows PowerShell 5.1; locally a Linux `pwsh` can run the same parser.
- C#: never filter with `when (ex is not OperationCanceledException)` — an `HttpClient` timeout is a
  `TaskCanceledException` and slipped past the fail-closed check (#31). Filter on the caller's token:
  `when (!ct.IsCancellationRequested)`.
- XAML `x:Class` must match the code-behind namespace (`Vigil.Ui.*`).
- `vigil_setup` reads only language-independent output (Polish Windows localizes tool output): registry values,
  APIs, task XML, CSV columns by position, exit codes. The SCM keeps its own copy of a service's config until a
  reboot: a `Start` edited in the registry is not what `Get-Service` shows.
- `vigil_setup` is trimmed: no built-in COM (Task Scheduler goes through `schtasks.exe`), no `dynamic`.
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
- Session 0 isolation: the service cannot touch user-session windows — anything UI runs via
  `CreateProcessAsUser` (`UiProcessLauncher`), with the user's `CreateEnvironmentBlock`: without it the process
  inherits SYSTEM's environment (#72: no ui.log, single-file extraction to `C:\Windows\Temp`).
- vigil_ui runs with a process DACL that denies the user `PROCESS_TERMINATE`/`WRITE_DAC`: never open the
  own process by PID inside it (e.g. `Process.GetCurrentProcess().MainModule`) — use pseudo-handles or
  `GetModuleHandle(null)`.

## Conventions
- New code, comments, commit messages: English. Existing Polish comments: leave unless touching the code.
- i18n (D21): each UI speaks its environment's language (Polish or English); the server and the watchdog send
  codes and numbers, never sentences. Panel: Polish source text marked `t('…')` / `tn(n, '…')` / `data-i18n`,
  English in `server/app/static/i18n.js` — `tests/test_i18n.py` fails on a missing or unused entry.
  `vigil_setup` speaks Windows' display language (`CurrentUICulture`), every text in `client/setup/Strings.cs`;
  the remaining PowerShell scripts stay Polish until they are removed (#65, #66).
- Versions: one for every client binary, `build-package.ps1 -Version` (release: the tag without "v"; CI:
  `0.0.0-ci.<run>`; local: `0.0.0-dev`). Binaries of v0.7 and older carry the SDK default 1.0.0 = "before v0.8".
- Python: plain `sqlite3` via `get_db()`; request bodies are Pydantic models (`app/models.py` for `/v1`, strict;
  panel bodies next to their endpoints); config through `get_config_value()`/`get_config_values()`, defaults in
  `config.CONFIG_DEFAULTS`, no caches. Schema changes = a new entry at the end of `migrations.MIGRATIONS`
  (never edit an applied one) + a test that migrates an older database.
- C#: async I/O, `ILogger<T>`, P/Invoke in `Native/`.
- PowerShell: `Write-Step/OK/Warn/Skip` helpers, `$ErrorActionPreference = 'Stop'`.
- One issue = one branch = one PR, except client changes batched for one VM session (see above).
  Update this file in the same PR when an invariant or gotcha changes.
