# vigil_guardian

`installer/vigil_guardian.ps1`, Scheduled Task „Vigil Guardian” (SYSTEM, przy starcie + co 5 min, limit 5 min).
Przywraca to, co trzyma `vigil_watchdog` przy życiu, i każdą naprawę zgłasza rodzicowi (#36).

Model zagrożeń: **utrudnienie + wykrywanie**. Dziecko z podniesionymi uprawnieniami może wyłączyć też to
zadanie; do tego czasu każda zmiana jest cofana, a watchdog wysyła ją do Dziennika jako alert
`tamper_guardian_repaired`.

## Co sprawdza (w tej kolejności)

| Event ID | `check` | Znalezisko | Naprawa |
|---|---|---|---|
| 7003 | `binary_replaced` | SHA256 `vigil_watchdog.exe` ≠ `guardian.dat` (DPAPI LocalMachine) | kopia z `backup\vigil_watchdog.exe`, stop/start usługi |
| 7004 | `ifeo_debugger` | `Debugger` w IFEO dla `vigil_watchdog.exe` | usunięcie wartości |
| 7002 | `service_disabled` | `Start` usługi ≠ 2 (automatyczny) | `sc.exe config start= auto` |
| 7006 | `recovery_missing` | brak akcji `RESTART` w `sc qfailure` | te same akcje co instalator (restart ×3 po 60 s) |
| 7005 | `safeboot_missing` | brak `SafeBoot\{Minimal,Network}\vigil_watchdog` = `Service` | odtworzenie kluczy |
| 7001 | `service_not_running` | usługa stoi także po 75 s (recovery restartuje po 60 s) | `Start-Service` |

Event Log: Application, źródło `Vigil Guardian`. **Warning = naprawione, Error = naprawa się nie udała.**
ID 7001–7006 to kontrakt z `client/watchdog/Guardian/GuardianReporter.cs` (co minutę czyta wpisy z ostatniej
doby i wysyła każdy raz; serwer odrzuca duplikaty po `record_id` + czasie). Pozostałe ID są diagnostyczne:
`1003` nieczytelny `guardian.dat`, `1099` brak usługi (reinstalacja to zadanie instalatora), `9999` crash.

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Vigil Guardian' } -MaxEvents 20
```

## Czego nie robi

- nie instaluje usługi od nowa, gdy jej brak (tylko wpis 1099);
- nie pilnuje `vigil_ui` — to zadanie watchdoga (respawn, wylogowanie przy pętli zabijania);
- nie sprawdza `vigil.toml` — bez niego watchdog nie wstaje, co wychodzi jako `service_not_running`.

Przed v0.6 guardian restartował komputer po 3 nieudanych startach usługi, miał tryb `-DryRun` i sprawdzał
zadanie „Vigil Tray”; wszystko to usunięte.
