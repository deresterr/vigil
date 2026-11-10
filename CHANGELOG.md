# Changelog

User-visible changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
the release tags. The reasoning behind each design decision (D1–D26) is in [`docs/decisions.md`](docs/decisions.md).

## [Unreleased]

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

[Unreleased]: https://github.com/deresterr/vigil/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/deresterr/vigil/releases/tag/v0.8.0
