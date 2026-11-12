# Changelog

User-visible changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
the release tags. The reasoning behind each design decision (D1–D26) is in [`docs/decisions.md`](docs/decisions.md).

## [Unreleased]

### Server
- A heartbeat arriving while a change from the panel was being saved could stop the server for 30 seconds and
  end with "database is locked" in the log; the change and the computers waited with it.
- Panel, setup wizard and login share one color scheme: one accent (cyan) for the main actions, focus and the
  selected item and confirmations, and one muted color per state everywhere — time graphite, night violet,
  manual lock red, unlocked green, pause grey; alerts and deleting keep a strong red. The extra-time and
  night-time buttons are outlined so they stand out from the card. The status card shows a tick, a padlock or
  pause bars in the state's color instead of an emoji. Journal icons take the color of what the entry is about
  (night, limit, pause, lock, unlock) instead of all being cyan. "Generate new TOTP code" is no longer a bright
  orange button.
- One term for moving the night time in the panel: the Status tag ("Moved to 22:30"), the journal and "Clear
  extra time and night time moves". One minute format in Status and the journal. "Show TOTP code" shows the key
  too, like a new code. The wizard explains a wrong TOTP code or an unknown time zone instead of showing the
  server's code.
- A damaged password hash refuses the login instead of failing with a server error. A new computer's name comes
  from the panel; the server no longer has a Polish default name.
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

- The heartbeat tells a locked computer when the night ends (`ui_state.night_end_local`).
- Codes are grouped as they are typed and shown: the pairing code as 1234 5678 in the panel, the TOTP code as
  123 456 in the setup wizard.

### Client
- A computer whose clock runs 5 s or more ahead of the server's (Windows syncs it once a week) no longer shows
  the lock screen for a moment at start: a request refused for its time is sent again at once with the server's
  time.
- Behind a reverse proxy that redirects to HTTPS the installer says which address to use instead of failing
  pairing with "HTTP 405". Vigil no longer follows redirects or uses the system proxy, and accepts only small
  answers from the server.
- A computer that joins Wi-Fi only after logon showed "No connection to your parent's server" for a moment at
  every start. The lock is the same (no answer from the server yet), but it now says the computer is not
  connected to a network and goes as soon as the network is there.
- The Event Log no longer gets a warning for every failed contact with the server (one a second while locked):
  one warning when there has been no answer for 30 seconds, one entry when the server answers again.
- The installer's pairing-code hint points to the panel's new place: Settings → Add computer (or Re-pair).
- The lock screen takes the panel's colors: a round badge with the state's icon in its color (limit, night,
  manual lock, pause), a title and one line below it, outlined turquoise controls, and no bright green on the
  pause screen. It stays black and dim, more so at night. The night lock says when the computer unlocks ("at
  06:30"), the pause screen how much time is left today; its button reads "Back to the computer".
- With display scaling above 100 % the lock screen was larger than the screen and off-center: the clock and
  "Shut down" were off the edge. It now covers each monitor exactly.
- Alt+Space no longer opens the lock screen's window menu (which could move or close it), and another program
  can no longer close, move, hide or minimize the lock screen for more than half a second.
- If the Vigil service stops unexpectedly while the computer is locked, the lock screen stays until the service is
  back, instead of disappearing after 30 seconds.
- "Shut down" asks on the lock screen itself instead of in a Windows message box.
- The code field on the lock screen groups the digits as 123 456 (a pasted code with spaces or dashes works too).
- A computer removed or re-paired in the panel says so on its lock screen instead of "No connection to the server".
- The lock screen clock always shows the server's time; messages after a code use the panel's format ("1 h
  added"); no technical error text is shown to the child; Polish texts no longer say "watchdog" or assume a boy.
- Tray: the icon takes the panel's turquoise, the tooltip says that a click starts a pause, and a day without a
  limit reads "No limit".
- The computer's key and the offline TOTP secret are written with their restricted permissions from the start,
  never readable by other accounts for a moment.
- Installer: the panel's turquoise accent; "night time" instead of "quiet hours"; Polish texts say "profil
  domyślny" and "zasady" instead of "Default profile" and "policy"; menu digits stay in order. The screens
  start a little off the window's left edge, and the pairing code is typed as 1234 5678.

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
