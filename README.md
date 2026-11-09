# Vigil

**English** · [Polski](README.pl.md)

Self-hosted screen-time control for a child's Windows 10/11 PC.
A server in Docker on your home network counts the time and decides when to lock; a service on the child's PC
shows a full-screen lock. The parent manages everything from a browser and unlocks with a TOTP code from their phone.

> **Status: v0.7, before 1.0.** In daily use. A child with administrator rights can get around Vigil — Vigil
> makes that harder and shows it to the parent, it does not make it impossible (see [Threat model](#threat-model)).
> Roadmap and decisions (in Polish): [`docs/review-2026-11.md`](docs/review-2026-11.md).

## What it does

- several children on one server, each with any number of PCs (time counted across all of them),
- a weekly schedule: daily time limit and night time for each day of the week and each child,
- manual lock from the panel, extra time from the panel or with a TOTP code typed on the lock screen,
- unlocking with the phone code also works when the server is unreachable,
- a pause the child starts (time is not counted, the screen is locked),
- fail-closed: no connection to the server = locked,
- a journal in the parent panel with alerts about attempts to get around it,
- panel, lock screen and installer in English or Polish, following the browser and Windows language.

## Threat model

We assume the child **may have an administrator account** — that is how most families use their PCs.
An administrator can run code as SYSTEM, read any file and disable any service, so nothing on the PC itself is
tamper-proof. Every mechanism in Vigil belongs to one of three groups:

**Protects** — holds even against an administrator, because the state lives on the server:
- limits, night time, the manual lock and the time counter are on the server; the PC cannot change them,
  at most feed it false data (see "Does not detect"),
- PCs are paired with a one-time code from the panel; every pairing issues a new key and the previous PC
  loses access,
- a TOTP code works only once; after wrong codes unlocking pauses for minutes, so the child cannot lock
  the parent out for long,
- a limit on login attempts to the parent panel.

**Impedes** — stops a process without administrator rights and casual attempts; an administrator gets past it:
- requests to the server are signed with a key in a file readable only by SYSTEM and administrators,
- no connection to the server = locked (fail-closed),
- a service an administrator cannot stop with a plain command; a guardian undoes disabling the service,
  replacing its file and other changes to the installation every 5 minutes; the service also runs in Safe Mode,
- the lock screen covers every monitor, shortcuts are blocked (Win, Alt+Tab, Alt+F4), Task Manager and
  changing the account password are disabled — these two policies apply to the parent too, as it is the same account,
- Vigil's process in the child's session cannot be closed without administrator rights; closing it
  repeatedly (e.g. from an elevated PowerShell) logs the user off,
- offline unlocking with the TOTP code: the secret is on the PC's disk and an administrator can read it.

**Detects** — shows an alert to confirm in the Journal tab:
- the Vigil service stopped, Vigil restarted without a system restart,
- Vigil frozen or suspended while the computer was running,
- the lock screen closed repeatedly,
- every repair by the guardian (e.g. a disabled service, a replaced file),
- runs of wrong TOTP codes, failed panel logins, an invalidated pairing code.

Offline unlocks and periods without a connection show in the Journal as ordinary entries.

**Does not detect:** a service stopped for good (the panel only shows the PC as offline), another system booted
from a USB stick (offline as well), or forged requests sent by an administrator who read the key from the disk.

## Requirements

- server: any Linux with Docker on the same network as the child's PC (or reachable over a VPN),
- child's PC: Windows 10/11 x64, Home or Pro; the child's account is the only user account
  and may have administrator rights (the parent maintains the PC from that same account),
- the parent's phone with a TOTP app (Aegis, Google Authenticator, …),
- to build the client: Windows with the .NET 8 SDK (or a ready `vigil_setup.exe` from a release or GitHub Actions).

## Quick start

Every release (the **Releases** tab) has both parts ready: the server image
`ghcr.io/deresterr/vigil-server:<version>` and `vigil_setup.exe`, the whole client for the child's PC in one file.
With them you build nothing: in step 1 set `VIGIL_IMAGE=ghcr.io/deresterr/vigil-server:<version>` in `server/.env`
and run `docker compose pull && docker compose up -d`; the downloaded `vigil_setup.exe` replaces step 2.

**1. Server**

```bash
cd server
cp .env.example .env          # instance name, port, optionally a ready image
docker compose up -d
```

Open `http://<server-ip>:8088/setup` and go through the wizard (parent password, TOTP, limits, server address).
Set the limit and night time for each day of the week later in the panel (Settings → Weekly schedule).
The wizard is open without a login until it is completed — do it right after starting the server.

**2. Package for the child's PC** (on Windows, in the repo folder)

You need the .NET 8 SDK — only on the machine that builds; the package is self-contained, the child's PC does
not need it:

```
winget install Microsoft.DotNet.SDK.8
```

Then (in a new window, so that `dotnet` is on the PATH):

```
Build.cmd            → dist\vigil_setup.exe
```

`vigil_setup.exe` is the whole client: the installer carries the service and the lock screen. Instead of building
locally you can download it from the `vigil-client-…` artifact of the latest CI run (Actions tab).

**3. Install on the child's PC** — copy `vigil_setup.exe` to the child's PC and run it (it asks for
administrator rights; a downloaded copy may first get SmartScreen's "Windows protected your PC": More info →
Run anyway). The wizard asks for the server address and a pairing code (parent panel: Settings →
Child's computers → "Add computer") and finds the child's account itself. Run it again later for the menu:
check, update, change the server address, re-pair, uninstall.

```
vigil_setup.exe                  wizard, or the status and menu when installed (menu: Uninstall)
vigil_setup.exe -Mode verify     checks the installation (or, uninstalled, that nothing was left)
vigil_setup.exe -Mode uninstall  uninstalls without the menu
```

Parameters for scripts and every step of an install: [`docs/installer.md`](docs/installer.md).

**If Windows does not start or Vigil cannot be removed normally:** boot Windows Recovery → Troubleshoot →
Command Prompt, run `rmdir /s /q D:\ProgramData\Vigil` (the Windows drive is often `D:` there; check with
`dir D:\ProgramData`), restart normally, then uninstall (menu → Uninstall) and run `vigil_setup.exe -Mode verify`. Without its
files Vigil does not start, so there is no lock. For an ordinary lock the parent's TOTP code is enough.

## Repository layout

| Folder | Contents |
|---|---|
| `server/` | FastAPI + SQLite, parent panel, tests |
| `client/` | Windows service (`watchdog`), lock screen with a tray icon (`ui`), installer (`setup`) |
| `client/setup/` | the installer `vigil_setup.exe`: wizard, menu, verify, integrity guardian |
| `installer/` | the old PowerShell installer, used only by the CI test as an installation from before the C# one |
| `e2e/` | installer end-to-end test (CI, throwaway Windows) |
| `docs/` | architecture, review and roadmap |

## Security

Report vulnerabilities privately — how, and what falls within the threat model: [`SECURITY.md`](SECURITY.md).

## License

MIT — see [`LICENSE`](LICENSE).
