# Contributing

Vigil is a one-person, spare-time project. Bug reports and ideas are welcome; for larger changes open an issue
first, so we can agree on the approach before you write code.

## Reporting

- **Security issues: never in a public issue.** Use Security → Report a vulnerability; what counts as one is in
  [`SECURITY.md`](SECURITY.md).
- Bugs and ideas: [open an issue](https://github.com/deresterr/vigil/issues/new/choose) with the form.
  For a bug the most useful details are the version, the Windows edition and language, whether the child's account
  is an administrator, and the installer log (`%TEMP%\vigil_setup_<time>.log`, its path is on screen).
- Before you attach a log or a screenshot, remove server addresses, computer names and children's names.

## Building and testing

**Server** (Python 3.12):

```bash
cd server
pip install -r requirements-dev.txt
ruff check .
pytest -q
docker compose -f compose.yaml -f compose.dev.yaml up -d   # live-mounted code, for the panel
```

**Client** (Windows, .NET 10 SDK): `Build.cmd` → `dist\vigil_setup.exe`. CI builds it on `windows-latest` and runs
the installer end-to-end test (`e2e/installer-e2e.ps1`) on that throwaway runner; every pull request that touches
the client gets the package as a build artifact. **Never run the e2e test on a real PC** — it installs, breaks and
removes Vigil on purpose. Test the package on a virtual machine; for the lock screen alone there is
`vigil_ui.exe --standalone-test` (see [`docs/architecture.md`](docs/architecture.md)).

## Rules

- **One issue, one branch, one pull request to `main`.** Keep it focused; no stacked pull requests.
- **Threat model.** The child may be an administrator (D2). Say in the pull request which group a protective change
  belongs to — protects (state on the server), impedes (stops a non-elevated process) or detects (an alert in the
  Journal) — and do not describe friction as protection.
- **Invariants** listed in [`CLAUDE.md`](CLAUDE.md) (fail-closed, one uvicorn worker, time counted on the server,
  lock priority, Windows Home support, file names and DPAPI entropy) are design decisions: discuss before changing them.
- **Tests.** New server behaviour gets a test; `ruff check .` and `pytest -q` pass. Client changes must build in CI.
- **Language.** Code, comments, logs and commit messages in English. User-facing text in Polish and English:
  the panel marks the Polish source text (`t('…')`, `data-i18n`) and keeps English in `server/app/static/i18n.js`;
  the lock screen and the installer keep both in their `Strings.cs`. The server and the service send codes, never sentences.
- **Database.** Schema changes only as a new entry at the end of `server/app/migrations.py`, with a test.
- **Docs.** When behaviour changes, update `docs/` (and the README if users see it) in the same pull request, and add
  a line under `Unreleased` in [`CHANGELOG.md`](CHANGELOG.md). Both READMEs carry the same content.

`CLAUDE.md` is the working notes for AI-assisted development in this repository: layout, invariants and the pitfalls
that already cost time. Worth reading before a larger change, whoever writes the code.

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE).
