# Threat model

**English** · [Polski](threat-model.pl.md)

Vigil assumes the child **may have an administrator account**, because that is how most families use their PCs.
An administrator can run code as SYSTEM, read any file and disable any service, so nothing on the PC itself is
tamper-proof. Vigil therefore keeps everything that matters on the server, makes the rest hard to undo, and tells
the parent when someone tries.

Every mechanism belongs to one of three groups:

| Group | What it means |
|---|---|
| **Protects** | Holds even against an administrator, because the state lives on the server. |
| **Impedes** | Stops a program without administrator rights and casual attempts. An administrator gets past it. |
| **Detects** | Shows up as an alert in the Journal for the parent to confirm. |

What counts as a vulnerability under this model is in [`SECURITY.md`](../SECURITY.md).

## Protects

- Limits, night time, the manual lock and the time counter are on the server. The PC cannot change them, only
  feed it false data (see [Does not detect](#does-not-detect)).
- PCs are paired with a one-time code from the panel. Every pairing issues a new key, and the previous PC loses
  access.
- A TOTP code works only once. After wrong codes, unlocking pauses for a few minutes.
- Logins to the parent panel are rate-limited.

## Impedes

- Requests to the server are signed with a key in a file readable only by SYSTEM and administrators.
- No connection to the server means locked (fail-closed).
- An administrator cannot stop the service with a plain command. A guardian task undoes disabling the service,
  replacing its file and other changes to the installation every 5 minutes. The service also runs in Safe Mode.
- The lock screen covers every monitor and blocks shortcuts (Win, Alt+Tab, Alt+F4). Task Manager and changing the
  account password are disabled — for the parent too, as it is the same account.
- The lock screen cannot be closed without administrator rights. Closing it repeatedly logs the user off.
- Unlocking with the TOTP code works offline. The secret for that is on the PC's disk, and an administrator can
  read it.

## Detects

An alert in the Journal, for the parent to confirm, when:

- the service stopped, or restarted without a system restart,
- the service froze or was suspended while the computer was running,
- the lock screen was closed repeatedly,
- the guardian repaired something (a disabled service, a replaced file, …),
- someone typed a run of wrong TOTP codes, failed to log in to the panel repeatedly, or guessed at a pairing code
  until it was invalidated.

## Does not detect

| What happens | What the parent sees |
|---|---|
| The service is stopped for good | The PC is offline in the panel |
| The configuration on the PC is deleted or damaged (`vigil.toml`, `signing.dat`) — the service does not start | The PC is offline in the panel |
| Another system is booted from a USB stick | The PC is offline in the panel |
| An administrator reads the key from the disk and sends forged requests | Nothing |

## Network

The server speaks plain HTTP. Requests and responses are signed, but the pairing response carries the PC's key in
the clear, and the panel password travels unencrypted too: whoever can read that traffic can act as that PC or as
the parent.

On a network you do not trust, put a reverse proxy with TLS in front of the server and give `vigil_setup.exe` its
`https://` address. Vigil follows no redirects and uses no system proxy, and the certificate must be one Windows
trusts.
