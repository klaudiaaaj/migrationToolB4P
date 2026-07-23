using MigrationTool.Cli;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Git;
using MigrationTool.Core.Runtime;
using MigrationTool.Core.Services;

return await MainAsync(args);

static Task<int> MainAsync(string[] args)
{
    try
    {
        var cli = CliArguments.Parse(args);
        if (cli.Command is "help" or "--help" or "-h")
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        var initialGit = new GitClient(cli.Get("repo") ?? Directory.GetCurrentDirectory());
        var repositoryRoot = Path.GetFullPath(initialGit.FindRepositoryRoot());
        var git = new GitClient(repositoryRoot);
        var configPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            cli.Get("config") ?? "migrationtool.json"));
        var configuration = MigrationToolConfiguration.Load(configPath);

        var scanner = new MigrationSourceScanner();
        var targetStore = new TargetVersionStore();
        var validator = new MigrationValidator(targetStore);

        return Task.FromResult(cli.Command switch
        {
            "new" => RunNew(cli, repositoryRoot, configuration, scanner, targetStore),
            "validate" => RunValidate(cli, repositoryRoot, configuration, scanner, validator),
            "check" => RunCheck(cli, repositoryRoot, configuration, git, scanner, validator),
            "sync" => RunSync(cli, repositoryRoot, configuration, git, scanner, validator, targetStore),
            "plan" => RunPlan(cli, repositoryRoot, configuration, scanner, targetStore),
            _ => throw new ArgumentException($"Nieznana komenda '{cli.Command}'.")
        });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("MigrationTool zakończył pracę błędem:");
        Console.Error.WriteLine(exception.Message);
        return Task.FromResult(2);
    }
}

static int RunNew(
    CliArguments cli,
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    MigrationSourceScanner scanner,
    TargetVersionStore targetStore)
{
    var service = configuration.GetRequiredService(cli.GetRequired("service"));
    var name = cli.GetRequired("name");
    var generator = new MigrationGenerator(scanner, targetStore);
    var migration = generator.Create(repositoryRoot, service, name);

    Console.WriteLine("Utworzono migrację:");
    Console.WriteLine($"  Service: {service.Name}");
    Console.WriteLine($"  Version: {migration.Version}");
    Console.WriteLine($"  Folder:  {migration.FolderPath}");
    return 0;
}

static int RunValidate(
    CliArguments cli,
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    MigrationSourceScanner scanner,
    MigrationValidator validator)
{
    var aggregate = new ValidationResult();

    foreach (var service in configuration.SelectServices(cli.Get("service")))
    {
        var migrations = scanner.ScanWorkingTree(repositoryRoot, service);
        var result = validator.ValidateStructure(repositoryRoot, service, migrations);
        PrintValidation(service.Name, result);
        aggregate.Merge(result);
    }

    return aggregate.IsValid ? 0 : 2;
}

static int RunCheck(
    CliArguments cli,
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    GitClient git,
    MigrationSourceScanner scanner,
    MigrationValidator validator)
{
    var targetRef = ResolveTargetRef(cli);
    if (!git.RefExists(targetRef))
    {
        throw new InvalidOperationException(
            $"Git ref '{targetRef}' nie istnieje. Wykonaj git fetch dla brancha docelowego.");
    }

    var aggregate = new ValidationResult();
    Console.WriteLine($"Target ref: {targetRef}");

    foreach (var service in configuration.SelectServices(cli.Get("service")))
    {
        var current = scanner.ScanWorkingTree(repositoryRoot, service);
        var target = scanner.ScanGitRef(git, targetRef, service);

        var structural = validator.ValidateStructure(repositoryRoot, service, current);
        var branchValidation = validator.ValidateAgainstTarget(service, current, target, targetRef);
        structural.Merge(branchValidation);

        PrintComparison(service.Name, current, target, targetRef);
        PrintValidation(service.Name, structural);
        aggregate.Merge(structural);
    }

    return aggregate.IsValid ? 0 : 2;
}

static int RunSync(
    CliArguments cli,
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    GitClient git,
    MigrationSourceScanner scanner,
    MigrationValidator validator,
    TargetVersionStore targetStore)
{
    var targetRef = ResolveTargetRef(cli);
    if (!git.RefExists(targetRef))
    {
        throw new InvalidOperationException(
            $"Git ref '{targetRef}' nie istnieje. Wykonaj git fetch dla brancha docelowego.");
    }

    var dryRun = cli.HasFlag("dry-run");
    var synchronizer = new MigrationSynchronizer(scanner, targetStore);
    var anyChanges = false;

    foreach (var service in configuration.SelectServices(cli.Get("service")))
    {
        var current = scanner.ScanWorkingTree(repositoryRoot, service);
        var structural = validator.ValidateStructure(repositoryRoot, service, current);
        if (!structural.IsValid)
        {
            PrintValidation(service.Name, structural);
            return 2;
        }

        var target = scanner.ScanGitRef(git, targetRef, service);
        var plan = synchronizer.BuildPlan(current, target);

        Console.WriteLine();
        Console.WriteLine($"[{service.Name}] target head: {plan.TargetMaximum}");
        if (plan.Changes.Count == 0)
        {
            Console.WriteLine("Brak migracji wymagających zmiany numeru.");
            continue;
        }

        anyChanges = true;
        foreach (var change in plan.Changes)
        {
            Console.WriteLine($"  {change.Migration.Version}_{change.Migration.Name}");
            Console.WriteLine($"  -> {change.NewVersion}_{change.Migration.Name}");
        }

        if (!dryRun)
        {
            synchronizer.Apply(repositoryRoot, service, plan);
            var refreshed = scanner.ScanWorkingTree(repositoryRoot, service);
            var validation = validator.ValidateStructure(repositoryRoot, service, refreshed);
            if (!validation.IsValid)
            {
                PrintValidation(service.Name, validation);
                return 2;
            }
        }
    }

    if (dryRun && anyChanges)
    {
        Console.WriteLine();
        Console.WriteLine("Dry run: nie zmieniono plików.");
    }
    else if (anyChanges)
    {
        Console.WriteLine();
        Console.WriteLine("Synchronizacja zakończona. Sprawdź git diff i wykonaj commit.");
    }

    return 0;
}

static int RunPlan(
    CliArguments cli,
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    MigrationSourceScanner scanner,
    TargetVersionStore targetStore)
{
    var service = configuration.GetRequiredService(cli.GetRequired("service"));
    var migrations = scanner.ScanWorkingTree(repositoryRoot, service);
    var targetVersion = cli.Get("target-version") is { } targetValue
        ? long.Parse(targetValue)
        : targetStore.Read(repositoryRoot, service.TargetVersionFiles[0]);
    var applied = ParseAppliedVersions(cli.Get("applied"));

    var runtimeMigrations = migrations
        .Select(x => new RuntimeMigration(x.Version, x.DisplayName, typeof(object)))
        .ToArray();
    var appliedMigrations = applied
        .Select(x => new AppliedMigration(x, null, null))
        .ToArray();

    var plan = MigrationPlanBuilder.Build(
        runtimeMigrations,
        appliedMigrations,
        targetVersion,
        failWhenDatabaseAhead: true,
        requireAppliedVersionsInAssembly: false);

    Console.WriteLine(MigrationRuntimeGuard.FormatPlan(plan));

    if (!plan.TargetExists)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("BŁĄD: target_version nie odpowiada żadnej migracji w projekcie.");
        return 2;
    }

    if (plan.Late.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("BŁĄD: wykryto migracje pominięte poniżej najwyższej wdrożonej wersji:");
        foreach (var migration in plan.Late)
        {
            Console.Error.WriteLine($"  - {migration.Version} {migration.Name}");
        }
        return 2;
    }

    if (plan.DatabaseAhead)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("BŁĄD: baza jest nowsza niż target_version artefaktu.");
        return 2;
    }

    return 0;
}

static string ResolveTargetRef(CliArguments cli)
{
    if (cli.Get("target-ref") is { Length: > 0 } explicitRef)
    {
        return explicitRef;
    }

    var targetBranch = Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_TARGET_BRANCH_NAME");
    if (!string.IsNullOrWhiteSpace(targetBranch))
    {
        return "origin/" + targetBranch;
    }

    throw new ArgumentException(
        "Brakuje --target-ref, a CI_MERGE_REQUEST_TARGET_BRANCH_NAME nie jest ustawione.");
}

static IReadOnlyList<long> ParseAppliedVersions(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => long.Parse(x))
        .Distinct()
        .OrderBy(x => x)
        .ToArray();
}

static void PrintComparison(
    string serviceName,
    IReadOnlyList<MigrationTool.Core.Domain.MigrationDescriptor> current,
    IReadOnlyList<MigrationTool.Core.Domain.MigrationDescriptor> target,
    string targetRef)
{
    var targetVersions = target.Select(x => x.Version).ToHashSet();
    var added = current.Where(x => !targetVersions.Contains(x.Version)).OrderBy(x => x.Version).ToArray();

    Console.WriteLine();
    Console.WriteLine($"[{serviceName}]");
    Console.WriteLine($"  Najwyższa wersja w {targetRef}: {target.Select(x => x.Version).DefaultIfEmpty(0).Max()}");
    Console.WriteLine($"  Najwyższa wersja w source:     {current.Select(x => x.Version).DefaultIfEmpty(0).Max()}");

    if (added.Length > 0)
    {
        Console.WriteLine("  Migracje obecne tylko w source:");
        foreach (var migration in added)
        {
            Console.WriteLine($"    - {migration.DisplayName}");
        }
    }
}

static void PrintValidation(string serviceName, ValidationResult result)
{
    if (result.Messages.Count == 0)
    {
        Console.WriteLine($"[{serviceName}] OK");
        return;
    }

    foreach (var message in result.Messages)
    {
        var prefix = message.Severity == ValidationSeverity.Error ? "ERROR" : "WARN";
        Console.WriteLine($"[{serviceName}] {prefix} {message.Code}: {message.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
MigrationTool (.NET 8)

Komendy:
  new      --service NAME --name MigrationName [--config migrationtool.json]
  validate [--service NAME] [--config migrationtool.json]
  check    [--service NAME] --target-ref origin/develop
  sync     [--service NAME] --target-ref origin/develop [--dry-run]
  plan     --service NAME --applied 100,200 [--target-version 300]

Opcje wspólne:
  --repo PATH       katalog znajdujący się wewnątrz repozytorium Git
  --config PATH     ścieżka względem katalogu głównego repozytorium

W GitLab CI opcję --target-ref można pominąć. Narzędzie użyje:
  origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME
""");
}
