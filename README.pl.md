# Vigil

[English](README.md) · **Polski**

Samodzielnie hostowana kontrola czasu dziecka przy komputerze z Windows 10/11.
Serwer w Dockerze w domowej sieci liczy czas i decyduje o blokadzie; usługa na PC dziecka
pokazuje pełnoekranową blokadę. Rodzic zarządza z przeglądarki i odblokowuje kodem TOTP z telefonu.

> **Status: v0.7, przed wydaniem 1.0.** Działa w codziennym użyciu. Dziecko z uprawnieniami administratora
> jest w stanie Vigila obejść — Vigil ma to utrudnić i pokazać rodzicowi, nie uniemożliwić
> (patrz [Model zagrożeń](#model-zagrożeń)). Plan dalszych prac: [`docs/review-2026-11.md`](docs/review-2026-11.md).

## Co potrafi

- kilkoro dzieci na jednym serwerze, każde z dowolną liczbą komputerów (czas liczony łącznie),
- harmonogram tygodnia: dzienny limit czasu i cisza nocna osobno dla każdego dnia i każdego dziecka,
- blokada ręczna z panelu, przedłużanie czasu z panelu lub kodem TOTP wpisanym na ekranie blokady,
- odblokowanie kodem z telefonu działa także, gdy serwer jest niedostępny,
- pauza uruchamiana przez dziecko (czas nie leci, ekran zablokowany),
- fail-closed: brak połączenia z serwerem = blokada,
- dziennik zdarzeń w panelu rodzica z alertami o próbach obejścia,
- panel, ekran blokady i instalator po polsku albo angielsku — według języka przeglądarki i Windows.

## Model zagrożeń

Zakładamy, że dziecko **może mieć konto administratora** — tak korzysta z komputera większość rodzin.
Administrator może uruchomić kod jako SYSTEM, odczytać każdy plik i wyłączyć każdą usługę, więc na samym
PC nic nie jest odporne na manipulację. Każdy mechanizm Vigila należy do jednej z trzech grup:

**Chroni** — działa także przeciw administratorowi, bo stan jest na serwerze:
- limity, cisza nocna, blokada ręczna i licznik czasu są na serwerze; z PC nie da się ich zmienić,
  można najwyżej podsunąć fałszywe dane (patrz „Nie wykrywa”),
- parowanie PC jednorazowym kodem z panelu; każde parowanie wydaje nowy klucz, poprzedni PC traci dostęp,
- kod TOTP działa tylko raz; po błędnych kodach blokada na minuty, więc dziecko nie zablokuje rodzicowi
  odblokowania na dłużej,
- limit prób logowania do panelu rodzica.

**Utrudnia** — zatrzymuje proces bez uprawnień administratora i przypadkowe próby; administrator to obejdzie:
- żądania do serwera podpisane kluczem z pliku dostępnego tylko dla SYSTEM i administratorów,
- brak połączenia z serwerem = blokada (fail-closed),
- usługa, której administrator nie zatrzyma zwykłym poleceniem; strażnik (guardian) co 5 minut cofa
  wyłączenie usługi, podmianę jej pliku i inne zmiany w instalacji; usługa działa też w trybie awaryjnym,
- ekran blokady na wszystkich monitorach, zablokowane skróty (Win, Alt+Tab, Alt+F4), wyłączony
  menedżer zadań i zmiana hasła konta — te dwie polityki działają też na rodzica, bo to to samo konto,
- procesu Vigila w sesji dziecka nie da się zamknąć bez uprawnień administratora; zamykany
  wielokrotnie (np. z podniesionego PowerShella) kończy się wylogowaniem,
- odblokowanie offline kodem TOTP: sekret jest na dysku PC, administrator może go odczytać.

**Wykrywa** — pokazuje w zakładce Dziennik jako alert do potwierdzenia:
- zatrzymanie usługi Vigil, ponowne uruchomienie Vigila bez restartu systemu,
- Vigil zamrożony lub wstrzymany, choć komputer działał,
- wielokrotne zamykanie ekranu blokady,
- każdą naprawę strażnika (np. wyłączona usługa, podmieniony plik),
- serie błędnych kodów TOTP, nieudane logowania do panelu, unieważniony kod parowania.

Odblokowania offline i okresy bez połączenia trafiają do Dziennika jako zwykłe wpisy.

**Nie wykrywa:** usługi zatrzymanej na stałe (panel pokazuje tylko, że PC jest offline), innego systemu
uruchomionego z pendrive'a (też tylko offline) ani sfałszowanych żądań wysłanych przez administratora,
który odczytał klucz z dysku.

## Wymagania

- serwer: dowolny Linux z Dockerem w tej samej sieci co PC dziecka (lub osiągalny przez VPN),
- PC dziecka: Windows 10/11 x64, Home lub Pro; konto dziecka jest jedynym kontem użytkownika
  i może mieć uprawnienia administratora (rodzic serwisuje PC z tego samego konta),
- telefon rodzica z aplikacją TOTP (Aegis, Google Authenticator, …),
- do budowania klienta: Windows z .NET 8 SDK (albo gotowy `vigil_setup.exe` z wydania lub z GitHub Actions).

## Szybki start

Przy każdym wydaniu (zakładka **Releases**) obie części są gotowe: obraz serwera
`ghcr.io/deresterr/vigil-server:<wersja>` i `vigil_setup.exe` — cały klient dla PC dziecka w jednym pliku.
Z nimi nie budujesz niczego: w kroku 1 ustaw w `server/.env` `VIGIL_IMAGE=ghcr.io/deresterr/vigil-server:<wersja>`
i uruchom `docker compose pull && docker compose up -d`, a krok 2 zastępuje pobrany `vigil_setup.exe`.

**1. Serwer**

```bash
cd server
cp .env.example .env          # port, opcjonalnie gotowy obraz
docker compose up -d
```

Otwórz `http://<ip-serwera>:8088/setup` i przejdź kreator (hasło rodzica, TOTP, limity, adres serwera).
Limity i ciszę nocną dla każdego dnia tygodnia ustawisz potem w panelu (Ustawienia → Harmonogram tygodnia).
Kreator jest dostępny bez logowania do momentu ukończenia — zrób to od razu po uruchomieniu.

**2. Paczka dla PC dziecka** (na Windows, w katalogu repo)

Potrzebny .NET 8 SDK — tylko na maszynie, która buduje; paczka jest self-contained, PC dziecka go nie potrzebuje:

```
winget install Microsoft.DotNet.SDK.8
```

Potem (w nowym oknie, żeby `dotnet` był w PATH):

```
Build.cmd            → dist\vigil_setup.exe
```

`vigil_setup.exe` to cały klient: instalator niesie w sobie usługę i ekran blokady. Zamiast budować lokalnie
możesz go pobrać z artefaktu `vigil-client-…` ostatniego przebiegu CI (zakładka Actions).

**3. Instalacja na PC dziecka** — skopiuj `vigil_setup.exe` na PC dziecka i uruchom go (sam poprosi
o uprawnienia administratora; pobrany plik może najpierw dostać od SmartScreen „System Windows ochronił
ten komputer”: Więcej informacji → Uruchom mimo to). Kreator pyta o adres serwera i kod parowania (panel rodzica: Ustawienia →
Komputery dziecka → „Dodaj komputer”), konto dziecka znajduje sam. Uruchomiony ponownie pokazuje menu:
sprawdzenie, aktualizacja, zmiana adresu serwera, ponowne parowanie, odinstalowanie.

```
vigil_setup.exe                  kreator, a gdy Vigil jest zainstalowany: stan i menu (menu: Odinstaluj)
vigil_setup.exe -Mode verify     sprawdza instalację (albo, po deinstalacji, czy nic nie zostało)
vigil_setup.exe -Mode uninstall  odinstalowuje bez menu
```

Parametry dla skryptów i każdy krok instalacji: [`docs/installer.md`](docs/installer.md).

**Gdy Windows nie startuje albo Vigila nie da się usunąć normalnie:** uruchom Windows Recovery → Rozwiąż
problemy → Wiersz polecenia, wpisz `rmdir /s /q D:\ProgramData\Vigil` (dysk z Windows to tam często `D:`;
sprawdź `dir D:\ProgramData`), uruchom komputer normalnie, odinstaluj (menu → Odinstaluj) i uruchom `vigil_setup.exe -Mode verify`.
Bez swoich plików Vigil nie startuje, więc nie ma blokady. Na zwykłą blokadę wystarczy kod TOTP rodzica.

## Struktura repo

| Katalog | Zawartość |
|---|---|
| `server/` | FastAPI + SQLite, panel rodzica, testy |
| `client/` | usługa Windows (`watchdog`), ekran blokady z ikoną w zasobniku (`ui`), instalator (`setup`) |
| `client/setup/` | instalator `vigil_setup.exe`: kreator, menu, verify, strażnik integralności |
| `installer/` | stary instalator PowerShell, używany tylko przez test w CI jako instalacja sprzed wersji w C# |
| `e2e/` | test end-to-end instalatora (CI, jednorazowy Windows) |
| `docs/` | architektura, przegląd i roadmapa |

## Bezpieczeństwo

Podatności zgłaszaj prywatnie — zasady i to, co mieści się w modelu zagrożeń, opisuje [`SECURITY.pl.md`](SECURITY.pl.md).

## Licencja

MIT — zobacz [`LICENSE`](LICENSE).
