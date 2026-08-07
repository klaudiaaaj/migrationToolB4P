# Coding Standards — migracje bazodanowe

## Zakres

Standard obowiązuje w serwisach:

- `TaskManager`,
- `Profile`,
- `Eventbus`.

Każdy serwis posiada w folderze `tools` osobny projekt `DatabaseMigrator`:

```text
TaskManager/
└── tools/
    └── DatabaseMigrator/

Profile/
└── tools/
    └── DatabaseMigrator/

Eventbus/
└── tools/
    └── DatabaseMigrator/
```

Projekt `tools/DatabaseMigrator` zawiera:

- wszystkie migracje danego serwisu,
- skrypty PowerShell służące do generowania migracji,
- konfigurację, w tym `TargetVersion`,
- punkt wejścia uruchamiający `Run`,
- referencję do firmowej paczki NuGet migratora.

Migracji jednego serwisu nie umieszczamy w projekcie innego serwisu ani we
wspólnym katalogu poza `tools/DatabaseMigrator`.

## Paczka NuGet migratora

Każdy projekt `DatabaseMigrator` korzysta z paczki:

```text
{NAZWA_PACZKI_NUGET}
```

Paczka zawiera wspólną integrację z FluentMigratorem, walidację historii,
obsługę `up`/`down`, blokadę bazy oraz obsługę błędów.

### Aktualizacja paczki

Po opublikowaniu nowej wersji paczki należy zaktualizować jej wersję we
wszystkich serwisach, których dotyczą zmiany. Nie zakładamy, że projekt
automatycznie pobierze najnowszą wersję.

Jeżeli repozytorium korzysta z Central Package Management, wersję zmieniamy w
`Directory.Packages.props`:

```xml
<ItemGroup>
  <PackageVersion Include="{NAZWA_PACZKI_NUGET}"
                  Version="{NOWA_WERSJA}" />
</ItemGroup>
```

W takim repozytorium `PackageReference` w `.csproj` nie może zawierać wersji:

```xml
<PackageReference Include="{NAZWA_PACZKI_NUGET}" />
```

Niepoprawnie:

```xml
<PackageReference Include="{NAZWA_PACZKI_NUGET}"
                  Version="{NOWA_WERSJA}" />
```

Jeżeli repozytorium nie korzysta z Central Package Management, wersję podajemy
w `PackageReference` zgodnie ze standardem danego repozytorium.

Po aktualizacji należy:

1. Wyczyścić ewentualny lokalny cache paczki, jeżeli użyto ponownie tego samego
   numeru wersji. Preferowane jest zawsze publikowanie nowej wersji.
2. Wykonać `dotnet restore` z firmowym źródłem NuGet/Nexus.
3. Zbudować wszystkie zmienione projekty `DatabaseMigrator`.
4. Uruchomić test poprawnego `up` i test walidacji negatywnej.
5. Sprawdzić, czy artefakt zawiera oczekiwaną wersję paczki.
6. Opisać aktualizację paczki w Merge Requeście.

Polecenia zależą od repozytorium:

```bash
dotnet restore {SOLUTION_OR_PROJECT} --source {NEXUS_SOURCE}
dotnet build {SOLUTION_OR_PROJECT} --configuration Release --no-restore
```

Nie wolno wpisywać danych dostępowych do Nexusa w repozytorium.

## Struktura projektu

Przykładowa struktura pojedynczego serwisu:

```text
{SERVICE_ROOT}/
└── tools/
    └── DatabaseMigrator/
        ├── Migrations/
        │   └── {TIMESTAMP}_{MIGRATION_NAME}/
        │       └── {MIGRATION_NAME}.cs
        ├── scripts/
        │   └── {NAZWA_GENERATORA}.ps1
        ├── appsettings.json
        ├── DatabaseMigrator.csproj
        └── Program.cs
```

Rzeczywista ścieżka skryptu generatora: `{DO UZUPEŁNIENIA}`.

## Tworzenie migracji

Migracji nie tworzymy ręcznie. Należy użyć skryptu PowerShell znajdującego
się w projekcie `tools/DatabaseMigrator` danego serwisu:

```powershell
pwsh {SCIEZKA_DO_SKRYPTU}/{NAZWA_SKRYPTU}.ps1 `
  -Name {NAZWA_MIGRACJI} `
  {POZOSTALE_PARAMETRY}
```

Przykład:

```powershell
pwsh {SCIEZKA_DO_SKRYPTU}/{NAZWA_SKRYPTU}.ps1 `
  -Name AddCustomerStatus
```

Skrypt powinien:

- wygenerować timestamp,
- utworzyć folder migracji,
- utworzyć klasę C#,
- ustawić `[Migration(timestamp)]`,
- zaktualizować `TargetVersion`.

Jeżeli firmowy skrypt ma inne parametry lub nie aktualizuje `TargetVersion`,
należy uzupełnić ten dokument: `{DO UZUPEŁNIENIA}`.

## Format wersji

Wersja jest timestampem zapisanym jako `long` w formacie:

```text
yyyyMMddHHmmssfff
```

Przykład:

```text
20260807142530123
```

Ta sama wartość musi występować w folderze i atrybucie:

```text
Migrations/20260807142530123_AddCustomerStatus
```

```csharp
[Migration(20260807142530123)]
```

oraz jako wersja docelowa artefaktu:

```json
{
  "TargetVersion": "20260807142530123"
}
```

## Nazewnictwo

Nazwa migracji powinna być angielska, zapisana w PascalCase i opisywać zmianę.

Poprawne przykłady:

```text
AddCustomerStatus
CreateOrderHistoryTable
RenameInvoiceNumberColumn
BackfillCustomerType
DropLegacyOrderIndex
```

Niepoprawne przykłady:

```text
Migration1
Changes
Fix
NewTable
Test
```

## Implementacja Up

`Up()` doprowadza bazę do nowego stanu:

```csharp
public override void Up()
{
    Alter.Table("Customer")
        .InSchema("orders")
        .AddColumn("Status")
        .AsString(50)
        .Nullable();
}
```

Zasady:

- zmiana powinna być mała i jednoznaczna,
- należy jawnie wskazywać schemat,
- migracja powinna być deterministyczna,
- nie może zależeć od lokalnego czasu ani maszyny developera,
- operacje na dużych tabelach należy uzgodnić z `{DBA/ARCHITEKTEM}`,
- nie wolno ukrywać błędów SQL.

## Implementacja Down

`Down()` powinno odwracać zmianę wykonaną przez `Up()`:

```csharp
public override void Down()
{
    Delete.Column("Status")
        .FromTable("Customer")
        .InSchema("orders");
}
```

Jeżeli pełne odwrócenie jest niemożliwe, należy opisać ryzyko w kodzie i
Merge Requeście oraz uzgodnić migrację kompensującą albo kopię danych. Nie
wolno implementować pozornie poprawnego `Down()`, który pozostawia bazę w
niespójnym stanie.

## Praca równoległa

Przed utworzeniem migracji należy zaktualizować branch względem rzeczywistego
target brancha Merge Requestu:

```bash
git fetch origin
git rebase origin/{TARGET_BRANCH}
```

Nowa migracja musi mieć wersję większą od każdej migracji znajdującej się w
aktualnym target branchu.

Przykład konfliktu:

```text
Target:  20260807150000000_Hotfix
Feature: 20260806120000000_AddCustomerStatus
```

Migracja feature musi otrzymać nową wersję większą od wersji hotfixu.

Jeżeli migracja nie była wdrożona na współdzielone środowisko, można zmienić
jej folder, `[Migration(...)]` i `TargetVersion`. Jeżeli została już wdrożona,
nie wolno jej renumerować — należy dodać nową migrację naprawczą.

## Niezmienność wdrożonych migracji

Po wdrożeniu migracji nie wolno:

- zmieniać jej numeru,
- usuwać jej klasy,
- używać numeru dla innej migracji,
- zmieniać jej zachowania na części środowisk.

Poprawki wprowadzamy przez kolejną migrację. Obecna walidacja runtime nie
porównuje zawartości `Up()` i `Down()`, dlatego niezmienność musi być
kontrolowana podczas code review.

## TargetVersion

W konfiguracji `DatabaseMigrator` musi znajdować się:

```json
{
  "TargetVersion": "{WERSJA_DOCELOWA}"
}
```

Zasady:

- klucz zapisujemy dokładnie jako `TargetVersion`,
- wartość zapisujemy jako string,
- wartość musi mieścić się w `long`,
- wersja musi istnieć w assembly,
- standardowo jest to najnowsza migracja dostarczana z artefaktem.

## Uruchamianie migratora

Projekt `tools/DatabaseMigrator` uruchamia metodę `Run` z paczki NuGet:

```csharp
await migrationToolRunner.Run(
    new MigrationOptions
    {
        ConnectionString = connectionString,
        SchemaName = "{SCHEMA_NAME}",
        ReportSchemaName = "{REPORT_SCHEMA_NAME}",
        Version = targetVersion,
        Timeout = {TIMEOUT_SECONDS},
        IsDryRun = false
    },
    cancellationToken);
```

| Opcja | Znaczenie |
|---|---|
| `ConnectionString` | Połączenie do docelowej bazy |
| `SchemaName` | Główny schemat serwisu |
| `ReportSchemaName` | Schemat raportowy |
| `Version` | Wersja odczytana z `TargetVersion` |
| `Timeout` | Limit czasu operacji w sekundach |
| `IsDryRun` | Podgląd SQL bez wykonania |

`{POTWIERDZIĆ, CZY IsDryRun JEST UŻYWANY}`.

Migrator wybiera kierunek następująco:

```text
TargetVersion > currentVersion → up
TargetVersion < currentVersion → down
TargetVersion = currentVersion → brak zmian
```

## Walidacja podczas Run

Migrator przerywa pracę, jeżeli:

- `TargetVersion` nie istnieje w assembly,
- `VersionInfo` zawiera migrację nieobecną w assembly,
- istnieje niewykonana migracja starsza od aktualnej wersji bazy,
- `down` wskazuje wersję nieobecną w `VersionInfo`,
- konfiguracja jest niepoprawna,
- nie można połączyć się z bazą,
- nie uda się uzyskać blokady,
- wykonanie SQL zakończy się błędem,
- końcowy stan `VersionInfo` jest niepoprawny.

## Obsługa błędów

Wyjątek z migratora nie może zostać połknięty. Na granicy procesu powinien
znajdować się jeden `try/catch`:

```csharp
try
{
    await runner.Run(options, cancellationToken);
    return 0;
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Database migration failed.");
    return 1;
}
```

Kod `1` musi zakończyć projekt `DatabaseMigrator` i zatrzymać dalsze
uruchamianie aplikacji. Stack trace logujemy raz na granicy procesu.

## Blokada bazy

Migrator korzysta z blokady bazodanowej, aby dwie instancje nie wykonywały
migracji jednocześnie. Blokada powinna być zwalniana również po błędzie.

Implementacja blokady: `{UZUPEŁNIĆ, NP. SQL SERVER sp_getapplock}`.

## Kontrola przed Merge Requestem

Walidacja nie jest jeszcze uruchamiana w pipeline. Do czasu jej wdrożenia
developer musi:

1. Zaktualizować branch względem target brancha.
2. Sprawdzić najnowszą migrację target brancha.
3. Wygenerować migrację skryptem PowerShell z `tools/DatabaseMigrator`.
4. Sprawdzić folder, `[Migration(...)]` i `TargetVersion`.
5. Zbudować `DatabaseMigrator`.
6. Wykonać migrację na lokalnej bazie.
7. Zweryfikować `up`, a jeżeli rollback jest wspierany — również `down`.
8. Ponownie wykonać `up`.

## Checklist developera

- [ ] Użyto generatora PowerShell z projektu danego serwisu.
- [ ] Branch został zaktualizowany względem target brancha.
- [ ] Wersja jest unikalna i nowsza od migracji target brancha.
- [ ] Folder i `[Migration(...)]` mają tę samą wersję.
- [ ] `TargetVersion` jest stringiem i wskazuje istniejącą migrację.
- [ ] `Up()` realizuje wymaganą zmianę.
- [ ] `Down()` poprawnie odwraca zmianę albo opisano ograniczenie.
- [ ] Migracja została sprawdzona na lokalnej bazie.
- [ ] Sprawdzono wpis w `VersionInfo`.
- [ ] Projekt `DatabaseMigrator` się buduje.
- [ ] Projekt korzysta z wymaganej wersji paczki NuGet.
- [ ] MR opisuje wpływ na dane i rollback.

## Checklist reviewera

- [ ] Migracja znajduje się w `tools/DatabaseMigrator` właściwego serwisu.
- [ ] Numer migracji jest poprawny, unikalny i nowszy od target brancha.
- [ ] Nie zmodyfikowano wdrożonej migracji.
- [ ] `TargetVersion` jest poprawne.
- [ ] Jawnie wskazano schemat.
- [ ] `Up()` i `Down()` są spójne.
- [ ] Operacje na danych są bezpieczne.
- [ ] Użyto wymaganej wersji paczki migratora.
- [ ] MR nie modyfikuje ręcznie `VersionInfo`.

## Zabronione praktyki

Nie wolno:

- ręcznie dopisywać rekordów do `VersionInfo`, aby ominąć migrację,
- usuwać wpisów z `VersionInfo` bez procedury naprawczej,
- używać istniejącego numeru dla innej migracji,
- wdrażać starszej migracji po nowszej,
- ignorować kodu zakończenia `1`,
- kontynuować startu aplikacji po błędzie migratora,
- testować walidacji na produkcji,
- tworzyć migracji ręcznie zamiast skryptem PowerShell,
- definiować wersji w `PackageReference`, jeżeli repozytorium korzysta z
  Central Package Management.

## Ograniczenie obecnego rozwiązania

Walidacja jest obecnie wykonywana podczas `Run`, czyli dopiero przy
uruchomieniu `DatabaseMigrator`. Chroni bazę i zatrzymuje aplikację, ale wykrywa
problem później niż pipeline.

Do czasu wdrożenia `check` w pipeline obowiązkowe są aktualizacja brancha,
lokalne uruchomienie migratora oraz kontrola wersji podczas code review.
