# Threat model

**English** · [Polski](threat-model.pl.md)

Vigil runs in one of two setups, chosen when it is installed:

- **Recommended: the child has a standard account** of its own, and the parent an administrator account with a
  password the child does not know. Only the child's sessions are counted and locked. Windows itself refuses a
  standard user what would switch Vigil off: stopping the service, replacing its files, reading its secrets,
  setting the clock. The installer can create the child's account.
- **Supported: one administrator account** that the child and the parent share. An administrator can run code as
  SYSTEM, read any file and disable any service, so nothing on the PC itself is tamper-proof: what matters lives
  on the server, the rest is made hard to undo, and the parent sees the attempts.

Every mechanism belongs to one of three groups:

| Group | What it means |
|---|---|
| **Protects** | Holds against the child: the state lives on the server, or Windows refuses the child's account. |
| **Impedes** | Stops a program without administrator rights and casual attempts. An administrator gets past it. |
| **Detects** | Shows up as an alert in the Journal for the parent to confirm. |

What counts as a vulnerability under this model is in [`SECURITY.md`](../SECURITY.md).

## By setup

| Mechanism | Standard account | One administrator account |
|---|---|---|
| Limits, night time, the manual lock and the time counter are on the server. The PC cannot change them, only feed it false data | **Protects** | **Protects** |
| Pairing with a one-time code; every pairing issues a new key and the previous PC loses access | **Protects** | **Protects** |
| A TOTP code works once; after wrong codes unlocking pauses for a few minutes. Panel logins are rate-limited | **Protects** | **Protects** |
| The service cannot be stopped, disabled or deleted; its files cannot be replaced; the guardian task cannot be changed | **Protects** — needs administrator rights | **Impedes** — a plain command fails, the guardian undoes the rest every 5 minutes, every repair is an alert |
| Requests are signed with a key only SYSTEM and administrators can read; the TOTP secret for offline unlocking likewise | **Protects** | **Impedes** — an administrator can read both |
| No connection to the server means locked (fail-closed) | **Protects** — the child cannot stop the service or forge answers | **Impedes** |
| The lock screen process cannot be closed by the user; closing it repeatedly logs the session off | **Protects** | **Impedes** |
| Setting the clock (the service does not use it, the server journals a difference) | **Protects** — needs administrator rights | **Impedes** |
| The lock screen covers every monitor and blocks shortcuts (Win, Alt+Tab, Alt+F4); Task Manager and changing the password are off in the child's account | **Impedes** | **Impedes** — for the parent too, as it is the same account |
| The service also runs in Safe Mode; the F8 boot menu is off | **Protects** — the boot configuration needs administrator rights | **Impedes** |

A standard account holds as long as Windows does: keep it updated, so that no known flaw lets a standard user gain
administrator rights.

## The parent's account

In the standard-account setup the parent's administrator account is what keeps the child out:

- Every administrator account on the PC needs a password the child does not know. The installer makes the
  account it runs from set one if it has none; check any other administrator account yourself.
- Automatic sign-in is turned off: Windows would otherwise start in the parent's session, which nobody counts.
- The parent's own session is never counted or locked, also without the server. Sign out or lock it (Win+L)
  before leaving the PC: an unlocked parent's session is a PC without limits.

## Physical access

- **Windows Recovery** (Shift + Restart on the sign-in screen) asks for an administrator's password for its
  command prompt, System Restore and Reset this PC. Without it only Startup Settings work, and Vigil runs in Safe
  Mode too (without networking it stays locked: no server). In the one-account setup the child knows that password.
- **Another system or Windows Setup from a USB stick** asks for nothing: it can delete Vigil's folder, as the
  [emergency path](installer.md#emergency-windows-does-not-start-or-vigil-cannot-be-removed-normally) does, or
  reset the PC. Neither setup sees that.
- Against that: a firmware (BIOS/UEFI) password, booting from USB off, and **Device encryption** where the PC has
  it (Windows 10/11 Home on most newer hardware; BitLocker on Pro). An encrypted disk asks for its recovery key
  outside Windows, so keep that key away from the child — and at hand for the emergency path.

## Detects

An alert in the Journal, for the parent to confirm, when:

- the service stopped, or restarted without a system restart,
- the service froze or was suspended while the computer was running,
- the lock screen was closed repeatedly,
- the guardian repaired something (a disabled service, a replaced file, …),
- the child's account, a standard one so far, became an administrator (Settings also shows it whenever it is one),
- someone typed a run of wrong TOTP codes, failed to log in to the panel repeatedly, or guessed at a pairing code
  until it was invalidated.

A guardian alert can be forged: any account may write to Windows' Application log, also under the
guardian's name. That gives a false alert, never a hidden one.

## Does not detect

| What happens | What the parent sees |
|---|---|
| The service is stopped for good (an administrator) | The PC is offline in the panel |
| The configuration on the PC is deleted or damaged (`vigil.toml`, `signing.dat`) — the service does not start | The PC is offline in the panel |
| Another system is booted from a USB stick, or the PC is reset | The PC is offline in the panel |
| An administrator reads the key from the disk and sends forged requests | Nothing |
| The child uses the parent's unlocked session | Nothing: that session is not counted |

## Network

The server speaks plain HTTP. Requests and responses are signed, but the pairing response carries the PC's key in
the clear, and the panel password travels unencrypted too: whoever can read that traffic can act as that PC or as
the parent.

On a network you do not trust, put a reverse proxy with TLS in front of the server and give `vigil_setup.exe` its
`https://` address. Vigil follows no redirects and uses no system proxy, and the certificate must be one Windows
trusts.

Set `FORWARDED_ALLOW_IPS` in `.env` to the address the proxy connects to Vigil from (the proxy's container address
or the Docker network's, a LAN address when it runs on another machine; a network such as `172.18.0.0/16` works
too). Otherwise every request seems to come from the proxy: the panel's login limit, which counts wrong passwords
per address, becomes one limit for everyone — a child typing wrong passwords locks the parent out as well — and the
session cookie is not marked Secure. Never `*`: anyone could then send a made-up `X-Forwarded-For` and get round the
login limit.
