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
└── src/Application.Cli — parser, Console i kody procesu
        ▲
        │
GitLab Pipeline
```

Projekt `MigrationTool.Core` nie ma `OutputType=Exe`, `PackAsTool` ani
`ToolCommandName`. Jego `PackageId` pozostaje `MigrationTool.Core`, a domyślna
wersja pakietu to `1.0.0`.

## Publiczne API dla operacji repozytoryjnych

Serwis tworzy się dla katalogu znajdującego się w repozytorium Git:

```csharp
var migrations = new MigrationWorkspaceService(
    repositoryPath: repositoryRoot,
    configurationPath: "migrationtool.json");
```

Dostępne operacje:

```csharp
await migrations.GenerateAsync(
    new GenerateMigrationRequest("Orders", "AddCustomerStatus"),
    cancellationToken);

var validation = await migrations.ValidateAsync(
    new ValidateMigrationsRequest("Orders"),
    cancellationToken);

var check = await migrations.CheckAsync(
    new CheckMigrationsRequest("origin/develop", "Orders"),
    cancellationToken);

var synchronization = await migrations.SynchronizeAsync(
    new SynchronizeMigrationsRequest(
        TargetRef: "origin/develop",
        ServiceName: "Orders",
        IsDryRun: false),
    cancellationToken);
```

API:

- przyjmuje jawne requesty,
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
  "services": [
    {
      "name": "Orders",
      "migrationRoot": "src/Orders/Orders.Database.Migrations/Migrations",
      "namespace": "Orders.Database.Migrations",
      "targetVersionFiles": [
        {
          "path": "src/Orders/Orders.Database.Migrations/appsettings.json",
          "propertyName": "target_version"
        }
      ]
    }
  ]
}
```

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

Wariant bez osobnego joba build:

```yaml
script:
  - dotnet restore src/Application.Cli/Application.Cli.csproj
  - >
    dotnet run
    --project src/Application.Cli/Application.Cli.csproj
    --configuration Release
    --no-restore
    --
    check
    --repo "$CI_PROJECT_DIR"
    --target-ref "origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME"
```

Wariant z wcześniejszym buildem uruchamia bezpośrednio:

```bash
dotnet src/Application.Cli/bin/Release/net8.0/Application.Cli.dll check ...
```

Kod różny od zera zwrócony przez `Application.Cli` automatycznie kończy job
błędem. Sekrety należy przechowywać jako chronione i maskowane zmienne GitLab
CI/CD oraz odczytywać w `Program.cs` przez `Environment.GetEnvironmentVariable`;
nie należy przekazywać ich w argumentach procesu.

## Budowanie i pakowanie

```bash
dotnet restore MigrationToolStarter.sln
dotnet build MigrationToolStarter.sln --configuration Release --no-restore
dotnet run --project tests/MigrationTool.SmokeTests --configuration Release --no-build
dotnet pack src/MigrationTool.Core/MigrationTool.Core.csproj \
  --configuration Release \
  --no-build \
  --output artifacts/packages
```

`dotnet pack` tworzy zwykły pakiet biblioteczny. Nie publikuje go i nie zmienia
pipeline'u release.

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
