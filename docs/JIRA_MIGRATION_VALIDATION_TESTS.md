# Testy manualne walidacji migracji bazodanowych

## Cel

Celem testów jest sprawdzenie, czy migrator:

- poprawnie wykonuje migracje `up` i `down`,
- wykrywa niespójną historię migracji,
- nie pomija starszej, niewykonanej migracji,
- kończy proces aplikacji kodem `1`, jeżeli walidacja lub migracja zakończy się błędem,
- nie uruchamia aplikacji po błędzie migracji,
- nie raportuje nieudanego wdrożenia jako sukcesu.

Walidacja nie jest obecnie wykonywana w pipeline. Testy należy przeprowadzić
podczas uruchamiania projektu `tools/DatabaseMigrator` na dedykowanej bazie
testowej.

## Serwisy objęte testami

Ten sam zestaw testów należy wykonać dla:

- `TaskManager/tools/DatabaseMigrator`,
- `Profile/tools/DatabaseMigrator`,
- `Eventbus/tools/DatabaseMigrator`.

Jeżeli wszystkie trzy projekty korzystają z tej samej wersji paczki i mają
identyczną integrację, pełny test negatywny można wykonać w jednym serwisie,
a w pozostałych wykonać test poprawnego `up`, `down` i obsługi błędu.
Zakres regresji zatwierdza `{QA LEAD/TECH LEAD}`.

## Warunki wstępne

Tester potrzebuje:

- dedykowanej, nietrwałej bazy testowej,
- możliwości odczytu tabeli `VersionInfo`,
- możliwości uruchomienia projektu `DatabaseMigrator`,
- dostępu do logów procesu i jego kodu zakończenia,
- możliwości zmiany `TargetVersion`,
- możliwości przygotowania migracji firmowym skryptem PowerShell,
- nazwy schematu testowanego serwisu: `{SCHEMA_NAME}`,
- komendy uruchamiającej migrator: `{KOMENDA_URUCHOMIENIA}`.

Testów nie wolno wykonywać na bazie produkcyjnej ani na współdzielonym
środowisku bez zgody osoby odpowiedzialnej za to środowisko.

## Przykładowe wersje

```text
A = 20260801090000000
B = 20260801100000000
C = 20260802120000000
X = 20260803130000000
```

`A`, `B` i `C` oznaczają kolejne migracje. `X` oznacza wersję nieistniejącą
w assembly. Rzeczywiste wartości mogą być inne.

## Dowody wymagane dla każdego testu

Do wyniku testu należy dołączyć:

- log migratora,
- kod zakończenia procesu,
- zawartość `VersionInfo` przed uruchomieniem,
- zawartość `VersionInfo` po uruchomieniu,
- informację, czy aplikacja została uruchomiona,
- dla sukcesu: potwierdzenie oczekiwanej zmiany w schemacie,
- dla błędu: potwierdzenie braku nieoczekiwanych zmian w bazie.

## Scenariusze testowe

### TC01 — poprawne wykonanie UP

**Stan początkowy:**

- assembly zawiera migracje `A` i `B`,
- `VersionInfo` zawiera tylko `A`,
- `TargetVersion` wskazuje `B`.

```json
{
  "TargetVersion": "20260801100000000"
}
```

**Kroki:**

1. Uruchomić `DatabaseMigrator`.
2. Sprawdzić log i kod zakończenia.
3. Odczytać `VersionInfo`.
4. Zweryfikować zmianę wykonaną przez migrację `B`.

**Oczekiwany rezultat:**

- migrator wybiera kierunek `up`,
- migracja `B` zostaje wykonana,
- `VersionInfo` zawiera `A` i `B`,
- proces kończy się kodem `0`,
- aplikacja może się uruchomić.

### TC02 — baza ma już wersję docelową

**Stan początkowy:** assembly i `VersionInfo` zawierają `A` i `B`, a
`TargetVersion` wskazuje `B`.

**Oczekiwany rezultat:**

- migrator nie wykonuje `up` ani `down`,
- `VersionInfo` pozostaje bez zmian,
- proces kończy się kodem `0`.

### TC03 — poprawne wykonanie DOWN

**Stan początkowy:**

- assembly i `VersionInfo` zawierają `A` i `B`,
- `TargetVersion` wskazuje `A`.

**Oczekiwany rezultat:**

- migrator wybiera kierunek `down`,
- wykonuje `Down()` migracji `B`,
- rekord `B` znika z `VersionInfo`,
- `A` pozostaje w `VersionInfo`,
- proces kończy się kodem `0`.

### TC04 — starsza pominięta migracja

To najważniejszy scenariusz odtwarzający problem równoległych branchy.

**Stan początkowy:**

- baza ma wykonane migracje `A` i `C`,
- `VersionInfo` nie zawiera `B`,
- assembly zawiera `A`, `B` i `C`,
- `B < C`,
- `TargetVersion` wskazuje `C`.

Stan można przygotować, wdrażając najpierw `A` i `C`, a następnie
dodając do kodu migrację `B` z wcześniejszą wersją. Nie należy dopisywać
`B` ręcznie do `VersionInfo`.

**Oczekiwany rezultat:**

- migrator wykrywa pominiętą migrację `B`,
- nie wykonuje `B` po migracji `C`,
- nie wykonuje kolejnych instrukcji SQL,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się,
- `VersionInfo` pozostaje bez zmian.

Oczekiwany komunikat powinien wskazywać numer pominiętej migracji, np.:

```text
Wykryto pominięte migracje starsze od aktualnej wersji bazy: {B}
```

### TC05 — wpis w VersionInfo nieobecny w assembly

**Stan początkowy:** `VersionInfo` zawiera `A` i `B`, natomiast projekt został
testowo zbudowany bez klasy migracji `B`.

**Oczekiwany rezultat:**

- migrator wykrywa migrację obecną w bazie, ale nieobecną w artefakcie,
- nie wykonuje `up` ani `down`,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się,
- `VersionInfo` pozostaje bez zmian.

### TC06 — TargetVersion nie istnieje w assembly

Ustawić nieistniejącą wersję:

```json
{
  "TargetVersion": "20260803130000000"
}
```

**Oczekiwany rezultat:**

- migrator zgłasza, że wersja docelowa nie istnieje w assembly,
- nie wykonuje SQL migracyjnego,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się.

### TC07 — dwie migracje z tą samą wersją

Przygotować dwie klasy oznaczone tym samym numerem:

```csharp
[Migration(20260801100000000)]
public class MigrationA : Migration
{
    // ...
}

[Migration(20260801100000000)]
public class MigrationB : Migration
{
    // ...
}
```

**Oczekiwany rezultat:**

- FluentMigrator lub walidacja odrzuca duplikat,
- konfliktowe migracje nie zostają wykonane,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się.

### TC08 — błędny lub brakujący connection string

**Kroki:** usunąć connection string albo ustawić niepoprawną wartość,
a następnie uruchomić migrator.

**Oczekiwany rezultat:**

- błąd połączenia jest widoczny w logach,
- wyjątek nie jest połykany,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się,
- uruchomienie nie jest raportowane jako sukces.

### TC09 — błąd SQL podczas migracji

Dodać testową migrację zawierającą celowo niepoprawny SQL:

```csharp
public override void Up()
{
    Execute.Sql("THIS IS NOT VALID SQL");
}
```

**Oczekiwany rezultat:**

- wyjątek jest widoczny w logach,
- proces kończy się kodem `1`,
- aplikacja nie uruchamia się,
- błędna migracja nie jest zapisana jako wykonana w `VersionInfo`,
- stan transakcji jest zgodny z `{USTAWIENIA TRANSAKCJI FLUENTMIGRATORA}`.

### TC10 — TargetVersion zapisany jako string

```json
{
  "TargetVersion": "20260801100000000"
}
```

**Oczekiwany rezultat:** konfiguracja zostaje odczytana, wartość zostaje
przekazana do migratora jako `long` i nie występuje błąd parsowania.

### TC11 — zwolnienie blokady po błędzie

1. Uruchomić migrację kończącą się błędem walidacji albo SQL.
2. Poprawić przyczynę błędu.
3. Uruchomić migrator ponownie.

**Oczekiwany rezultat:**

- pierwsze uruchomienie kończy się kodem `1`,
- blokada zostaje zwolniona,
- drugie uruchomienie nie czeka do timeoutu na starą blokadę,
- poprawiona migracja może zostać wykonana.

## Ograniczenie obecnej walidacji

Nazwa folderu nie istnieje w skompilowanym assembly. Jeżeli `Run` sprawdza
wyłącznie migracje załadowane przez FluentMigratora, zmiana nazwy folderu nie
musi zostać wykryta podczas uruchomienia.

Walidacje takie jak pusty folder, niepoprawna nazwa folderu lub rozbieżność
między folderem i `[Migration(...)]` wymagają walidatora kodu źródłowego w
pipeline. Pipeline nie został jeszcze wdrożony.
