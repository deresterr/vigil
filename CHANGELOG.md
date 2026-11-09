# Changelog

User-visible changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
the release tags. The reasoning behind each design decision (D1–D26) is in [`docs/decisions.md`](docs/decisions.md).

## [Unreleased]

### Documentation
- README rewritten (English and Polish): how it works, a quick start from the release, updating.
- The server's plain HTTP is named as a known limit in the README threat model and in SECURITY.md:
  the pairing response carries the PC's key and the panel login its password; use a reverse proxy with TLS.
- CONTRIBUTING.md, issue forms and a pull request template.

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
