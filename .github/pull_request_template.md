<!-- One issue, one branch, one pull request to main. See CONTRIBUTING.md. -->

Closes #

## What and why


## Threat model

<!-- For a change that protects the PC or the server: protects (state on the server), impedes (stops a
non-administrator) or detects (an alert in the Journal)? Otherwise: n/a. -->

## Checklist

- [ ] Server: `ruff check .` and `pytest -q` pass; new behaviour has a test
- [ ] Client: CI builds the package and the installer e2e passes
- [ ] Tested on Windows (edition, language) — or not needed:
- [ ] `docs/`, both READMEs and `CHANGELOG.md` (Unreleased) updated where users or behaviour change
- [ ] `CLAUDE.md` updated if an invariant or a gotcha changed
- [ ] No addresses, host names or personal data in code, logs, screenshots or this description
