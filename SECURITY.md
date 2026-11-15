# Security

**English** · [Polski](SECURITY.pl.md)

## Reporting a vulnerability

Do not open a public issue. Use a private report on GitHub: the **Security** tab → **Report a vulnerability**.
Only the maintainer sees it.

Helpful details: the version (release tag or commit), what it affects (server, the service on the PC, installer),
steps to reproduce, and what rights on the child's PC it needs (standard account or administrator).

This is a spare-time project: expect an answer within a few days and a fix in the next release.
Only the latest version on the **Releases** tab is supported.

## What counts as a vulnerability

Vigil runs in two setups ([threat model](docs/threat-model.md)): the child on a **standard account** of its own
(recommended), or one account the child and the parent share, which is an **administrator**. Nothing on the PC
itself withstands an administrator, so in that setup whether something is a bug depends on the group its
mechanism belongs to:

| Group | Examples | Report it if… |
|---|---|---|
| **Protects** (state on the server) | limits, schedule, manual lock, pairing, TOTP, panel login | it can be bypassed **even from an administrator's PC** — e.g. change a limit, take over the panel, use a TOTP code twice, pair a computer without a code |
| **Impedes** | signed requests, fail-closed, service and UI process DACLs, registry policies | it can be bypassed **without administrator rights** (non-elevated process, standard account) |
| **Detects** | alerts in the Journal | an administrator disables Vigil so that **no alert appears** in the panel, and the threat model does not list it under "Does not detect" |

**On a standard account**, report anything that lets the child get round Vigil from that account: stop or change
the service, its files or the guardian task, read the signing key or the TOTP secret, close the lock screen, or
use the PC without the time counting — other than through the parent's password, a Windows flaw that grants
administrator rights (report that to Microsoft), or physical access (below).

Also a vulnerability: anything that lets someone on the same network (without access to the child's PC) affect the
server — read secrets, change settings, impersonate the child's computer — other than by reading or altering plain
HTTP traffic (below).

**Not vulnerabilities** (listed in the threat model as known limits): an administrator getting past an "Impedes"
mechanism, an administrator reading secrets from the PC's disk (the signing key, the TOTP secret for offline
unlocking), booting another system from a USB stick or Windows Recovery, resetting the PC, using the parent's
unlocked session, a service stopped for good, which shows in the panel as an
offline computer, and anything done by reading or altering the traffic of a server run without TLS — it speaks
plain HTTP, the pairing response carries the PC's key and the panel login its password ("Network" in the
[threat model](docs/threat-model.md#network); put a reverse proxy with TLS in front of it).
