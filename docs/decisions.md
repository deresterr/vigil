# Design decisions

The decisions the code refers to as D1–D27, in short. The reasoning behind each is in the issues and pull
requests named next to it; the threat model in full is in the [README](../README.md#threat-model).

| # | Decision |
|---|---|
| D1 | Evolve the v0.4 code base rather than rewrite it. |
| D2 | Threat model: the child **may have an administrator account**. Nothing on the PC withstands an administrator, so every mechanism is protection (state on the server), friction (stops a non-elevated process) or detection (reported to the parent), and says which. |
| D3 | First milestone (M1): tamper detection plus the cheap fixes — signed requests (#3), pairing codes (#4), TOTP replay protection and a short lockout (#5), a login limit for the panel (#6). |
| D4 | Offline unlock: the watchdog verifies the parent's TOTP code itself when the server is unreachable. The secret is on the PC's disk (an administrator can read it); every offline grant is reported to the server afterwards (#9). |
| D5 | Alerts about tampering appear in the parent panel only (see D20). |
| D6 | No presence checks of the PC from the network (ping, ARP): detection relies on what the watchdog reports (boot time, a goodbye on stop). |
| D7 | One server for many children, each with any number of PCs; the time limit is per child (#40). |
| D8 | One UI process in the child's session: tray icon and lock overlay together (see D14). |
| D9 | Superseded by D21. |
| D10 | MIT licence; commits from the GitHub noreply address. |
| D11 | Windows 10/11 x64, **Home and Pro**: no Pro-only features (gpedit, AppLocker, secpol); policies only as registry values, which Home honours. |
| D12 | The child's account is the only interactive account on the PC and is an administrator; the parent maintains the PC through it, so per-user policies hit the parent too. The installer finds that account itself. |
| D13 | The repository is published only once the security work (M1), the client (M2), the multi-child server (M3) and the installer are done. |
| D14 | `vigil_ui` is resident: the watchdog starts it and keeps it alive; a lock is a message over the pipe (#35). |
| D15 | The guardian stays as friction, slimmed down; every repair is an alert in the Journal (#36). |
| D16 | No idle detection: time counts for the whole active session. |
| D17 | Per-user policies only where they close a way out from under the lock: Task Manager, changing the password, accessibility shortcuts, touchpad gestures (#37). |
| D18 | D7 in detail: one TOTP secret for all children; a child's time is the sum over its PCs; a PC is identified by its signing key; locks and pause are per child (#40). |
| D19 | A weekly schedule: limit and night time for each day of the week. A night belongs to the evening of its day (#48). |
| D20 | No notifications outside the panel (no e-mail, no messengers): alerts live in the Journal and are confirmed there (#50). |
| D21 | Polish and English: each UI follows its environment (panel: the browser; lock screen and installer: Windows). The server and the watchdog send codes, never sentences. Code, comments and logs are in English (#52). |
| D22 | The installer is `vigil_setup.exe` in C#: a console wizard and menu over a core that never touches the console, a parameter mode for scripts and CI, the guardian as `vigil_setup.exe guardian`, `update` without re-pairing; tested end to end in CI on a throwaway Windows runner (#62–#66). Since #76 the whole client is that one file. |
| D23 | No compatibility with installations from before v0.8: an older client is uninstalled with its own tools first. |
| D24 | Nothing from the child's environment reaches the lock screen process: the watchdog builds `vigil_ui`'s environment from a fixed list with .NET diagnostics off, startup hooks are disabled, and no native library is extracted at run time (#79). |
| D25 | Published as the same repository, with its history, issues and pull requests. |
| D26 | Schema migrations start from the v0.8 schema: no database of an earlier version is migrated (#78). |
| D27 | Fail-closed whatever the PC's network: no server answer since the service started or woke up (Fast Startup included), or none for 30 s, locks at once, without a grace period after logon. No network at all only changes the lock screen's message, never the decision: the child can switch Wi-Fi off without elevation (#128). |

## Open

- WinRE stays enabled unless the installer is told otherwise (`-DisableWinRE`): it is how a parent frees a PC
  that does not start (README, emergency path). The F8 boot menu is turned off.
