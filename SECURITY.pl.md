# Bezpieczeństwo

[English](SECURITY.md) · **Polski**

## Jak zgłosić podatność

Nie zakładaj publicznego issue. Użyj prywatnego zgłoszenia na GitHubie: zakładka **Security** →
**Report a vulnerability**. Zgłoszenie widzi tylko opiekun repo.

Przydadzą się: wersja (tag wydania albo commit), czego dotyczy (serwer, usługa na PC, instalator),
kroki do odtworzenia i jakie uprawnienia na PC dziecka są potrzebne (konto standardowe czy administrator).

To projekt utrzymywany po godzinach: odpowiedź zwykle w ciągu kilku dni, poprawka w kolejnym wydaniu.

## Wspierane wersje

| Wersja | Wspierana |
|---|---|
| Najnowsze wydanie z zakładki **Releases** | Tak |
| Starsze wydanie 1.x | Nie — zaktualizuj; serwer 1.x działa z każdym PC z 1.x |
| 0.x | Nie |

Poprawki bezpieczeństwa w tym, co wydanie zawiera — środowisko .NET 10 w `vigil_setup.exe`, obraz bazowy
serwera — przychodzą jako wydania poprawkowe.

## Co jest podatnością

Vigil działa w dwóch układach ([model zagrożeń](docs/threat-model.pl.md)): dziecko na własnym **koncie
standardowym** (zalecany) albo jedno konto, wspólne dla dziecka i rodzica, które jest **administratorem**. Na samym
PC nic nie jest odporne na administratora, więc w tym układzie o tym, czy coś jest błędem, decyduje grupa, do
której należy mechanizm:

| Grupa | Przykład | Zgłoś, jeśli… |
|---|---|---|
| **Chroni** (stan na serwerze) | limity, harmonogram, blokada ręczna, parowanie, TOTP, logowanie do panelu | da się to obejść **nawet z PC administratora** — np. zmienić limit, przejąć panel, użyć kodu TOTP dwa razy, sparować komputer bez kodu |
| **Utrudnia** | podpisane żądania, fail-closed, DACL usługi i procesu UI, polityki rejestru | da się to obejść **bez uprawnień administratora** (proces niepodniesiony, konto standardowe) |
| **Wykrywa** | alerty w Dzienniku | administrator wyłącza Vigila tak, że w panelu **nie pojawia się alert**, a model zagrożeń nie wymienia tego w „Nie wykrywa” |

**Na koncie standardowym** zgłoś wszystko, co pozwala dziecku obejść Vigila z tego konta: zatrzymać albo zmienić
usługę, jej pliki lub zadanie strażnika, odczytać klucz podpisujący albo sekret TOTP, zamknąć ekran blokady albo
korzystać z PC bez liczenia czasu — inaczej niż hasłem rodzica, luką Windows dającą uprawnienia administratora
(tę zgłoś Microsoftowi) albo przez fizyczny dostęp (niżej).

Podatnością jest też wszystko, co pozwala osobie w tej samej sieci (bez dostępu do PC dziecka) wpłynąć na
serwer: odczytać sekrety, zmienić ustawienia, podszyć się pod komputer dziecka — inaczej niż przez odczyt albo
zmianę ruchu po zwykłym HTTP (niżej).

**Nie są podatnością** (opisane w modelu zagrożeń jako znane ograniczenia): obejście przez administratora mechanizmu
z grupy „Utrudnia”, odczyt przez administratora sekretów z dysku PC (klucz podpisujący, sekret TOTP do
odblokowania offline), uruchomienie innego systemu z pendrive'a, środowisko odzyskiwania Windows z hasłem
administratora, reset komputera, korzystanie z odblokowanej sesji rodzica, trwałe zatrzymanie usługi widoczne w panelu
jako komputer offline oraz wszystko, co wymaga odczytu albo zmiany ruchu serwera działającego bez TLS — mówi
on zwykłym HTTP, odpowiedź parowania niesie klucz PC, a logowanie do panelu hasło („Sieć” w
[modelu zagrożeń](docs/threat-model.pl.md#sieć); postaw przed serwerem reverse proxy z TLS).
