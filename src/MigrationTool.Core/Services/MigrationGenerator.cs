using System.Globalization;
using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Services;

public sealed class MigrationGenerator
{
    private static readonly Regex ClassNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MigrationSourceScanner _scanner;
    private readonly TargetVersionStore _targetVersionStore;

    public MigrationGenerator(
        MigrationSourceScanner scanner,
        TargetVersionStore targetVersionStore)
    {
        _scanner = scanner;
        _targetVersionStore = targetVersionStore;
    }

    public MigrationDescriptor Create(
        string repositoryRoot,
        MigrationToolConfiguration configuration,
        string migrationName)
    {
        if (!ClassNamePattern.IsMatch(migrationName))
        {
            throw new InvalidOperationException(
                "Nazwa migracji musi być poprawną nazwą klasy C# bez spacji, np. AddCustomerStatus.");
        }

        var existing = _scanner.ScanWorkingTree(repositoryRoot, configuration);
        var currentTimestamp = long.Parse(
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        var version = Math.Max(currentTimestamp, existing.Select(x => x.Version).DefaultIfEmpty(0).Max() + 1);

        var migrationRoot = Path.GetFullPath(
            Path.Combine(repositoryRoot, configuration.MigrationRoot));
        var folderName = $"{version}_{migrationName}";
        var folder = Path.Combine(migrationRoot, folderName);

        if (Directory.Exists(folder))
        {
            throw new IOException($"Folder '{folder}' już istnieje.");
        }

        var targetVersionPath = Path.GetFullPath(
            Path.Combine(repositoryRoot, configuration.TargetVersionFile));
        var targetVersionBackup = File.ReadAllBytes(targetVersionPath);

        try
        {
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, migrationName + ".cs");
            File.WriteAllText(
                filePath,
                BuildTemplate(configuration.Namespace, migrationName, version));
            _targetVersionStore.Write(repositoryRoot, configuration, version);

            return _scanner.ScanWorkingTree(repositoryRoot, configuration)
                .Single(x => x.Version == version);
        }
        catch
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            File.WriteAllBytes(targetVersionPath, targetVersionBackup);

            throw;
        }
    }

    private static string BuildTemplate(string migrationNamespace, string migrationName, long version)
        => $$"""
using FluentMigrator;

namespace {{migrationNamespace}};

[Migration({{version}})]
public sealed class {{migrationName}} : Migration
{
    public override void Up()
    {
        // TODO: dodaj zmianę schematu lub danych.
    }

    public override void Down()
    {
        // TODO: dodaj bezpieczne wycofanie albo jawnie opisz brak rollbacku.
    }
}
""";
}
