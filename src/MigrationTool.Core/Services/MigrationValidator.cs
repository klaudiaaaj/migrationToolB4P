using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Services;

public sealed class MigrationValidator
{
    private readonly TargetVersionStore _targetVersionStore;

    public MigrationValidator(TargetVersionStore targetVersionStore)
    {
        _targetVersionStore = targetVersionStore;
    }

    public ValidationResult ValidateStructure(
        string repositoryRoot,
        MigrationServiceConfiguration service,
        IReadOnlyList<MigrationDescriptor> migrations)
    {
        var result = new ValidationResult();

        foreach (var duplicate in migrations.GroupBy(x => x.Version).Where(x => x.Count() > 1))
        {
            result.Error(
                "DUPLICATE_VERSION",
                $"W mikroserwisie '{service.Name}' wersja {duplicate.Key} występuje więcej niż raz: " +
                string.Join(", ", duplicate.Select(x => x.FolderPath)));
        }

        foreach (var migration in migrations)
        {
            if (migration.Files.Count == 0)
            {
                result.Error(
                    "EMPTY_MIGRATION_FOLDER",
                    $"Folder '{migration.FolderPath}' nie zawiera pliku .cs.");
                continue;
            }

            if (migration.AttributeCount != 1 || migration.AttributeVersion is null)
            {
                result.Error(
                    "MIGRATION_ATTRIBUTE_MISSING_OR_AMBIGUOUS",
                    $"W folderze '{migration.FolderPath}' oczekiwano dokładnie jednego atrybutu " +
                    $"[Migration(...)], znaleziono: {migration.AttributeCount}.");
                continue;
            }

            if (migration.AttributeVersion.Value != migration.Version)
            {
                result.Error(
                    "FOLDER_ATTRIBUTE_MISMATCH",
                    $"Folder '{migration.FolderPath}' ma wersję {migration.Version}, ale atrybut " +
                    $"[Migration(...)] ma wersję {migration.AttributeVersion.Value}.");
            }
        }

        if (migrations.Count == 0)
        {
            result.Warning(
                "NO_MIGRATIONS",
                $"Nie znaleziono migracji dla mikroserwisu '{service.Name}'.");
            return result;
        }

        var expectedTarget = migrations.Max(x => x.Version);
        foreach (var targetFile in service.TargetVersionFiles)
        {
            try
            {
                var actualTarget = _targetVersionStore.Read(repositoryRoot, targetFile);
                if (actualTarget != expectedTarget)
                {
                    result.Error(
                        "TARGET_VERSION_MISMATCH",
                        $"Plik '{targetFile.Path}' ma {targetFile.PropertyName}={actualTarget}, " +
                        $"ale najwyższa migracja ma wersję {expectedTarget}.");
                }
            }
            catch (Exception exception)
            {
                result.Error("TARGET_VERSION_READ_ERROR", exception.Message);
            }
        }

        return result;
    }

    public ValidationResult ValidateAgainstTarget(
        MigrationServiceConfiguration service,
        IReadOnlyList<MigrationDescriptor> current,
        IReadOnlyList<MigrationDescriptor> target,
        string targetRef)
    {
        var result = new ValidationResult();

        foreach (var duplicate in target.GroupBy(x => x.Version).Where(x => x.Count() > 1))
        {
            result.Error(
                "TARGET_DUPLICATE_VERSION",
                $"Branch '{targetRef}' zawiera więcej niż jedną migrację o wersji {duplicate.Key}.");
        }

        var targetByVersion = target
            .GroupBy(x => x.Version)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single());
        var currentByVersion = current
            .GroupBy(x => x.Version)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single());

        foreach (var currentMigration in currentByVersion.Values)
        {
            if (!targetByVersion.TryGetValue(currentMigration.Version, out var targetMigration))
            {
                continue;
            }

            if (!string.Equals(currentMigration.Name, targetMigration.Name, StringComparison.Ordinal) ||
                !string.Equals(currentMigration.ContentHash, targetMigration.ContentHash, StringComparison.Ordinal))
            {
                result.Error(
                    "VERSION_COLLISION_OR_MODIFIED_MIGRATION",
                    $"Wersja {currentMigration.Version} oznacza inną migrację w branchu źródłowym i w " +
                    $"'{targetRef}'. Source: '{currentMigration.DisplayName}', target: " +
                    $"'{targetMigration.DisplayName}'. Zrób rebase albo uruchom sync, jeśli jest to nowa kolizja.");
            }
        }

        var newMigrations = current
            .Where(x => !targetByVersion.ContainsKey(x.Version))
            .OrderBy(x => x.Version)
            .ToArray();

        if (newMigrations.Length == 0)
        {
            return result;
        }

        var targetMaximum = target.Select(x => x.Version).DefaultIfEmpty(0).Max();
        foreach (var migration in newMigrations.Where(x => x.Version <= targetMaximum))
        {
            result.Error(
                "MIGRATION_OLDER_THAN_TARGET_HEAD",
                $"Nowa migracja '{migration.DisplayName}' ma wersję {migration.Version}, która nie jest " +
                $"większa od najwyższej migracji {targetMaximum} w '{targetRef}'. " +
                "Uruchom migrationtool sync.");
        }

        return result;
    }
}
