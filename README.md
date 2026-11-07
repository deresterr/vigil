# Vigil

Samodzielnie hostowana kontrola czasu dziecka przy komputerze z Windows 10/11.
Serwer w Dockerze w domowej sieci liczy czas i decyduje o blokadzie; usługa na PC dziecka
pokazuje pełnoekranową blokadę. Rodzic zarządza z przeglądarki i odblokowuje kodem TOTP z telefonu.

> **Status: v0.5, przed wydaniem 1.0.** Działa w codziennym użyciu. Dziecko z uprawnieniami administratora
> jest w stanie Vigila obejść — Vigil ma to utrudnić i pokazać rodzicowi, nie uniemożliwić
> (patrz [Model zagrożeń](#model-zagrożeń)). Plan dalszych prac: [`docs/review-2026-11.md`](docs/review-2026-11.md).

## Co potrafi

- dzienny limit czasu (osobno dni robocze / weekend) i cisza nocna,
- blokada ręczna z panelu, przedłużanie czasu z panelu lub kodem TOTP wpisanym na ekranie blokady,
- odblokowanie kodem z telefonu działa także, gdy serwer jest niedostępny,
- pauza uruchamiana przez dziecko (czas nie leci, ekran zablokowany),
- fail-closed: brak połączenia z serwerem = blokada,
- dziennik zdarzeń w panelu rodzica z alertami o próbach obejścia.

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
- usługa, której administrator nie zatrzyma zwykłym poleceniem; strażnik (guardian) przywraca usługę
  i binarkę; usługa działa też w trybie awaryjnym,
- ekran blokady na wszystkich monitorach, zablokowane skróty (Win, Alt+Tab, Alt+F4), polityki
  (menedżer zadań, edytor rejestru, wylogowanie) — działają też na rodzica, bo to to samo konto,
- wielokrotne zamykanie ekranu blokady kończy się wylogowaniem,
- odblokowanie offline kodem TOTP: sekret jest na dysku PC, administrator może go odczytać.

**Wykrywa** — pokazuje w zakładce Dziennik jako alert do potwierdzenia:
- zatrzymanie usługi Vigil, ponowne uruchomienie Vigila bez restartu systemu,
- Vigil zamrożony lub wstrzymany, choć komputer działał,
- wielokrotne zamykanie ekranu blokady,
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
- do budowania klienta: Windows z .NET 8 SDK (lub paczka z GitHub Actions).

## Szybki start

**1. Serwer**

```bash
cd server
cp .env.example .env          # nazwa instancji i port
docker compose up -d
```

Otwórz `http://<ip-serwera>:8088/setup` i przejdź kreator (hasło rodzica, TOTP, limity, adres serwera).
Kreator jest dostępny bez logowania do momentu ukończenia — zrób to od razu po uruchomieniu.

**2. Paczka dla PC dziecka** (na Windows, w katalogu repo)

```
Build.cmd            → dist\
```

**3. Instalacja na PC dziecka** — w panelu rodzica kliknij „Wygeneruj kod parowania”, skopiuj `dist\`
na PC dziecka i uruchom jako administrator:

```
Install.cmd          (tryb interaktywny: adres serwera, kod parowania, konto dziecka)
```

Odinstalowanie: `Cleanup.cmd` (lub `vigil-cleanup-offline.cmd` z WinRE, gdy system nie startuje).

## Struktura repo

| Katalog | Zawartość |
|---|---|
| `server/` | FastAPI + SQLite, panel rodzica, testy |
| `client/` | usługa Windows (`watchdog`), ekran blokady (`lockscreen`), ikona w zasobniku (`tray`) |
| `installer/` | skrypty instalacji, deinstalacji i strażnika integralności |
| `docs/` | architektura, przegląd i roadmapa |

## Licencja

MIT — zobacz [`LICENSE`](LICENSE).
