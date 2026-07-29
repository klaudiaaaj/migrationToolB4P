using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Runtime;
using MigrationTool.Core.Services;

var root = Path.Combine(Path.GetTempPath(), "migrationtool-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    RunGit(root, "init", "-b", "main");
    RunGit(root, "config", "user.email", "migrationtool@example.test");
    RunGit(root, "config", "user.name", "MigrationTool Smoke Test");

    var configuration = new MigrationToolConfiguration
    {
        ProjectRoot = "src/Orders",
        Namespace = "Orders.Migrations"
    };

    CreateMigration(root, configuration, 20260101000000000, "Baseline");
    WriteTargetVersion(root, 20260101000000000);
    CommitAll(root, "baseline");

    RunGit(root, "branch", "target");
    RunGit(root, "checkout", "-b", "feature");
    CreateMigration(root, configuration, 20260102000000000, "FeatureMigration");
    WriteTargetVersion(root, 20260102000000000);
    CommitAll(root, "feature migration");

    RunGit(root, "checkout", "target");
    CreateMigration(root, configuration, 20260103000000000, "HotfixMigration");
    WriteTargetVersion(root, 20260103000000000);
    CommitAll(root, "hotfix migration");

    RunGit(root, "checkout", "feature");

    WriteConfiguration(root, configuration);
    var api = new MigrationWorkspaceService(root);
    using var registeredServices = new ServiceCollection()
        .AddMigrationToolServices(root)
        .BuildServiceProvider();
    Assert(
        registeredServices.GetRequiredService<MigrationWorkspaceService>() is not null,
        "AddMigrationToolServices powinno zarejestrować publiczne API.");

    await AssertThrowsAsync<InvalidOperationException>(
        () => api.CheckAsync("missing-ref"),
        "CheckAsync powinno zgłosić błąd dla nieistniejącego refa.");

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            () => api.ValidateAsync(cancelled.Token),
            "Publiczne API powinno respektować CancellationToken.");
    }

    var before = await api.CheckAsync("target");

    Assert(
        before.Validation.Messages
            .Any(x => x.Code == "MIGRATION_OLDER_THAN_TARGET_HEAD"),
        "Walidator powinien wykryć migrację starszą od target head.");

    var dryRun = await api.SynchronizeAsync("target", isDryRun: true);
    Assert(dryRun.HasChanges, "Dry-run powinien wykryć zmianę numeru.");

    var synchronization = await api.SynchronizeAsync("target");
    var change = synchronization.Changes.Single();
    Assert(
        change.NewVersion > 20260103000000000,
        "Nowa wersja powinna być większa od hotfixu.");

    var after = await api.CheckAsync("target");
    var structure = await api.ValidateAsync();

    Assert(after.IsValid, "Walidacja względem targetu powinna przejść po sync.");
    Assert(structure.IsValid, "Walidacja strukturalna powinna przejść po sync.");

    var generated = await api.GenerateAsync("GeneratedByPublicApi");
    Assert(
        generated.Migration.Version > change.NewVersion,
        "Publiczne GenerateAsync powinno utworzyć kolejną migrację.");

    var targetStore = new TargetVersionStore();
    Assert(
        targetStore.Read(root, configuration) == generated.Migration.Version,
        "target_version powinno wskazywać najwyższą migrację.");

    VerifyUnifiedRunApi();

    Console.WriteLine("MigrationTool smoke test: PASSED");
    return 0;
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // Nie przesłaniamy wyniku testu problemem sprzątania katalogu tymczasowego.
    }
}

static void VerifyUnifiedRunApi()
{
    _ = new MigrationToolRunner(typeof(Program).Assembly);

    _ = new MigrationOptions
    {
        ConnectionString = "Server=example;Database=example",
        SchemaName = "orders",
        ReportSchemaName = "orders_reports",
        Version = 200,
        Timeout = 60,
        IsDryRun = true
    };
}

static void CreateMigration(
    string repositoryRoot,
    MigrationToolConfiguration configuration,
    long version,
    string name)
{
    var folder = Path.Combine(
        repositoryRoot,
        configuration.MigrationRoot,
        $"{version}_{name}");
    Directory.CreateDirectory(folder);
    File.WriteAllText(
        Path.Combine(folder, name + ".cs"),
        $"using FluentMigrator;\nnamespace {configuration.Namespace};\n" +
        $"[Migration({version})]\npublic sealed class {name} {{ }}\n");
}

static void WriteTargetVersion(string root, long version)
{
    var path = Path.Combine(root, "src/Orders/appsettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, $"{{\n  \"target_version\": {version}\n}}\n");
}

static void WriteConfiguration(
    string root,
    MigrationToolConfiguration configuration)
{
    File.WriteAllText(
        Path.Combine(root, "migrationtool.json"),
        $$"""
{
  "projectRoot": "{{configuration.ProjectRoot}}",
  "namespace": "{{configuration.Namespace}}"
}
""");
}

static void CommitAll(string root, string message)
{
    RunGit(root, "add", ".");
    RunGit(root, "commit", "-m", message);
}

static void RunGit(string root, params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Nie udało się uruchomić git.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var output = outputTask.GetAwaiter().GetResult();
    var error = errorTask.GetAwaiter().GetResult();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"git {string.Join(" ", arguments)} failed: {output}\n{error}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("SMOKE TEST FAILED: " + message);
    }
}

static async Task AssertThrowsAsync<TException>(
    Func<Task> action,
    string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException("SMOKE TEST FAILED: " + message);
}
