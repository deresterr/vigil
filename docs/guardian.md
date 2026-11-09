# Vigil Guardian

`vigil_setup.exe guardian` (`client/setup/Core/Guardian.cs`), uruchamiany przez zadanie „Vigil Guardian”
(SYSTEM, przy starcie + co 5 min, limit 5 min) z `C:\ProgramData\Vigil\vigil_setup.exe` — instalator kopiuje
tam samego siebie (#65). Przywraca to, co trzyma `vigil_watchdog` przy życiu, i każdą naprawę zgłasza
rodzicowi (#36).

Model zagrożeń: **utrudnienie + wykrywanie**. Dziecko z podniesionymi uprawnieniami może wyłączyć też to
zadanie albo podmienić `vigil_setup.exe` w ProgramData (jak wcześniej skrypt PS); do tego czasu każda zmiana
jest cofana, a watchdog wysyła ją do Dziennika jako alert `tamper_guardian_repaired`.

## Co sprawdza (w tej kolejności)

| Event ID | `check` | Znalezisko | Naprawa |
|---|---|---|---|
| 7003 | `binary_replaced` | SHA256 `vigil_watchdog.exe` ≠ `guardian.dat` (DPAPI LocalMachine) | stop usługi, kopia z `backup\vigil_watchdog.exe`; start dopiero po pozostałych naprawach (IFEO Debugger przejąłby ten start), wtedy wpis |
| 7004 | `ifeo_debugger` | `Debugger` w IFEO dla `vigil_watchdog.exe` | usunięcie wartości |
| 7002 | `service_disabled` | `Start` usługi w rejestrze ≠ 2 (automatyczny) | `sc.exe config start= auto` |
| 7006 | `recovery_missing` | `FailureActions` w rejestrze ≠ restart ×3 albo bez flagi „także przy błędzie” | te same akcje co instalator (restart ×3 po 60 s) |
| 7005 | `safeboot_missing` | brak `SafeBoot\{Minimal,Network}\vigil_watchdog` = `Service` | odtworzenie kluczy |
| 7001 | `service_not_running` | usługa stoi także po 75 s (recovery restartuje po 60 s) | start usługi |

Wszystko czytane z rejestru i API, nic z tekstu narzędzi (polski Windows tłumaczy np. `sc qfailure`).
Kryterium 7006 jest to samo co w verify („Recovery: restart×3”).

Event Log: Application, źródło `Vigil Guardian`. **Warning = naprawione, Error = naprawa się nie udała.**
ID 7001–7006 to kontrakt z `client/watchdog/Guardian/GuardianReporter.cs` (`VigilLayout.GuardianChecks`; co
minutę czyta wpisy z ostatniej doby i wysyła każdy raz; serwer odrzuca duplikaty po `record_id` + czasie).
Pozostałe ID są diagnostyczne: `1003` nieczytelny `guardian.dat`, `1099` brak usługi (reinstalacja to zadanie
instalatora), `9999` crash. Treści wpisów są techniczne, po angielsku (jak log watchdoga).

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Vigil Guardian' } -MaxEvents 20
```

## Instalacja i aktualizacja

- Instalacja (także `update` i „Napraw”) kończy działającą instancję i usuwa zadanie przed kopiowaniem plików
  (guardian trzyma `vigil_setup.exe` otwarty i „naprawiałby” wymienianą usługę), potem zakłada je od nowa.

## Czego nie robi

- nie instaluje usługi od nowa, gdy jej brak (tylko wpis 1099);
- nie pilnuje `vigil_ui` — to zadanie watchdoga (respawn, wylogowanie przy pętli zabijania);
- nie sprawdza `vigil.toml` — bez niego watchdog nie wstaje, co wychodzi jako `service_not_running`.

Przed v0.6 guardian restartował komputer po 3 nieudanych startach usługi, miał tryb `-DryRun` i sprawdzał
zadanie „Vigil Tray”; wszystko to usunięte.
