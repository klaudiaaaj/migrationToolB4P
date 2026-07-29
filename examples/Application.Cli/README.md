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

Tworzy folder i klasę migracji oraz aktualizuje `target_version`.

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
dotnet run --project src/Application.Cli/Application.Cli.csproj -- \
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
Nazwa właściwości wersji jest stała: `target_version`.
