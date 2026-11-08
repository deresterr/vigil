# Bezpieczeństwo

*English: reports in English are welcome — use the private report described below.*

## Jak zgłosić podatność

Nie zakładaj publicznego issue. Użyj prywatnego zgłoszenia na GitHubie: zakładka **Security** →
**Report a vulnerability**. Zgłoszenie widzi tylko opiekun repo.

Przydadzą się: wersja (tag wydania albo commit), czego dotyczy (serwer, usługa na PC, instalator),
kroki do odtworzenia i jakie uprawnienia na PC dziecka są potrzebne (konto standardowe czy administrator).

To projekt utrzymywany po godzinach: odpowiedź zwykle w ciągu kilku dni, poprawka w kolejnym wydaniu.
Wspierana jest tylko najnowsza wersja z zakładki **Releases**.

## Co jest podatnością

Vigil zakłada, że dziecko **może mieć konto administratora** ([Model zagrożeń](README.md#model-zagrożeń)).
Na samym PC nic nie jest odporne na administratora, więc o tym, czy coś jest błędem, decyduje grupa, do której
należy mechanizm:

| Grupa | Przykład | Zgłoś, jeśli… |
|---|---|---|
| **Chroni** (stan na serwerze) | limity, harmonogram, blokada ręczna, parowanie, TOTP, logowanie do panelu | da się to obejść **nawet z PC administratora** — np. zmienić limit, przejąć panel, użyć kodu TOTP dwa razy, sparować komputer bez kodu |
| **Utrudnia** | podpisane żądania, fail-closed, DACL usługi i procesu UI, polityki rejestru | da się to obejść **bez uprawnień administratora** (proces niepodniesiony, konto standardowe) |
| **Wykrywa** | alerty w Dzienniku | administrator wyłącza Vigila tak, że w panelu **nie pojawia się alert**, a README nie wymienia tego w „Nie wykrywa” |

Podatnością jest też wszystko, co pozwala osobie w tej samej sieci (bez dostępu do PC dziecka) wpłynąć na
serwer: odczytać sekrety, zmienić ustawienia, podszyć się pod komputer dziecka.

**Nie są podatnością** (opisane w README jako znane ograniczenia): obejście przez administratora mechanizmu
z grupy „Utrudnia”, odczyt przez administratora sekretów z dysku PC (klucz podpisujący, sekret TOTP do
odblokowania offline), uruchomienie innego systemu z pendrive'a, trwałe zatrzymanie usługi widoczne w panelu
jako komputer offline.
