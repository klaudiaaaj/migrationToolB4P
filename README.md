# MigrationTool.Core

Biblioteka NuGet dla .NET 8 zawierająca:

- publiczne API do generowania, walidowania, porównywania i synchronizowania
  migracji w repozytorium,
- runtime `MigrationToolRunner.Run(MigrationOptions)`, który bezpiecznie wykonuje
  `up` albo `down`,
- integrację z FluentMigratorem i SQL Server.

Pakiet nie zawiera CLI, parsera argumentów ani punktu wejścia `Program.cs`.

## Architektura

```text
Repozytorium paczki NuGet
└── MigrationTool.Core — biblioteka i publiczne API
        ▲
        │ PackageReference
Repozytorium aplikacji
├── src/Application
├── src/Application.Database.Migrations
└── src/Application.Cli — parser, Console i kody procesu
        ▲
        │
GitLab Pipeline
```

Projekt `MigrationTool.Core` nie ma `OutputType=Exe`, `PackAsTool` ani
`ToolCommandName`. Jego `PackageId` pozostaje `MigrationTool.Core`, a wersja
uproszczonego API jednego projektu migracyjnego to `2.0.0`.

## Publiczne API dla operacji repozytoryjnych

Serwis tworzy się dla katalogu znajdującego się w repozytorium Git:

```csharp
var migrations = new MigrationWorkspaceService(
    repositoryPath: repositoryRoot,
    configurationPath: "migrationtool.json");
```

Dostępne operacje:

```csharp
await migrations.GenerateAsync("AddCustomerStatus", cancellationToken);

var validation = await migrations.ValidateAsync(cancellationToken);

var check = await migrations.CheckAsync("origin/develop", cancellationToken);

var synchronization = await migrations.SynchronizeAsync(
    targetRef: "origin/develop",
    isDryRun: false,
    cancellationToken);
```

API:

- zwraca jawne rezultaty,
- respektuje `CancellationToken`,
- nie wypisuje niczego przez `Console`,
- nie zwraca kodów procesu,
- zgłasza wyjątki dla błędów infrastruktury i niepoprawnych argumentów.

Opcjonalna rejestracja w kontenerze aplikacji:

```csharp
services.AddMigrationToolServices(
    repositoryPath: environment.ContentRootPath,
    configurationPath: "migrationtool.json");
```

Operacje plikowe i Git pozostają synchroniczne wewnątrz obecnej implementacji,
dlatego metody kończą się `Async`, ale nie przenoszą pracy sztucznie do
`Task.Run`. Token anulowania jest sprawdzany przed każdym etapem operacji.

## Konfiguracja repozytorium

```json
{
  "projectRoot": "src/Orders.Database.Migrations",
  "namespace": "Orders.Database.Migrations"
}
```

Konfiguracja opisuje dokładnie jeden projekt migracyjny. `projectRoot` jest
ścieżką względną wobec katalogu głównego repozytorium. Biblioteka przyjmuje
stały układ projektu: migracje w `Migrations/` i wersję w `appsettings.json`.
Nazwa właściwości wersji jest stała: `TargetVersion`.

Kompletny przykład znajduje się w
[migrationtool.example.json](migrationtool.example.json).

## Użycie runtime bez CLI

Główna aplikacja wywołuje bibliotekę bez uruchamiania procesu:

```csharp
public sealed class DatabaseMigrationService
{
    private readonly MigrationToolRunner _runner =
        new(typeof(DatabaseMigrationService).Assembly);

    public Task<MigrationRunResult> MigrateAsync(
        string connectionString,
        long version,
        CancellationToken cancellationToken)
        => _runner.Run(
            new MigrationOptions
            {
                ConnectionString = connectionString,
                SchemaName = "orders",
                ReportSchemaName = "orders_reports",
                Version = version,
                Timeout = 120,
                IsDryRun = false
            },
            cancellationToken);
}
```

`Run` pobiera listę migracji z FluentMigratora, odczytuje całe `VersionInfo`,
sprawdza spójność historii i wybiera:

```text
target > current → MigrateUp(target)
target < current → MigrateDown(target)
target = current → brak operacji
```

Aplikacja nie powinna uruchamiać CLI, wywoływać `Program.cs`, budować tablicy
argumentów ani parsować własnych danych parserem CLI.

## Projekt CLI w aplikacji

Gotowy wzorzec znajduje się w
[examples/Application.Cli](examples/Application.Cli/README.md). Zawiera:

- `Application.Cli.csproj` z `OutputType=Exe` i `PackageReference`,
- parser dotychczasowych argumentów,
- `Program.cs` mapujący `new`, `validate`, `check` i `sync` na publiczne API,
- obsługę `Ctrl+C`, błędów oraz kodów `0`, `2` i `130`,
- dwa warianty GitLab CI,
- opcjonalny target MSBuild.

Po skopiowaniu do aplikacji:

```bash
dotnet run \
  --project src/Application.Cli/Application.Cli.csproj \
  -- \
  check \
  --target-ref origin/develop
```

## GitLab CI

Pełny przykład jest w
[examples/Application.Cli/gitlab-ci.yml](examples/Application.Cli/gitlab-ci.yml).

W ustawieniach GitLaba należy włączyć `Enable merged results pipelines`.
Job MR uruchamia `scripts/migrationtool.sh check`. Skrypt nie wymaga .NET:
korzysta z Gita oraz podstawowych narzędzi POSIX. Wykonuje walidację struktury,
sprawdza `TargetVersion`, porównuje numery i nazwy migracji z target branchem
i zwraca kod różny od zera, gdy MR nie jest bezpieczny. Zawartość metod
`Up()` i `Down()` nie jest porównywana.

`sync` nadal jest uruchamiany lokalnie przez aplikację CLI, ponieważ to komenda
modyfikująca pliki developera. Kod różny od zera zwrócony przez skrypt
automatycznie kończy job błędem.

## Budowanie i pakowanie

```bash
dotnet restore MigrationToolStarter.sln
dotnet build MigrationToolStarter.sln --configuration Release --no-restore
dotnet test tests/MigrationTool.Core.Tests/MigrationTool.Core.Tests.csproj \
  --configuration Release \
  --no-build \
  --no-restore
dotnet run --project tests/MigrationTool.SmokeTests --configuration Release --no-build
sh tests/migrationtool-check-tests.sh
dotnet pack src/MigrationTool.Core/MigrationTool.Core.csproj \
  --configuration Release \
  --no-build \
  --output artifacts/packages
```

`dotnet pack` tworzy zwykły pakiet biblioteczny. Nie publikuje go i nie zmienia
pipeline'u release.

## Testy NUnit

Projekt
[MigrationTool.Core.Tests](tests/MigrationTool.Core.Tests/MigrationTool.Core.Tests.csproj)
zawiera unit testy biblioteki. Testy sprawdzają:

- wybór `up`, `down` albo braku operacji na podstawie wersji,
- blokowanie docelowej wersji, której nie ma w assembly,
- blokowanie migracji obecnych w `VersionInfo`, ale nieobecnych w assembly,
- wykrywanie pominiętej migracji poniżej aktualnej wersji bazy,
- poprawny i niepoprawny stan po `up` lub `down`,
- walidację `MigrationOptions`,
- odczyt płaskiego `migrationtool.json` dla jednego projektu,
- walidację zmian względem target brancha,
- renumerowanie migracji przez `sync`,
- blokowanie duplikatów i modyfikacji istniejących migracji.

Metody zawierające czystą logikę bezpieczeństwa mają dostęp `internal` i są
udostępnione wyłącznie assembly testowemu przez `InternalsVisibleTo`. Nie
powiększa to publicznego API paczki NuGet.

Unit testy celowo nie otwierają połączenia z bazą. Zachowanie
`SqlConnection`, `sp_getapplock` i rzeczywiste wykonanie instrukcji
FluentMigratora należy sprawdzać osobnym testem integracyjnym uruchamianym na
tymczasowej bazie SQL Server.

## Kompatybilność i migracja

Publiczne API runtime oraz niskopoziomowe serwisy biblioteki pozostały dostępne.
Breaking changes dotyczą wyłącznie dawnej warstwy CLI:

- usunięto projekt `MigrationTool.Cli` z solution,
- usunięto znajdujący się w repozytorium `Program.cs` i parser,
- usunięto skrypty uruchamiające lokalny projekt CLI,
- pipeline aplikacji musi wskazać własny `Application.Cli.csproj`.

Zalecana kolejność migracji:

1. opublikować nową wersję `MigrationTool.Core`,
2. dodać `Application.Cli` do repozytorium aplikacji,
3. odtworzyć komendy przy użyciu publicznego `MigrationWorkspaceService`,
4. przełączyć joby GitLab na projekt aplikacji,
5. usunąć stare wywołania CLI z repozytoriów konsumujących,
6. dopiero wtedy zaktualizować `PackageReference`.
