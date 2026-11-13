# Changelog

User-visible changes, written for the parent who runs Vigil. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow the release tags; each version's
section is also its release notes, written to the template in [`docs/releasing.md`](docs/releasing.md). The
reasoning behind each design decision (D1–D27) is in [`docs/decisions.md`](docs/decisions.md).

## [Unreleased]

### Documentation
- README rewritten for people trying Vigil (English and Polish): a logo, captioned screenshots, a short feature
  list, the quick start near the top, and the threat model as a table. The full threat model moved to
  `docs/threat-model.md`.
- Release notes come straight from this file, in one template ([`docs/releasing.md`](docs/releasing.md)).

## [0.9.1] - 2026-11-13

Small fixes in the parent panel: the header, typing night times on a phone, and pauses in the Journal.

| | Update |
|---|---|
| Server | **Yes.** New image; the database does not change. |
| Children's PCs | **Not needed.** The client did not change; the 0.9.1 `vigil_setup.exe` differs only in its version number. |

### Panel
- The header shows Online or Offline for the child on screen. It used to count the computers of every child and
  show the offline time in minutes ("Offline (3421 min)"); which computer is offline, and since when, is in the
  Computers card.
- Night times in the weekly schedule (panel and setup wizard) are typed as digits and the ":" goes in by itself,
  as a phone's number keyboard has none: "2130" gives 21:30, "630" gives 06:30. The limit field takes at most four
  digits.
- A dialog (e.g. "Add computer") or the child menu no longer closes when you select text in a field and let go of
  the mouse outside it.
- Journal: a pause shows its time span ("16:02–16:14", or "under a minute") instead of "time was not counted", and
  durations of a day or more read in days and hours ("2 d 3 h").

## [0.9.0] - 2026-11-12

A new look for the panel, the lock screen and the installer, a weekly schedule that is easier to fill in, more
reliable locking at start-up, and the client on .NET 10.

| | Update |
|---|---|
| Server | **Yes, first.** The database is updated at start: back up `data/` before. |
| Children's PCs | **Yes.** Run the new `vigil_setup.exe` → **Update**. No new pairing; a 0.8 client keeps working with the 0.9 server until then. |

**Changed:** a daily limit of 0 now means no limit that day (until now: the whole day locked). To keep a child off
the PC for a day, use the manual lock.

### Panel
- One color scheme for the panel, the setup wizard and the login: a turquoise accent and one muted color per
  state (unlocked green, night violet, manual lock red, time limit graphite, pause grey). Alerts and deleting stay
  a strong red.
- Status tab: today's limit and night time are two tags ("Limit 2 h + 30 min", "21:30–06:30", "Moved to 22:30").
  Actions are grouped under "Time today" and "Move night time by" (30, 60 or 120 min); the manual lock is a switch.
- Settings tab: one card per child with its name, computers, "Add computer" and "Delete child". Children are picked
  and added from the menu at the name in the header.
- Weekly schedule: "Default week" and "Every day like Monday" fill the table, the limit field takes digits only (it
  used to save `1e3` as 1000) and a wrong field is marked. The setup wizard sets the first child's week in the same
  table.
- The Journal shows when a computer was shut down, asleep or woken up, also with Fast Startup (the Windows
  default), and its icons take the color of what an entry is about.
- Codes are grouped as you type and read them: the pairing code as 1234 5678, the TOTP code as 123 456. "Show TOTP
  code" shows the key too. The wizard explains a wrong TOTP code or an unknown time zone in words.

### Lock screen and tray
- A new look matching the panel: black, a round badge with the state's icon and color, and one line saying what
  happens next — at night when the computer unlocks, during a pause how much time is left today.
- With display scaling above 100 % the lock screen was larger than the screen and "Shut down" was off the edge; it
  now covers each monitor exactly. "Shut down" asks on the lock screen itself.
- Alt+Space no longer opens the lock screen's window menu, and another program can no longer close, move or hide it
  for more than a moment.
- A computer that joins Wi-Fi only after logon briefly showed "No connection to your parent's server" at every
  start. It now says the computer is not connected to a network, and unlocks as soon as it is. A clock a few
  seconds ahead of the server no longer flashes the lock at start.
- A computer removed or re-paired in the panel says so on its lock screen. If the Vigil service stops while the
  computer is locked, the lock stays until the service is back.
- The lock screen's clock shows the server's time, the child sees no technical errors, and the Polish texts no
  longer say "watchdog" or assume a boy.
- Tray: a turquoise icon, a tooltip saying that a click starts a pause, and "No limit" on a day without one.

### Installer
- A turquoise accent, clearer Polish texts, the pairing code typed as 1234 5678, and a hint pointing to Settings →
  Add computer.
- Behind a reverse proxy that redirects to HTTPS, the installer says which address to use instead of failing with
  "HTTP 405". Vigil no longer follows redirects or uses the system proxy.

### Server
- Saving a change in the panel while a computer sent its heartbeat could stop the server for 30 seconds ("database
  is locked" in the log).
- The container log shows the server itself: start and stop (version, port, database), migrations, warnings, and
  errors with the request they broke — no line for every request. `NO_COLOR=1` turns the colors off.
- A damaged password hash refuses the login instead of failing with a server error.

### Security and maintenance
- The client runs on .NET 10, supported until November 2028; .NET 8 gets no security fixes after 10 November 2026.
- The computer's key and the offline TOTP secret are written with their restricted permissions from the start.
- The Windows Event Log gets one warning when the server has not answered for 30 seconds and one entry when it
  answers again, instead of a warning for every failed attempt.

## [0.8.2] - 2026-11-10

Fixes "Shut down" on the lock screen, which did nothing since 0.8.0.

| | Update |
|---|---|
| Server | **Not needed.** No changes; pulling `:0.8.2` is optional. |
| Children's PCs | **Yes.** Run the new `vigil_setup.exe` → **Update**. The pairing and the settings stay. |

### Lock screen
- "Shut down" works again. If Windows refuses to shut down, the lock screen shows the reason.

### Documentation
- README: the "How it works" diagram is a picture readable at the page's width, and the third screenshot shows the
  whole Settings tab.

## [0.8.1] - 2026-11-10

> **Withdrawn:** "Shut down" on the lock screen did nothing in this version (fixed in 0.8.2). Its release and
> server image were removed; install the latest release instead.

### Server
- Logging out of the panel, or changing the password, ends every panel session — also a copy of the cookie
  left in the browser of the child's PC. Logging out on one device logs out the others.
- Time is counted to the millisecond across heartbeats: about 1–2 % of the time used was not counted before.
- "I'm back" on the pause screen can no longer start the pause again on a double click.
- An offline unlock reported by a PC can no longer block the parent's TOTP codes for good; the same code used
  offline on two PCs shows as two unlocks in the Journal.
- The setup wizard no longer asks for the server address (the installer always used the address typed in it),
  and reports invalid fields instead of failing with a server error.
- Dependencies updated (FastAPI, Starlette, python-multipart, uvicorn): known vulnerabilities fixed.
- The panel no longer asks for a deleted child at every refresh (a 404 in the server log) and forgets a Journal
  filter for it.
- Settings → Children: the dot next to a child is green when any of its PCs is online and red otherwise (it showed
  the lock state, so it was green almost always).

### Client
- A PC sitting at the Windows logon screen no longer counts as used: before, the child's time ran there too.
- After a logon, vigil_ui and any lock start at once instead of after up to a minute.
- A closed lock screen comes back within a second (was five).
- The guardian also puts back the service's program path and undoes a delayed start (alert in the Journal).
- An install or update unpacks every file before it stops anything: a failure there (antivirus, disk space)
  leaves the installed Vigil as it was.
- A PC whose clock is off no longer stays locked with "server unreachable" for good: the watchdog uses the
  server's time, and the Journal shows the PC's clock drift.
- Setting the PC clock back no longer stops the lock when the server is unreachable, nor extends an offline
  unlock.

### Documentation
- README rewritten (English and Polish): how it works, a quick start from the release, updating.
- The server's plain HTTP is named as a known limit in the README threat model and in SECURITY.md:
  the pairing response carries the PC's key and the panel login its password; use a reverse proxy with TLS.
- CONTRIBUTING.md, issue forms and a pull request template.
- README threat model: a deleted or damaged configuration on the PC stops the service, which the panel shows only
  as the PC being offline.

## [0.8.0] - 2026-11-09

> **Withdrawn:** "Shut down" on the lock screen did nothing in this version (fixed in 0.8.2). Its release and
> server image were removed; install the latest release instead.

The first public release.

### Server
- One server for several children, each with any number of PCs; a child's time is the sum over its PCs (D7, D18).
- Weekly schedule: a daily limit and night time for each day of the week; a night belongs to the evening of its day (D19).
- Parent panel: status, extra time, manual lock, pairing PCs with a one-time code, a Journal with alerts to confirm.
- Signed requests, TOTP codes usable once with a short lockout, a limit on panel logins.
- Panel and setup wizard in English or Polish, following the browser (D21).
- Image `ghcr.io/deresterr/vigil-server` (amd64).

### Client
- The whole client in one file, `vigil_setup.exe`: a console wizard, a status menu (check, update, change the
  server address, re-pair, uninstall) and a parameter mode for scripts (D22).
- Fail-closed lock on every monitor, a tray icon with a pause, TOTP unlock on the lock screen — also without
  the server (D4).
- Tamper detection reported to the Journal: a stopped or frozen service, a lock screen closed repeatedly,
  every repair by the guardian (D15).
- Lock screen and installer in English or Polish, following Windows (D21).
- No upgrade from builds before 0.8 (D23); the server's database starts from the 0.8 schema (D26).

[Unreleased]: https://github.com/deresterr/vigil/compare/v0.9.1...HEAD
[0.9.1]: https://github.com/deresterr/vigil/releases/tag/v0.9.1
[0.9.0]: https://github.com/deresterr/vigil/releases/tag/v0.9.0
[0.8.2]: https://github.com/deresterr/vigil/releases/tag/v0.8.2
[0.8.1]: https://github.com/deresterr/vigil/tree/v0.8.1
[0.8.0]: https://github.com/deresterr/vigil/tree/v0.8.0
