using Application.Cli;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Services;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var cli = CliArguments.Parse(args);
    if (cli.Command is "help" or "--help" or "-h")
    {
        PrintHelp();
        return 0;
    }

    var repositoryPath = cli.Get("repo") ?? Directory.GetCurrentDirectory();
    var configurationPath = cli.Get("config") ?? "migrationtool.json";
    var migrations = new MigrationWorkspaceService(repositoryPath, configurationPath);

    return cli.Command switch
    {
        "new" => await GenerateAsync(migrations, cli, cancellation.Token),
        "validate" => await ValidateAsync(migrations, cli, cancellation.Token),
        "check" => await CheckAsync(migrations, cli, cancellation.Token),
        "sync" => await SynchronizeAsync(migrations, cli, cancellation.Token),
        _ => throw new ArgumentException($"Nieznana komenda '{cli.Command}'.")
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operacja została anulowana.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Migration CLI zakończyło pracę błędem: {exception.Message}");
    return 2;
}

static async Task<int> GenerateAsync(
    MigrationWorkspaceService migrations,
    CliArguments cli,
    CancellationToken cancellationToken)
{
    var result = await migrations.GenerateAsync(
        new GenerateMigrationRequest(
            cli.GetRequired("service"),
            cli.GetRequired("name")),
        cancellationToken);

    Console.WriteLine($"Utworzono {result.Migration.DisplayName}");
    Console.WriteLine($"Folder: {result.Migration.FolderPath}");
    return 0;
}

static async Task<int> ValidateAsync(
    MigrationWorkspaceService migrations,
    CliArguments cli,
    CancellationToken cancellationToken)
{
    var result = await migrations.ValidateAsync(
        new ValidateMigrationsRequest(cli.Get("service")),
        cancellationToken);

    PrintValidation(result);
    return result.IsValid ? 0 : 2;
}

static async Task<int> CheckAsync(
    MigrationWorkspaceService migrations,
    CliArguments cli,
    CancellationToken cancellationToken)
{
    var result = await migrations.CheckAsync(
        new CheckMigrationsRequest(
            ResolveTargetRef(cli),
            cli.Get("service")),
        cancellationToken);

    PrintValidation(result);
    return result.IsValid ? 0 : 2;
}

static async Task<int> SynchronizeAsync(
    MigrationWorkspaceService migrations,
    CliArguments cli,
    CancellationToken cancellationToken)
{
    var result = await migrations.SynchronizeAsync(
        new SynchronizeMigrationsRequest(
            ResolveTargetRef(cli),
            cli.Get("service"),
            cli.HasFlag("dry-run")),
        cancellationToken);

    foreach (var service in result.Services)
    {
        PrintMessages(service.ServiceName, service.Validation);
        foreach (var change in service.Changes)
        {
            Console.WriteLine(
                $"[{service.ServiceName}] {change.Migration.Version} -> {change.NewVersion} " +
                change.Migration.Name);
        }
    }

    if (result.IsDryRun && result.HasChanges)
    {
        Console.WriteLine("Dry run: nie zmieniono plików.");
    }

    return result.IsValid ? 0 : 2;
}

static string ResolveTargetRef(CliArguments cli)
{
    if (cli.Get("target-ref") is { Length: > 0 } targetRef)
    {
        return targetRef;
    }

    var targetBranch = Environment.GetEnvironmentVariable(
        "CI_MERGE_REQUEST_TARGET_BRANCH_NAME");
    return !string.IsNullOrWhiteSpace(targetBranch)
        ? "origin/" + targetBranch
        : throw new ArgumentException(
            "Podaj --target-ref albo ustaw CI_MERGE_REQUEST_TARGET_BRANCH_NAME.");
}

static void PrintValidation(MigrationsValidationResult result)
{
    foreach (var service in result.Services)
    {
        Console.WriteLine(
            $"[{service.ServiceName}] source={service.CurrentMaximum}" +
            (service.TargetMaximum is null ? string.Empty : $" target={service.TargetMaximum}"));
        PrintMessages(service.ServiceName, service.Validation);
    }
}

static void PrintMessages(string serviceName, ValidationResult validation)
{
    if (validation.Messages.Count == 0)
    {
        Console.WriteLine($"[{serviceName}] OK");
        return;
    }

    foreach (var message in validation.Messages)
    {
        Console.WriteLine(
            $"[{serviceName}] {message.Severity.ToString().ToUpperInvariant()} " +
            $"{message.Code}: {message.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
Application.Cli

Komendy:
  new      --service NAME --name MigrationName
  validate [--service NAME]
  check    [--service NAME] --target-ref origin/develop
  sync     [--service NAME] --target-ref origin/develop [--dry-run]

Opcje wspólne:
  --repo PATH
  --config migrationtool.json
""");
}
