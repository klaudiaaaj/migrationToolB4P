# Application.Cli

Ten katalog jest wzorcem do skopiowania do repozytorium aplikacji:

```text
Application.sln
├── src/Application
├── src/Application.Database.Migrations
├── src/Application.Cli
└── tests
```

1. Skopiuj `Application.Cli.csproj`, `Program.cs` i `CliArguments.cs` do
   `src/Application.Cli`.
2. Ustaw faktyczny `PackageReference` oraz wersję paczki.
3. Dodaj projekt do solution aplikacji:

   ```bash
   dotnet sln Application.sln add src/Application.Cli/Application.Cli.csproj
   ```

4. Uruchom jedną z komend opisanych niżej.

## Komendy

Wszystkie polecenia zakładają, że są uruchamiane z katalogu głównego
repozytorium aplikacji. Jeżeli uruchamiasz je z innego katalogu, podaj
`--repo /sciezka/do/repozytorium`.

### Tworzenie migracji

```bash
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  new \
  --name AddCustomerStatus
```

Tworzy folder i klasę migracji oraz aktualizuje `TargetVersion`.

### Walidacja lokalna

```bash
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  validate
```

### Sprawdzenie względem brancha docelowego

Najpierw pobierz aktualny branch docelowy:

```bash
git fetch origin develop
```

Następnie uruchom:

```bash
sh scripts/migrationtool.sh \
  check \
  --target-ref origin/develop
```

W pipeline GitLab parametr `--target-ref` może być zbudowany z
`CI_MERGE_REQUEST_TARGET_BRANCH_NAME`. Jeżeli go nie podasz, CLI użyje tej
zmiennej automatycznie.

### Synchronizacja numerów migracji

Najpierw sprawdź wynik bez zapisu plików:

```bash
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  sync \
  --target-ref origin/develop \
  --dry-run
```

Jeżeli wynik jest poprawny, wykonaj synchronizację:

```bash
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  sync \
  --target-ref origin/develop
```

`sync` może zmienić numery tylko migracji znajdujących się wyłącznie na Twoim
branchu. Po synchronizacji sprawdź diff, zacommituj zmiany i ponownie uruchom
`check`.

### Opcje wspólne

```text
--repo PATH                 katalog repozytorium Git
--config migrationtool.json ścieżka do konfiguracji migracji
```

Przykład z jawną lokalizacją repozytorium i konfiguracji:

```bash
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  validate \
  --repo /sciezka/do/aplikacji \
  --config migrationtool.json
```

Parser, `Console` i mapowanie kodów wyjścia należą wyłącznie do tego projektu.
Biblioteka `MigrationTool.Core` nie zna argumentów tekstowych ani procesu CLI.

## Konfiguracja jednego projektu migracyjnego

`migrationtool.json` znajduje się w repozytorium aplikacji i opisuje dokładnie
jeden projekt migracyjny:

```json
{
  "projectRoot": "src/Application.Database.Migrations",
  "namespace": "Application.Database.Migrations"
}
```

`projectRoot` jest względny wobec katalogu głównego repozytorium. CLI zawsze
szuka migracji w `<projectRoot>/Migrations` i wersji w
`<projectRoot>/appsettings.json`.
Nazwa właściwości wersji jest stała: `TargetVersion`.

## Merged Results Pipeline

Najpierw w GitLabie włącz:

```text
Settings
└── Merge requests
    └── Merge options
        └── Enable merged results pipelines
```

Opcji nie da się włączyć samym YAML-em. W `.gitlab-ci.yml` job musi mieć regułę:

```yaml
rules:
  - if: '$CI_PIPELINE_SOURCE == "merge_request_event"'
```

Gotowy job znajduje się w `gitlab-ci.yml`. GitLab uruchamia go na tymczasowym
commicie będącym wynikiem połączenia source i target brancha. Job wykonuje:

```text
scripts/migrationtool.sh check
```

Skrypt nie używa .NET. Sprawdza foldery, atrybuty, `TargetVersion`, porównuje
wynik merge z dokładnym SHA target brancha i blokuje MR, jeżeli migracja
wymaga synchronizacji. Dla migracji istniejących na target branchu porównuje
wyłącznie metody `public override void Up()` i `public override void Down()`;
zmiany poza tymi metodami są ignorowane. W obrazie wystarczą `git` oraz podstawowe narzędzia
POSIX (`sh`, `find`, `sed`, `awk`, `sort`).

`sync` pozostaje komendą aplikacji .NET uruchamianą lokalnie. Nie wykonujemy
go automatycznie w pipeline, ponieważ zmiany zniknęłyby razem z katalogiem
roboczym joba. Po czerwonym `check` developer wykonuje lokalnie:

```bash
git fetch origin develop

dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
  sync \
  --target-ref origin/develop

sh scripts/migrationtool.sh \
  check \
  --target-ref origin/develop

git add .
git commit -m "Synchronize migration version"
git push
```

Push uruchomi nowy Merged Results Pipeline już z poprawioną migracją.

### Czas wykonania pipeline

Pipeline nie wykonuje już `dotnet restore`, `dotnet build` ani `dotnet run`.
Wykonuje tylko:

```bash
sh scripts/migrationtool.sh \
  check \
  --repo "$CI_PROJECT_DIR" \
  --config migrationtool.json \
  --target-ref origin/develop
```

Target branch jest pobierany z `--depth=1`. Jeżeli najdłużej trwa instalowanie
Gita, użyj firmowego obrazu, który już go zawiera.

## Git w obrazie GitLab CI

Skrypt `check` i komenda `sync` uruchamiają proces `git`, dlatego Git musi być
dostępny wewnątrz obrazu joba. Sam checkout wykonywany przez GitLab Runnera nie
gwarantuje, że polecenie `git` będzie dostępne później w skrypcie joba.

Minimalny przykład:

```yaml
image: alpine:3.20

before_script:
  - apk add --no-cache git
  - git --version
  - git fetch origin "+${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}:refs/remotes/origin/${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}"
```

Jeżeli runner nie ma dostępu do repozytoriów systemowych albo uruchamia joby
bez uprawnień do instalowania pakietów, przygotuj firmowy obraz z Gitem.
