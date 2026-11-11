# Changelog

User-visible changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
the release tags. The reasoning behind each design decision (D1–D26) is in [`docs/decisions.md`](docs/decisions.md).

## [Unreleased]

### Server
- Settings tab: the child's name, computers and "Delete child" are one card titled with the name ("Rename" opens
  a dialog). The "Children" card is gone: children are picked and added from the menu at the name in the header,
  which now opens with one child too. Shorter TOTP description.
- Status tab, Actions: the buttons are grouped under "Time today" and "Move night time by", which now offers
  30, 60 or 120 min instead of one "Night time +60 min" button. The manual lock is a switch that turns red while
  it is on.
- Status tab: today's limit and night time are two tags under the minutes used and left, instead of one line
  joined with "•". The limit includes extra time ("Limit 2 h + 30 min"); the night tag shows the hours, the end
  while it runs, or how long it is extended. The "summed over all computers" note is gone.
- **A daily limit of 0 now means no limit that day** (until now: the whole day locked). The panel shows
  "No limit", and the extra-time buttons are off on such a day. To keep a child off the PC for a day, use the
  manual lock.
- The limit field of the weekly table takes digits only: it used to save `12e0` as 12 and `1e3` as 1000. Times
  keep digits and `:` as they are typed, and the field in error is marked.
- The setup wizard sets the first child's week day by day, in the same table as Settings → Weekly schedule,
  instead of one limit for Monday–Thursday, one for Friday–Sunday and one night time for the whole week.
- Templates fill the weekly table, in the wizard and in the panel: "Default week" (the week a new child starts
  with) and "Every day like Monday". They replace the panel's "Mo → Tu–Fr" button. A script that completes the
  wizard without a week gets that default week.
- The container log shows the server itself, not every request: one line when it starts (version, port,
  database) and stops, a warning while the setup wizard is not completed, database migrations, and errors with
  their traceback and the request they broke. HTTP requests are no longer logged; what happens on the children's
  PCs stays in the panel's journal. A failing database health check is logged once, then its recovery.
- Log lines have one format with a colored level; `NO_COLOR=1` in `.env` turns the colors off.

### Client
- The installer's pairing-code hint points to the panel's new place: Settings → Add computer (or Re-pair).

## [0.8.2] - 2026-11-10

### Client
- "Shut down" on the lock screen works again: it did nothing since 0.8.0. A refused shutdown now shows
  its reason on the screen.

### Documentation
- README: the "How it works" diagram is a picture readable at the page's width (the Mermaid chart was scaled
  down to about 9 px text); the third screenshot shows the whole Settings tab, not one card.

## [0.8.1] - 2026-11-10

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

[Unreleased]: https://github.com/deresterr/vigil/compare/v0.8.2...HEAD
[0.8.2]: https://github.com/deresterr/vigil/releases/tag/v0.8.2
[0.8.1]: https://github.com/deresterr/vigil/releases/tag/v0.8.1
[0.8.0]: https://github.com/deresterr/vigil/releases/tag/v0.8.0
