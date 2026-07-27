# Wydzielenie CLI z paczki

## Stan przed zmianą

Elementy CLI:

- `src/MigrationTool.Cli/Program.cs` — punkt wejścia, handlery, `Console`,
  kody `0/2` i odczyt zmiennych CI,
- `src/MigrationTool.Cli/CliArguments.cs` — własny parser argumentów,
- `src/MigrationTool.Cli/MigrationTool.Cli.csproj` — projekt wykonywalny z
  `<OutputType>Exe</OutputType>`,
- `scripts/*.sh` i `scripts/*.ps1` — wrappery wywołujące projekt CLI,
- `.gitlab/validate-migrations.yml` — job wskazujący projekt CLI,
- `build/MigrationValidation.targets` — target MSBuild uruchamiający CLI.

Nie znaleziono zależności `System.CommandLine`, `CommandLineParser`,
`Spectre.Console.Cli`, `PackAsTool` ani `ToolCommandName`. Parser był napisany
ręcznie.

Podział odpowiedzialności:

| Warstwa | Elementy |
|---|---|
| CLI | parser, `Program.cs`, formatowanie komunikatów, kody procesu |
| Logika biznesowa | generator, validator, synchronizer, runtime runner |
| Infrastruktura | Git, system plików, JSON, FluentMigrator, SQL Server |
| API wielokrotnego użycia | requesty, rezultaty i `MigrationWorkspaceService` |

Przepływy przed refaktoryzacją:

```text
new → CliArguments → RunNew → MigrationGenerator.Create
validate → CliArguments → RunValidate → MigrationValidator.ValidateStructure
check → CliArguments → RunCheck → scanner Git → MigrationValidator
sync → CliArguments → RunSync → MigrationSynchronizer.BuildPlan/Apply
```

## Stan po zmianie

Projekt paczki zawiera wyłącznie `MigrationTool.Core`. Warstwa wejściowa została
usunięta z solution, a orkiestrację udostępnia:

```csharp
MigrationWorkspaceService.GenerateAsync(...)
MigrationWorkspaceService.ValidateAsync(...)
MigrationWorkspaceService.CheckAsync(...)
MigrationWorkspaceService.SynchronizeAsync(...)
```

Każda metoda:

- przyjmuje jawny request,
- przyjmuje `CancellationToken`,
- zwraca jawny rezultat,
- nie korzysta z `Console`,
- nie zwraca kodu procesu.

Istniejące klasy niskopoziomowe pozostały publiczne, więc nie zmieniono
niezwiązanego z CLI API.

## Zmiany projektów

Projekt usunięty z repozytorium paczki:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Projekt paczki pozostał biblioteką:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentMigrator.Runner.SqlServer" Version="8.0.1" />
  </ItemGroup>
</Project>
```

Nie dodano `OutputType`, `PackAsTool` ani `ToolCommandName`. Nie zmieniono
`PackageId`, sposobu pakowania ani pipeline'u release.

## CLI w aplikacji

Do repozytorium aplikacji należy skopiować
`examples/Application.Cli` i dodać projekt do jej solution. Przykładowy
`Program.cs` realizuje przepływ:

```text
argumenty → CliArguments → request paczki → MigrationWorkspaceService
→ rezultat → Console i kod procesu
```

`Application.Cli` odpowiada za:

- mapowanie argumentów,
- odczyt `CI_MERGE_REQUEST_TARGET_BRANCH_NAME`,
- formatowanie rezultatów,
- kod `0` przy sukcesie,
- kod `2` przy błędzie lub niepoprawnej walidacji,
- kod `130` przy anulowaniu.

## Użycie przez główną aplikację

```csharp
public sealed class MigrationCheckService
{
    private readonly MigrationWorkspaceService _migrations;

    public MigrationCheckService(MigrationWorkspaceService migrations)
    {
        _migrations = migrations;
    }

    public Task<MigrationsValidationResult> ExecuteAsync(
        CancellationToken cancellationToken)
        => _migrations.ValidateAsync(
            new ValidateMigrationsRequest("Orders"),
            cancellationToken);
}
```

Główna aplikacja nie uruchamia procesu, nie wywołuje `Program.cs` i nie buduje
argumentów tekstowych.

Jeżeli aplikacja korzysta z własnego kontenera DI:

```csharp
services.AddMigrationToolServices(
    repositoryPath: environment.ContentRootPath,
    configurationPath: "migrationtool.json");
```

Rejestracja jest opcjonalna; publiczny serwis można również utworzyć bez DI.

## Testy

Smoke test używa bezpośrednio publicznego API i sprawdza:

- wykrycie migracji starszej od target branch,
- dry-run synchronizacji,
- zastosowanie synchronizacji,
- walidację po synchronizacji,
- generowanie migracji,
- aktualizację `target_version`,
- nieistniejący Git ref,
- anulowanie przez `CancellationToken`.

Mapowanie parsera i kodów wyjścia powinno być testowane w repozytorium aplikacji,
ponieważ należy do `Application.Cli`. Minimalne przypadki:

- brak wymaganej opcji daje kod `2`,
- nieznana komenda daje kod `2`,
- niepoprawna walidacja daje kod `2`,
- sukces daje kod `0`,
- anulowanie daje kod `130`.

## Breaking changes

- usunięty projekt i namespace `MigrationTool.Cli`,
- usunięte wrappery z katalogu `scripts`,
- usunięty package-repo job wskazujący stare CLI,
- konsumenci muszą dodać własny projekt wykonywalny.

Runtime `MigrationToolRunner`, konfiguracja i serwisy biznesowe pozostały
dostępne.

## Kolejność wdrożenia

1. Zbudować i opublikować nową wersję biblioteki.
2. Dodać `Application.Cli` do repozytorium aplikacji.
3. Dodać `PackageReference`.
4. Przenieść joby GitLab na `Application.Cli`.
5. Zweryfikować kody wyjścia i artefakty.
6. Usunąć stare wywołania CLI z repozytoriów aplikacji.
