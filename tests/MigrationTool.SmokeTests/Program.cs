using System.Diagnostics;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Git;
using MigrationTool.Core.Runtime;
using MigrationTool.Core.Services;

var root = Path.Combine(Path.GetTempPath(), "migrationtool-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    RunGit(root, "init", "-b", "main");
    RunGit(root, "config", "user.email", "migrationtool@example.test");
    RunGit(root, "config", "user.name", "MigrationTool Smoke Test");

    var service = new MigrationServiceConfiguration
    {
        Name = "Orders",
        MigrationRoot = "src/Orders/Migrations",
        Namespace = "Orders.Migrations",
        TargetVersionFiles =
        [
            new TargetVersionFileConfiguration
            {
                Path = "src/Orders/appsettings.json",
                PropertyName = "target_version"
            }
        ]
    };

    CreateMigration(root, service, 20260101000000000, "Baseline");
    WriteTargetVersion(root, 20260101000000000);
    CommitAll(root, "baseline");

    RunGit(root, "branch", "target");
    RunGit(root, "checkout", "-b", "feature");
    CreateMigration(root, service, 20260102000000000, "FeatureMigration");
    WriteTargetVersion(root, 20260102000000000);
    CommitAll(root, "feature migration");

    RunGit(root, "checkout", "target");
    CreateMigration(root, service, 20260103000000000, "HotfixMigration");
    WriteTargetVersion(root, 20260103000000000);
    CommitAll(root, "hotfix migration");

    RunGit(root, "checkout", "feature");

    var scanner = new MigrationSourceScanner();
    var targetStore = new TargetVersionStore();
    var validator = new MigrationValidator(targetStore);
    var synchronizer = new MigrationSynchronizer(scanner, targetStore);
    var git = new GitClient(root);

    var current = scanner.ScanWorkingTree(root, service);
    var target = scanner.ScanGitRef(git, "target", service);
    var before = validator.ValidateAgainstTarget(service, current, target, "target");

    Assert(
        before.Messages.Any(x => x.Code == "MIGRATION_OLDER_THAN_TARGET_HEAD"),
        "Walidator powinien wykryć migrację starszą od target head.");

    var plan = synchronizer.BuildPlan(current, target);
    Assert(plan.Changes.Count == 1, "Sync powinien przenumerować jedną migrację feature.");
    Assert(
        plan.Changes[0].NewVersion > 20260103000000000,
        "Nowa wersja powinna być większa od hotfixu.");

    synchronizer.Apply(root, service, plan);

    var refreshed = scanner.ScanWorkingTree(root, service);
    var after = validator.ValidateAgainstTarget(service, refreshed, target, "target");
    var structure = validator.ValidateStructure(root, service, refreshed);

    Assert(after.IsValid, "Walidacja względem targetu powinna przejść po sync.");
    Assert(structure.IsValid, "Walidacja strukturalna powinna przejść po sync.");
    Assert(
        targetStore.Read(root, service.TargetVersionFiles[0]) == refreshed.Max(x => x.Version),
        "target_version powinno wskazywać najwyższą migrację.");

    VerifyDownPlanning();
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

static void VerifyDownPlanning()
{
    var available = new[]
    {
        new RuntimeMigration(100, "Baseline", typeof(object)),
        new RuntimeMigration(200, "AddCustomer", typeof(string)),
        new RuntimeMigration(300, "AddCustomerIndex", typeof(int))
    };
    var applied = new[]
    {
        new AppliedMigration(100),
        new AppliedMigration(200),
        new AppliedMigration(300)
    };

    var plan = MigrationDownPlanBuilder.Build(available, applied, 100);
    Assert(plan.IsSafe, "Rollback do wdrożonej wersji powinien być bezpieczny.");
    Assert(
        plan.ToRollback.Select(x => x.Version).SequenceEqual(new long[] { 300, 200 }),
        "Migracje Down powinny być planowane malejąco.");

    var missingTarget = MigrationDownPlanBuilder.Build(available, applied, 150);
    Assert(
        !missingTarget.TargetExists && !missingTarget.TargetApplied && !missingTarget.IsSafe,
        "Rollback do nieistniejącej wersji powinien zostać zablokowany.");

    var incompleteAssembly = MigrationDownPlanBuilder.Build(
        available.Where(x => x.Version != 300).ToArray(),
        applied,
        100);
    Assert(
        incompleteAssembly.UnavailableToRollback.Select(x => x.Version).SequenceEqual(new long[] { 300 }),
        "Rollback powinien wykrywać brak implementacji wdrożonej migracji.");

    var rollbackAll = MigrationDownPlanBuilder.Build(available, applied, 0);
    Assert(rollbackAll.IsSafe, "Wartość 0 powinna umożliwiać wycofanie wszystkich migracji.");
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
    MigrationServiceConfiguration service,
    long version,
    string name)
{
    var folder = Path.Combine(repositoryRoot, service.MigrationRoot, $"{version}_{name}");
    Directory.CreateDirectory(folder);
    File.WriteAllText(
        Path.Combine(folder, name + ".cs"),
        $"using FluentMigrator;\nnamespace {service.Namespace};\n[Migration({version})]\npublic sealed class {name} {{ }}\n");
}

static void WriteTargetVersion(string root, long version)
{
    var path = Path.Combine(root, "src/Orders/appsettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, $"{{\n  \"target_version\": {version}\n}}\n");
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
