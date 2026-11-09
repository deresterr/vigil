# Vigil Guardian

`vigil_setup.exe guardian` (`client/setup/Core/Guardian.cs`), run by the task "Vigil Guardian" (SYSTEM, at
startup + every 5 min, 5 min limit) from `C:\ProgramData\Vigil\vigil_setup.exe`, where the installer copies
itself (#65). It puts back what keeps `vigil_watchdog` running and reports every repair to the parent (#36).

Threat model: **friction + detection**. An elevated child can disable this task too, or replace
`vigil_setup.exe` in ProgramData; until then every change below is undone, and the watchdog sends it to the
Journal as a `tamper_guardian_repaired` alert.

## What it checks (in this order)

| Event ID | `check` | Finding | Repair |
|---|---|---|---|
| 7003 | `binary_replaced` | SHA-256 of `vigil_watchdog.exe` ≠ `guardian.dat` (DPAPI LocalMachine) | service stopped, copy from `backup\vigil_watchdog.exe`; started only after the other repairs (an IFEO debugger would take over that start), then the entry |
| 7004 | `ifeo_debugger` | `Debugger` in IFEO for `vigil_watchdog.exe` | value removed |
| 7002 | `service_disabled` | the service's `Start` in the registry ≠ 2 (automatic) | `sc.exe config start= auto` |
| 7006 | `recovery_missing` | `FailureActions` in the registry ≠ restart ×3, or without the "also on a non-crash failure" flag | the installer's actions (restart ×3 after 60 s) |
| 7005 | `safeboot_missing` | no `SafeBoot\{Minimal,Network}\vigil_watchdog` = `Service` | keys recreated |
| 7001 | `service_not_running` | the service is still stopped after 75 s (recovery restarts after 60 s) | service started |

Everything is read from the registry and APIs, nothing from tool output (Polish Windows localizes e.g.
`sc qfailure`). The 7006 criterion is the same as verify's ("Recovery: restart×3").

Event Log: Application, source `Vigil Guardian`. **Warning = repaired, Error = the repair failed.**
IDs 7001–7006 are a contract with `client/watchdog/Guardian/GuardianReporter.cs` (`VigilLayout.GuardianChecks`):
every minute it reads the entries of the last day and sends each one once; the server drops duplicates by
`record_id` + time. The other IDs are diagnostic: `1003` unreadable `guardian.dat`, `1099` no service
(reinstalling is the installer's job), `9999` crash. Entries are technical, in English.

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Vigil Guardian' } -MaxEvents 20
```

## Install and update

Install (also `update` and Repair) ends a running instance and deletes the task before copying files (the
guardian holds `vigil_setup.exe` open and would "repair" the service being replaced), then creates it again.

## What it does not do

- reinstall the service when it is missing (only entry 1099);
- watch `vigil_ui`: that is the watchdog's job (respawn, logoff on a kill loop);
- check `vigil.toml`: without it the watchdog does not start, which shows as `service_not_running`.
