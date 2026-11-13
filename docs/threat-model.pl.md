# Model zagrożeń

[English](threat-model.md) · **Polski**

Vigil zakłada, że dziecko **może mieć konto administratora**, bo tak korzysta z komputera większość rodzin.
Administrator może uruchomić kod jako SYSTEM, odczytać każdy plik i wyłączyć każdą usługę, więc na samym PC nic
nie jest odporne na manipulację. Dlatego wszystko, co ważne, Vigil trzyma na serwerze, resztę utrudnia odkręcić,
a o każdej próbie mówi rodzicowi.

Każdy mechanizm należy do jednej z trzech grup:

| Grupa | Co to znaczy |
|---|---|
| **Chroni** | Działa także przeciw administratorowi, bo stan jest na serwerze. |
| **Utrudnia** | Zatrzymuje program bez uprawnień administratora i przypadkowe próby. Administrator to obejdzie. |
| **Wykrywa** | Pojawia się jako alert w Dzienniku, do potwierdzenia przez rodzica. |

Co w tym modelu jest podatnością: [`SECURITY.pl.md`](../SECURITY.pl.md).

## Chroni

- Limity, cisza nocna, blokada ręczna i licznik czasu są na serwerze. Z PC nie da się ich zmienić, można najwyżej
  podsunąć fałszywe dane (patrz [Nie wykrywa](#nie-wykrywa)).
- PC paruje się jednorazowym kodem z panelu. Każde parowanie wydaje nowy klucz, a poprzedni PC traci dostęp.
- Kod TOTP działa tylko raz. Po błędnych kodach odblokowanie wstrzymuje się na kilka minut.
- Liczba prób logowania do panelu rodzica jest ograniczona.

## Utrudnia

- Żądania do serwera są podpisane kluczem z pliku dostępnego tylko dla SYSTEM i administratorów.
- Brak połączenia z serwerem oznacza blokadę (fail-closed).
- Administrator nie zatrzyma usługi zwykłym poleceniem. Zadanie strażnika co 5 minut cofa wyłączenie usługi,
  podmianę jej pliku i inne zmiany w instalacji. Usługa działa też w trybie awaryjnym.
- Ekran blokady zakrywa wszystkie monitory i blokuje skróty (Win, Alt+Tab, Alt+F4). Menedżer zadań i zmiana hasła
  konta są wyłączone — także dla rodzica, bo to to samo konto.
- Ekranu blokady nie da się zamknąć bez uprawnień administratora. Wielokrotne zamykanie wylogowuje użytkownika.
- Odblokowanie kodem TOTP działa offline. Sekret do tego jest na dysku PC i administrator może go odczytać.

## Wykrywa

Alert w Dzienniku, do potwierdzenia przez rodzica, gdy:

- usługa została zatrzymana albo uruchomiona ponownie bez restartu systemu,
- usługa zamarła albo została wstrzymana przy działającym komputerze,
- ekran blokady był wielokrotnie zamykany,
- strażnik coś naprawił (wyłączoną usługę, podmieniony plik, …),
- ktoś wpisał serię błędnych kodów TOTP, wielokrotnie nie zalogował się do panelu albo zgadywał kod parowania,
  aż został unieważniony.

## Nie wykrywa

| Co się dzieje | Co widzi rodzic |
|---|---|
| Usługa zatrzymana na stałe | PC offline w panelu |
| Usunięta albo uszkodzona konfiguracja na PC (`vigil.toml`, `signing.dat`) — usługa nie startuje | PC offline w panelu |
| Inny system uruchomiony z pendrive'a | PC offline w panelu |
| Administrator odczytuje klucz z dysku i wysyła sfałszowane żądania | Nic |

## Sieć

Serwer mówi zwykłym HTTP. Żądania i odpowiedzi są podpisane, ale odpowiedź parowania niesie klucz PC jawnym
tekstem, a hasło do panelu też idzie niezaszyfrowane: kto może czytać ten ruch, może podszyć się pod ten PC albo
pod rodzica.

W sieci, której nie ufasz, postaw przed serwerem reverse proxy z TLS i podaj w `vigil_setup.exe` jego adres
`https://`. Vigil nie idzie za przekierowaniami i nie używa proxy systemowego, a certyfikat musi być zaufany przez
Windows.
