# MigrationTool Starter dla .NET 8

Gotowy szkielet rozszerzenia istniejącego `MigrationToolPackage` o:

- `new` — tworzenie folderu, klasy `[Migration(...)]` i aktualizacja `target_version`,
- `validate` — lokalna walidacja struktury,
- `check` — porównanie z rzeczywistym branchem docelowym Merge Requesta,
- `sync` — automatyczne przenumerowanie nowych migracji z brancha,
- `plan` — prosty plan na podstawie wersji z `VersionInfo`,
- runtime guard przed `up` i weryfikację po `up`.

Kod celowo nie zależy od konkretnej wersji pakietu FluentMigrator w części Core. Runtime guard odkrywa atrybuty `FluentMigrator.MigrationAttribute` refleksją. Dzięki temu można go wpiąć do istniejącego wewnętrznego pakietu bez zastępowania obecnego runnera.

## 1. Umieszczenie w repozytorium

Proponowana lokalizacja:

```text
repo/
├── migrationtool.json
├── tools/
│   └── MigrationTool/
│       ├── src/
│       ├── scripts/
│       └── ...
└── src/
    ├── Orders/
    ├── Billing/
    └── Identity/
```

Skopiuj katalog startera jako `tools/MigrationTool`, a plik `migrationtool.example.json` jako `migrationtool.json` w katalogu głównym repozytorium. Następnie popraw ścieżki i namespace'y.

## 2. Konfiguracja

Przykład:

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
      ],
      "versionInfo": {
        "schema": "dbo",
        "table": "VersionInfo",
        "versionColumn": "Version",
        "descriptionColumn": "Description",
        "appliedOnColumn": "AppliedOn",
        "provider": "SqlServer",
        "failWhenDatabaseAhead": true,
        "treatMissingVersionInfoAsEmpty": true,
        "requireAppliedVersionsInAssembly": false
      }
    }
  ]
}
```

Obsługiwane wartości `provider`:

- `SqlServer`,
- `PostgreSql`,
- `MySql`,
- `Sqlite`.

`target_version` może być liczbą albo tekstem zawierającym cyfry. Wskazana właściwość powinna występować w pliku dokładnie raz.

## 3. Codzienna praca developera

### Nowa migracja

```bash
dotnet run --project tools/MigrationTool/src/MigrationTool.Cli -- \
  new --service Orders --name AddCustomerStatus
```

Narzędzie:

1. generuje 17-cyfrowy timestamp UTC z milisekundami,
2. tworzy folder `[timestamp]_AddCustomerStatus`,
3. tworzy klasę z `[Migration(timestamp)]`,
4. aktualizuje wszystkie skonfigurowane pliki `target_version`.

### Lokalna walidacja

```bash
dotnet run --project tools/MigrationTool/src/MigrationTool.Cli -- validate
```

Sprawdzane są:

- duplikaty wersji,
- zgodność folderu z atrybutem,
- obecność klasy migracji,
- zgodność `target_version` z najwyższą migracją.

### Sprawdzenie względem target brancha

```bash
git fetch origin +develop:refs/remotes/origin/develop

dotnet run --project tools/MigrationTool/src/MigrationTool.Cli -- \
  check --target-ref origin/develop
```

Dla MR target branch nie jest zakodowany na stałe. GitLab przekazuje go przez `CI_MERGE_REQUEST_TARGET_BRANCH_NAME`.

### Automatyczna naprawa kolejności

Najpierw podgląd:

```bash
dotnet run --project tools/MigrationTool/src/MigrationTool.Cli -- \
  sync --target-ref origin/develop --dry-run
```

Następnie zmiana plików:

```bash
./tools/MigrationTool/scripts/sync-with-target.sh develop

git diff
git add .
git commit -m "Synchronize migration versions"
```

`sync` przenumerowuje wszystkie migracje występujące tylko w source branchu, gdy przynajmniej jedna z nich jest starsza od headu target brancha. Zachowuje ich wzajemną kolejność.

Narzędzie nie przenumeruje automatycznie migracji, która ma tę samą nazwę i wersję co migracja w target branchu, ale inną zawartość. Taki przypadek jest traktowany jako próba zmiany istniejącej historii i wymaga rebase'u lub ręcznej analizy.

## 4. GitLab CI

Dołącz `.gitlab/validate-migrations.yml` do głównego pipeline'u:

```yaml
include:
  - local: 'tools/MigrationTool/.gitlab/validate-migrations.yml'
```

Job wykonuje:

```bash
git fetch origin "+${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}:refs/remotes/origin/${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}"
migrationtool check --target-ref "origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME"
```

Ustaw `GIT_DEPTH: "0"`, aby porównanie działało również dla dłużej żyjących branchy.

Warto dodatkowo włączyć w GitLabie:

- wymagany zielony pipeline MR,
- merged results pipelines,
- merge trains dla repozytoriów, w których często równolegle powstają migracje.

## 5. Wpięcie runtime guard do istniejącego MigrationToolPackage

Dodaj referencję do `MigrationTool.Core`, a następnie przed obecnym `MigrateUp`:

```csharp
var plan = await MigrationRuntimeGuard.ValidateBeforeUpAsync(
    connection,
    typeof(Program).Assembly,
    targetVersion,
    versionInfoOptions,
    cancellationToken);

logger.LogInformation("{Plan}", MigrationRuntimeGuard.FormatPlan(plan));
runner.MigrateUp(targetVersion);

await MigrationRuntimeGuard.VerifyAfterUpAsync(
    connection,
    typeof(Program).Assembly,
    targetVersion,
    versionInfoOptions,
    cancellationToken);
```

Pełny przykład znajduje się w `examples/ExistingMigratorIntegration.cs`. Lock bazy musi zostać uzyskany przed `ValidateBeforeUpAsync` i pozostawać aktywny aż do zakończenia `VerifyAfterUpAsync`.

Guard blokuje wdrożenie przed wykonaniem SQL, gdy:

- `target_version` nie odpowiada żadnej migracji w assembly,
- istnieje niewdrożona migracja o wersji niższej niż najwyższa wersja z `VersionInfo`,
- baza jest nowsza niż `target_version` i `failWhenDatabaseAhead=true`,
- tabela `VersionInfo` nie istnieje, gdy `treatMissingVersionInfoAsEmpty=false`,
- `requireAppliedVersionsInAssembly=true` i baza zawiera wersję nieobecną w assembly.

Po `MigrateUp` guard ponownie czyta `VersionInfo` i sprawdza, czy wszystkie migracje do `target_version` zostały zapisane.

## 6. Plan bez łączenia z bazą

Przydatne do szybkiej diagnozy:

```bash
dotnet run --project tools/MigrationTool/src/MigrationTool.Cli -- \
  plan --service Orders --applied 20260723100000000,20260723120000000
```

Przykład wykrycia luki:

```text
applied: 100,200
available: 100,150,200

BŁĄD: 150 jest niewdrożoną migracją poniżej najwyższej wdrożonej wersji 200.
```

## 7. Opcjonalna walidacja MSBuild

Plik `build/MigrationValidation.targets` można zaimportować wyłącznie do projektów migracyjnych:

```xml
<PropertyGroup>
  <MigrationServiceName>Orders</MigrationServiceName>
  <MigrationValidationEnabled>true</MigrationValidationEnabled>
</PropertyGroup>

<Import Project="../../../tools/MigrationTool/build/MigrationValidation.targets" />
```

Target działa przed buildem, więc obejmie również standardowy `dotnet publish`, który wcześniej uruchamia build. Nie importuj targetu do projektu samego MigrationTool.

W dużym repozytorium lepiej pozostawić obowiązkową walidację w osobnym jobie CI, a target MSBuild traktować jako dodatkową kontrolę lokalną.

## 8. Założenia i ograniczenia startera

- Foldery migracji są bezpośrednimi dziećmi `migrationRoot`.
- Każdy folder zawiera dokładnie jedną unikalną wartość `[Migration(...)]`.
- Istniejące, wdrożone migracje powinny być niezmienne.
- `sync` powinno być używane przed wdrożeniem migracji na współdzielone środowisko.
- Runtime guard nie zastępuje blokady bazy. Obecny mechanizm lockowania w `MigrationToolPackage` powinien pozostać.
- Regex analizuje typową składnię atrybutu FluentMigratora. Jeżeli używacie własnych atrybutów lub generatorów kodu, scanner należy rozszerzyć o Roslyn.

## 9. Minimalny rollout

1. Wdrożyć `validate` i `check` jako nieblokujący job MR.
2. Po tygodniu poprawiania konfiguracji ustawić job jako obowiązkowy.
3. Udostępnić developerom `new` i `sync`.
4. Dodać runtime guard w trybie log-only na środowiskach testowych.
5. Przełączyć runtime guard na fail-fast.
6. Dopiero potem dodać opcjonalną walidację MSBuild.

## 10. Smoke test scenariusza równoległych branchy

Po skopiowaniu narzędzia uruchom:

```bash
dotnet run --project tools/MigrationTool/tests/MigrationTool.SmokeTests
```

Test tworzy tymczasowe repozytorium Git i odtwarza scenariusz:

1. wspólny baseline,
2. starsza migracja na branchu feature,
3. nowszy hotfix na branchu target,
4. błąd `MIGRATION_OLDER_THAN_TARGET_HEAD`,
5. automatyczne `sync`,
6. poprawna walidacja po przenumerowaniu.
# migrationToolB4P
