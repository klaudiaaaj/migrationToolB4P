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
        "validate" => await ValidateAsync(migrations, cancellation.Token),
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
        cli.GetRequired("name"),
        cancellationToken);

    Console.WriteLine($"Utworzono {result.Migration.DisplayName}");
    Console.WriteLine($"Folder: {result.Migration.FolderPath}");
    return 0;
}

static async Task<int> ValidateAsync(
    MigrationWorkspaceService migrations,
    CancellationToken cancellationToken)
{
    var result = await migrations.ValidateAsync(cancellationToken);

    PrintValidation(result);
    return result.IsValid ? 0 : 2;
}

static async Task<int> CheckAsync(
    MigrationWorkspaceService migrations,
    CliArguments cli,
    CancellationToken cancellationToken)
{
    var result = await migrations.CheckAsync(
        ResolveTargetRef(cli),
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
        ResolveTargetRef(cli),
        cli.HasFlag("dry-run"),
        cancellationToken);

    PrintMessages(result.Validation);
    foreach (var change in result.Changes)
    {
        Console.WriteLine(
            $"{change.Migration.Version} -> {change.NewVersion} " +
            change.Migration.Name);
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

static void PrintValidation(MigrationValidationResult result)
{
    Console.WriteLine(
        $"source={result.CurrentMaximum}" +
        (result.TargetMaximum is null ? string.Empty : $" target={result.TargetMaximum}"));
    PrintMessages(result.Validation);
}

static void PrintMessages(ValidationResult validation)
{
    if (validation.Messages.Count == 0)
    {
        Console.WriteLine("OK");
        return;
    }

    foreach (var message in validation.Messages)
    {
        Console.WriteLine(
            $"{message.Severity.ToString().ToUpperInvariant()} " +
            $"{message.Code}: {message.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
Application.Cli

Komendy:
  new      --name MigrationName
  validate
  check    --target-ref origin/develop
  sync     --target-ref origin/develop [--dry-run]

Opcje wspólne:
  --repo PATH
  --config migrationtool.json
""");
}
