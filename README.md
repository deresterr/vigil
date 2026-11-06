# Vigil

Samodzielnie hostowana kontrola czasu dziecka przy komputerze z Windows 10/11.
Serwer w Dockerze w domowej sieci liczy czas i decyduje o blokadzie; usługa na PC dziecka
pokazuje pełnoekranową blokadę. Rodzic zarządza z przeglądarki i odblokowuje kodem TOTP z telefonu.

> **Status: v0.4, przed wydaniem 1.0.** Działa w codziennym użyciu, ale ma znane problemy bezpieczeństwa
> opisane w [`docs/review-2026-11.md`](docs/review-2026-11.md). Nie polegaj na nim wobec dziecka,
> które potrafi uruchomić PowerShella.

## Co potrafi

- dzienny limit czasu (osobno dni robocze / weekend) i cisza nocna,
- blokada ręczna z panelu, przedłużanie czasu z panelu lub kodem TOTP wpisanym na ekranie blokady,
- pauza uruchamiana przez dziecko (czas nie leci, ekran zablokowany),
- fail-closed: brak połączenia z serwerem = blokada,
- dziennik zdarzeń w panelu rodzica.

## Wymagania

- serwer: dowolny Linux z Dockerem w tej samej sieci co PC dziecka (lub osiągalny przez VPN),
- PC dziecka: Windows 10/11 x64, **dziecko na koncie standardowym** (bez uprawnień administratora),
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

**3. Instalacja na PC dziecka** — skopiuj `dist\`, uruchom jako administrator:

```
Install.cmd          (tryb interaktywny: adres serwera, konto dziecka, hasło awaryjne)
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
