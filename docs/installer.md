# vigil_setup.exe — the installer

One program (`client/setup`, D22): the wizard and the menu for the parent, the parameter mode for scripts
and CI, `verify`, and the guardian task. .NET 8, self-contained, trimmed single file (~12 MB), console,
`requireAdministrator` (UAC). It speaks Windows' display language (Polish or English, `Strings.cs`).

## The package: one file

The client is distributed as `vigil_setup.exe` alone (#76): `build-package.ps1` appends `vigil_watchdog.exe`,
`vigil_ui.exe` and its native WPF libraries (D24), gzipped, after the .NET bundle of `vigil_setup.exe` (the host finds its bundle through an
offset in its header and ignores what follows), with a trailer. Format and the only reader:
`Core/Package.cs`. Each file carries its SHA-256: a damaged download fails before anything is replaced.

The installer unpacks them into `C:\ProgramData\Vigil` and writes there only the first part of itself, without the payload: the guardian task starts a ~12 MB program every 5 minutes. That copy (and
a cut-off download) has no package: install and update refuse, verify and uninstall work.

`Core/` does the work and never touches the console; it reports through `IReport`. `Screens/` (Spectre.Console)
and the parameter mode (`Program.cs`) are two front ends on the same calls.

## Running it

| | |
|---|---|
| `vigil_setup.exe` (double click) | nothing installed → **wizard**; installed → **status and menu** |
| `vigil_setup.exe -Mode …` | parameter mode (below); exit code 0 done, 1 failed (verify: a failure), 2 wrong parameters |
| `vigil_setup.exe guardian` | the guardian task only (`docs/guardian.md`) |

Without parameters and with stdin/stdout redirected it prints the usage instead of waiting for keys. Every
run writes a log to `%TEMP%\vigil_setup_<time>.log` (its path is on screen) — the thing to attach to a report.

### Wizard

Server address (checked against `/health`, asked again until it answers) → pairing code (8 digits; the
panel: Settings → Child's computers → Add computer) → the child's account (only when there are several) →
summary → install → a short verify table → the menu. **Nothing changes on the PC before "Install" on the
summary**: the code goes to the server only then, and a wrong or expired one asks again with the system untouched.

### Menu

Header: installed version vs this package, server address and whether it answers, service state, the
computer's key, the child's account. Items:

- **Check the installation** — verify, every check.
- **Update to X / Repair / Install the older version** — the package's files over the installation (the
  label follows the versions); address and key kept. Without a key it asks for a pairing code first.
- **Change the server address** — the same server at a new address; the key stays.
- **Re-pair** — a new key from a code (panel: Re-pair next to the computer).
- **Uninstall** — after a confirmation; the parent panel's Journal shows the service stop.

Arrow keys and Enter, or the item's digit; works in a legacy console too (digits only).

## Parameter mode

```
-Mode install       -ServerUrl <address> -PairingCode <code> [-ChildUsername <account>] [-DisableWinRE] [-NoUserPolicies]
-Mode update        [-ServerUrl <address>]            files from this package; vigil.toml, signing.dat, totp.dat kept
-Mode update-config [-ServerUrl <address>] [-PairingCode <code>]
-Mode reinstall     -ServerUrl <address> -PairingCode <code>
-Mode uninstall
-Mode verify        [-ChildUsername <account>]        changes nothing
```

The address may be given without a scheme or port (`192.168.1.5` → `http://192.168.1.5:8088`). The child's
account: the only interactive account with a profile (D12); with several, `-ChildUsername`, otherwise the
per-user policies go to the Default profile only. Pairing always happens before anything on the PC changes.

## What an install does

1. **Unpacking**: every file of the package to `<name>.new` next to its target in `C:\ProgramData\Vigil`,
   checked against its SHA-256 — before anything is stopped, so a failure here (antivirus, disk space)
   leaves the installation as it was.
2. **Stopping**: the service stopped as SYSTEM through a one-off task (a normal stop: the watchdog says
   goodbye) and deleted; vigil_ui killed; the guardian task ended and deleted (it runs the copy of
   `vigil_setup.exe` being replaced).
3. **Files** moved into place: `vigil_watchdog.exe`, `vigil_ui.exe` and its native WPF libraries
   (`VigilLayout.UiNativeLibraries`, D24), `vigil_setup.exe` (without the payload), `backup\vigil_watchdog.exe`.
4. **Configuration**: `guardian.dat` (SHA-256 of the watchdog, DPAPI), `vigil.toml` (server address only),
   `signing.dat` (the key, DPAPI with entropy) — kept on `update`, like `totp.dat`.
5. **Service** `vigil_watchdog`: automatic, quoted path, recovery restart ×3 after 60 s (also on a non-zero
   exit), DACL: administrators query/start only.
6. **Protection**: SafeBoot Minimal/Network, the task "Vigil Guardian" (`vigil_setup.exe guardian`, SYSTEM,
   startup + every 5 min), F8 menu off (`bootmenupolicy Standard`), optionally WinRE off (`-DisableWinRE`).
7. **Child's account**: the per-user policies (`VigilLayout.UserPolicies`) in the child's hive and the
   Default profile — live, or loaded as an app hive; never through `reg.exe` (D12: it may be blocked).
8. **Permissions**: folder SYSTEM full / Administrators modify / Authenticated Users read+execute,
   secrets SYSTEM full / Administrators read.
9. **Start** and a check that it runs.

**Uninstall** reverses it best effort — every step runs even when one fails, also on a half-removed
installation — and removes the Event Log sources. The F8 policy is left at Windows' default.

## Verify

Reads only language-independent sources (registry, APIs, task XML, CSV columns by position, exit codes).
Installed: service (state, start, path, recovery, DACL), files (complete, no leftovers, backup and
`guardian.dat` match, binaries = this package, secrets decrypt, ACLs), tasks and boot (guardian task, no
temporary tasks, SafeBoot, F8), the child's policies, runtime (vigil_ui in the session, the server answers,
watchdog warnings in the last 15 minutes). Not installed: nothing left (service, folder, tasks, SafeBoot,
processes, policies, Event Log sources).

## Emergency: Windows does not start or Vigil cannot be removed normally

The lock is fail-closed, and the parent unlocks offline with the TOTP code (D4). If the PC itself must be
freed from outside Windows:

1. Boot Windows Recovery (WinRE: Shift + Restart, or three failed boots) → Troubleshoot → Command Prompt.
2. Find the Windows drive (in WinRE it is often `D:`): `dir D:\ProgramData`.
3. `rmdir /s /q D:\ProgramData\Vigil` — without its files the watchdog and the guardian cannot start, and
   without the watchdog there is no lock.
4. Restart normally, then uninstall (the menu or `-Mode uninstall`) and check with `vigil_setup.exe -Mode verify`.

Threat model: nothing new — an administrator can do the same from Windows; the panel only sees the PC offline.

## Tests

`e2e/installer-e2e.ps1` runs in CI on a throwaway `windows-latest` runner against the server of the same
commit: install → `update` → the one-file package (installed files = `build\parts`, the installed copy and a
payload-less one refuse to update) →
broken installs → re-pair → uninstall → install →
six guardian repairs reported to the journal → the emergency path above → uninstall, then the wizard and
the menu through a key script (`-Keys <file>`, not in the usage). What CI cannot see — Windows 10/11 Home,
Polish Windows, the child's session, the console look — is tested on a VM.
