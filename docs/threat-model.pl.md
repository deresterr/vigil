# Model zagrożeń

[English](threat-model.md) · **Polski**

Vigil działa w jednym z dwóch układów, wybieranym przy instalacji:

- **Zalecany: dziecko ma własne konto standardowe**, a rodzic konto administratora z hasłem, którego dziecko nie
  zna. Liczone i blokowane są tylko sesje dziecka. Sam Windows odmawia użytkownikowi standardowemu tego, co
  wyłączyłoby Vigila: zatrzymania usługi, podmiany jej plików, odczytu sekretów, zmiany zegara. Konto dziecka może
  założyć instalator.
- **Wspierany: jedno konto administratora**, wspólne dla dziecka i rodzica. Administrator może uruchomić kod jako
  SYSTEM, odczytać każdy plik i wyłączyć każdą usługę, więc na samym PC nic nie jest odporne na manipulację:
  to, co ważne, jest na serwerze, resztę trudno odkręcić, a rodzic widzi próby.

Każdy mechanizm należy do jednej z trzech grup:

| Grupa | Co to znaczy |
|---|---|
| **Chroni** | Działa przeciw dziecku: stan jest na serwerze albo Windows odmawia kontu dziecka. |
| **Utrudnia** | Zatrzymuje program bez uprawnień administratora i przypadkowe próby. Administrator to obejdzie. |
| **Wykrywa** | Pojawia się jako alert w Dzienniku, do potwierdzenia przez rodzica. |

Co w tym modelu jest podatnością: [`SECURITY.pl.md`](../SECURITY.pl.md).

## Według układu

| Mechanizm | Konto standardowe | Jedno konto administratora |
|---|---|---|
| Limity, cisza nocna, blokada ręczna i licznik czasu są na serwerze. Z PC nie da się ich zmienić, można najwyżej podsunąć fałszywe dane | **Chroni** | **Chroni** |
| Parowanie jednorazowym kodem; każde parowanie wydaje nowy klucz, a poprzedni PC traci dostęp | **Chroni** | **Chroni** |
| Kod TOTP działa raz; po błędnych kodach odblokowanie wstrzymuje się na kilka minut. Liczba prób logowania do panelu jest ograniczona | **Chroni** | **Chroni** |
| Usługi nie da się zatrzymać, wyłączyć ani usunąć; jej plików nie da się podmienić; zadania strażnika nie da się zmienić | **Chroni** — wymaga uprawnień administratora | **Utrudnia** — zwykłe polecenie zawodzi, resztę strażnik cofa co 5 minut, każda naprawa to alert |
| Żądania są podpisane kluczem, który odczyta tylko SYSTEM i administrator; tak samo sekret TOTP do odblokowania offline | **Chroni** | **Utrudnia** — administrator odczyta oba |
| Brak połączenia z serwerem oznacza blokadę (fail-closed) | **Chroni** — dziecko nie zatrzyma usługi ani nie podrobi odpowiedzi | **Utrudnia** |
| Użytkownik nie zamknie procesu ekranu blokady; wielokrotne zamykanie wylogowuje sesję | **Chroni** | **Utrudnia** |
| Zmiana zegara (usługa z niego nie korzysta, serwer zapisuje różnicę w Dzienniku) | **Chroni** — wymaga uprawnień administratora | **Utrudnia** |
| Ekran blokady zakrywa wszystkie monitory i blokuje skróty (Win, Alt+Tab, Alt+F4); Menedżer zadań i zmiana hasła są wyłączone na koncie dziecka | **Utrudnia** | **Utrudnia** — także dla rodzica, bo to to samo konto |
| Usługa działa też w trybie awaryjnym; menu F8 jest wyłączone | **Chroni** — konfiguracja rozruchu wymaga uprawnień administratora | **Utrudnia** |

Konto standardowe trzyma tak długo, jak Windows: aktualizuj go, żeby żadna znana luka nie dała użytkownikowi
standardowemu uprawnień administratora.

## Konto rodzica

W układzie z kontem standardowym to konto administratora rodzica zatrzymuje dziecko:

- Każde konto administratora na PC potrzebuje hasła, którego dziecko nie zna. Instalator każe ustawić je kontu,
  z którego jest uruchomiony, jeśli go nie ma; inne konta administratora sprawdź sam.
- Automatyczne logowanie jest wyłączane: inaczej Windows startowałby w sesji rodzica, której nikt nie liczy.
- Sesja rodzica nigdy nie jest liczona ani blokowana, także bez serwera. Wyloguj się albo zablokuj ją (Win+L),
  zanim odejdziesz od PC: odblokowana sesja rodzica to PC bez limitów.

## Fizyczny dostęp

- **Środowisko odzyskiwania Windows** (Shift + Uruchom ponownie na ekranie logowania) pyta o hasło administratora
  przy wierszu polecenia, przywracaniu systemu i resetowaniu komputera. Bez niego działają tylko ustawienia
  uruchamiania, a Vigil działa też w trybie awaryjnym (bez sieci zostaje zablokowany: nie ma serwera). W układzie
  z jednym kontem dziecko zna to hasło.
- **Inny system albo instalator Windows z pendrive'a** o nic nie pyta: może usunąć folder Vigila, tak jak robi to
  [ścieżka awaryjna](installer.md#emergency-windows-does-not-start-or-vigil-cannot-be-removed-normally), albo
  zresetować komputer. Żaden układ tego nie widzi.
- Przeciw temu: hasło firmware'u (BIOS/UEFI), wyłączony rozruch z USB i **szyfrowanie urządzenia** tam, gdzie PC
  je ma (Windows 10/11 Home na większości nowszego sprzętu; BitLocker w Pro). Zaszyfrowany dysk poza Windowsem
  pyta o klucz odzyskiwania, więc trzymaj go z dala od dziecka — i pod ręką na ścieżkę awaryjną.

## Wykrywa

Alert w Dzienniku, do potwierdzenia przez rodzica, gdy:

- usługa została zatrzymana albo uruchomiona ponownie bez restartu systemu,
- usługa zamarła albo została wstrzymana przy działającym komputerze,
- ekran blokady był wielokrotnie zamykany,
- strażnik coś naprawił (wyłączoną usługę, podmieniony plik, …),
- konto dziecka, dotąd standardowe, dostało uprawnienia administratora (Ustawienia pokazują też, kiedy nim jest),
- ktoś wpisał serię błędnych kodów TOTP, wielokrotnie nie zalogował się do panelu albo zgadywał kod parowania,
  aż został unieważniony.

Alert strażnika da się podrobić: każde konto może pisać do dziennika Aplikacja Windows, także pod nazwą
strażnika. Daje to fałszywy alert, nigdy ukryty.

## Nie wykrywa

| Co się dzieje | Co widzi rodzic |
|---|---|
| Usługa zatrzymana na stałe (administrator) | PC offline w panelu |
| Usunięta albo uszkodzona konfiguracja na PC (`vigil.toml`, `signing.dat`) — usługa nie startuje | PC offline w panelu |
| Inny system uruchomiony z pendrive'a albo zresetowany komputer | PC offline w panelu |
| Administrator odczytuje klucz z dysku i wysyła sfałszowane żądania | Nic |
| Dziecko korzysta z odblokowanej sesji rodzica | Nic: ta sesja nie jest liczona |

## Sieć

Serwer mówi zwykłym HTTP. Żądania i odpowiedzi są podpisane, ale odpowiedź parowania niesie klucz PC jawnym
tekstem, a hasło do panelu też idzie niezaszyfrowane: kto może czytać ten ruch, może podszyć się pod ten PC albo
pod rodzica.

W sieci, której nie ufasz, postaw przed serwerem reverse proxy z TLS i podaj w `vigil_setup.exe` jego adres
`https://`. Vigil nie idzie za przekierowaniami i nie używa proxy systemowego, a certyfikat musi być zaufany przez
Windows.

Ustaw w `.env` `FORWARDED_ALLOW_IPS` na adres, z którego proxy łączy się z Vigil (adres kontenera proxy albo sieci
Dockera, adres w LAN, gdy proxy działa na innej maszynie; można też podać sieć, np. `172.18.0.0/16`). Inaczej każde
żądanie wygląda, jakby przyszło od proxy: limit logowania do panelu, który liczy błędne hasła dla każdego adresu,
staje się jednym limitem dla wszystkich — dziecko wpisujące złe hasła blokuje także rodzica — a ciasteczko sesji nie
dostaje flagi Secure. Nigdy `*`: każdy mógłby wtedy wysłać zmyślone `X-Forwarded-For` i obejść limit logowania.
