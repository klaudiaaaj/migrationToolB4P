# Application.Cli

Ten katalog jest wzorcem do skopiowania do repozytorium aplikacji:

```text
Application.sln
├── src/Application
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

4. Uruchom:

   ```bash
   dotnet run --project src/Application.Cli/Application.Cli.csproj -- validate
   ```

Parser, `Console` i mapowanie kodów wyjścia należą wyłącznie do tego projektu.
Biblioteka `MigrationTool.Core` nie zna argumentów tekstowych ani procesu CLI.
