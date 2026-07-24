using System.Data.Common;
using System.Reflection;
using System.Text;
using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Runtime;

public static class MigrationRuntimeGuard
{
    public static async Task<MigrationPlan> ValidateBeforeUpAsync(
        DbConnection connection,
        Assembly migrationsAssembly,
        long targetVersion,
        VersionInfoConfiguration versionInfo,
        CancellationToken cancellationToken = default)
    {
        var available = AssemblyMigrationDiscovery.Discover(migrationsAssembly);
        var applied = await VersionInfoReader
            .ReadAsync(connection, versionInfo, cancellationToken)
            .ConfigureAwait(false);

        var plan = MigrationPlanBuilder.Build(
            available,
            applied,
            targetVersion,
            versionInfo.FailWhenDatabaseAhead,
            versionInfo.RequireAppliedVersionsInAssembly);

        if (!plan.TargetExists || plan.Late.Count > 0 || plan.DatabaseAhead || plan.UnknownApplied.Count > 0)
        {
            throw new MigrationHistoryException(BuildErrorMessage(plan), plan);
        }

        return plan;
    }

    public static async Task VerifyAfterUpAsync(
        DbConnection connection,
        Assembly migrationsAssembly,
        long targetVersion,
        VersionInfoConfiguration versionInfo,
        CancellationToken cancellationToken = default)
    {
        var available = AssemblyMigrationDiscovery.Discover(migrationsAssembly);
        var applied = await VersionInfoReader
            .ReadAsync(connection, versionInfo, cancellationToken)
            .ConfigureAwait(false);

        var plan = MigrationPlanBuilder.Build(
            available,
            applied,
            targetVersion,
            failWhenDatabaseAhead: false,
            requireAppliedVersionsInAssembly: versionInfo.RequireAppliedVersionsInAssembly);

        if (!plan.TargetExists || plan.Pending.Count > 0)
        {
            var message = !plan.TargetExists
                ? $"target_version {targetVersion} nie odpowiada żadnej migracji w assembly."
                : "Migrator zakończył pracę, ale nie wszystkie oczekiwane migracje znajdują się w VersionInfo.\n" +
                  FormatMigrations("Brakujące migracje", plan.Pending);

            throw new MigrationHistoryException(message, plan);
        }
    }

    public static async Task<MigrationDownPlan> ValidateBeforeDownAsync(
        DbConnection connection,
        Assembly migrationsAssembly,
        long targetVersion,
        VersionInfoConfiguration versionInfo,
        CancellationToken cancellationToken = default)
    {
        var available = AssemblyMigrationDiscovery.Discover(migrationsAssembly);
        var applied = await VersionInfoReader
            .ReadAsync(connection, versionInfo, cancellationToken)
            .ConfigureAwait(false);

        var plan = MigrationDownPlanBuilder.Build(available, applied, targetVersion);
        if (!plan.IsSafe)
        {
            throw new MigrationRollbackException(BuildDownErrorMessage(plan), plan);
        }

        return plan;
    }

    public static async Task VerifyAfterDownAsync(
        DbConnection connection,
        Assembly migrationsAssembly,
        long targetVersion,
        VersionInfoConfiguration versionInfo,
        CancellationToken cancellationToken = default)
    {
        var available = AssemblyMigrationDiscovery.Discover(migrationsAssembly);
        var applied = await VersionInfoReader
            .ReadAsync(connection, versionInfo, cancellationToken)
            .ConfigureAwait(false);

        var plan = MigrationDownPlanBuilder.Build(available, applied, targetVersion);
        var versionsStillAboveTarget = applied
            .Where(x => x.Version > targetVersion)
            .OrderByDescending(x => x.Version)
            .ToArray();

        if (!plan.TargetExists || !plan.TargetApplied || versionsStillAboveTarget.Length > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Migrator zakończył rollback, ale VersionInfo nie osiągnęło oczekiwanego stanu.");

            if (!plan.TargetExists)
            {
                builder.AppendLine(
                    $"Wersja docelowa {targetVersion} nie istnieje w assembly migracyjnym.");
            }

            if (!plan.TargetApplied)
            {
                builder.AppendLine(
                    $"Wersja docelowa {targetVersion} nie istnieje w VersionInfo.");
            }

            if (versionsStillAboveTarget.Length > 0)
            {
                builder.AppendLine("Wersje nadal obecne powyżej celu:");
                foreach (var migration in versionsStillAboveTarget)
                {
                    builder.AppendLine($"  - {migration.Version} {migration.Description}");
                }
            }

            throw new MigrationRollbackException(builder.ToString().TrimEnd(), plan);
        }
    }

    public static string FormatPlan(MigrationPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Plan migracji bazy");
        builder.AppendLine($"Target version: {plan.TargetVersion}");
        builder.AppendLine($"Najwyższa wdrożona wersja: {plan.HighestApplied}");
        if (!plan.TargetExists)
        {
            builder.AppendLine("UWAGA: target_version nie istnieje w assembly migracyjnym.");
        }
        builder.AppendLine();

        if (plan.Pending.Count == 0)
        {
            builder.AppendLine("Brak migracji do wykonania.");
        }
        else
        {
            builder.Append(FormatMigrations("Migracje do wykonania", plan.Pending));
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatDownPlan(MigrationDownPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Plan wycofania migracji");
        builder.AppendLine($"Aktualna wersja: {plan.HighestApplied}");
        builder.AppendLine($"Wersja docelowa: {plan.TargetVersion}");
        builder.AppendLine();

        if (plan.ToRollback.Count == 0)
        {
            builder.AppendLine("Brak migracji do wycofania.");
        }
        else
        {
            builder.Append(FormatMigrations("Migracje do wycofania", plan.ToRollback));
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildErrorMessage(MigrationPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DATABASE MIGRATION BLOCKED");
        builder.AppendLine("Nie wykonano żadnej migracji.");
        builder.AppendLine();

        if (!plan.TargetExists)
        {
            builder.AppendLine(
                $"target_version {plan.TargetVersion} nie odpowiada żadnej migracji w assembly.");
        }

        if (plan.Late.Count > 0)
        {
            builder.AppendLine(
                "Artefakt zawiera migracje starsze od najwyższej wdrożonej wersji, " +
                "których nie ma w VersionInfo.");
            builder.Append(FormatMigrations("Pominięte migracje", plan.Late));
        }

        if (plan.DatabaseAhead)
        {
            builder.AppendLine(
                $"Baza ma wersję {plan.HighestApplied}, która jest wyższa od target_version " +
                $"artefaktu ({plan.TargetVersion}).");
        }

        if (plan.UnknownApplied.Count > 0)
        {
            builder.AppendLine("VersionInfo zawiera wersje, których nie ma w assembly migracyjnym:");
            foreach (var migration in plan.UnknownApplied)
            {
                builder.AppendLine($"  - {migration.Version} {migration.Description}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildDownErrorMessage(MigrationDownPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DATABASE ROLLBACK BLOCKED");
        builder.AppendLine("Nie wycofano żadnej migracji.");
        builder.AppendLine();

        if (!plan.TargetExists)
        {
            builder.AppendLine(
                $"Wersja docelowa {plan.TargetVersion} nie istnieje w assembly migracyjnym.");
        }

        if (!plan.TargetApplied)
        {
            builder.AppendLine(
                $"Wersja docelowa {plan.TargetVersion} nie istnieje w VersionInfo. " +
                "Wskaż wdrożoną migrację albo 0.");
        }

        if (plan.UnavailableToRollback.Count > 0)
        {
            builder.AppendLine(
                "Nie można bezpiecznie wykonać Down, ponieważ brakuje implementacji " +
                "wdrożonych migracji:");
            foreach (var migration in plan.UnavailableToRollback)
            {
                builder.AppendLine($"  - {migration.Version} {migration.Description}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatMigrations(
        string title,
        IEnumerable<RuntimeMigration> migrations)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title + ":");
        foreach (var migration in migrations)
        {
            builder.AppendLine($"  - {migration.Version} {migration.Name}");
        }

        return builder.ToString();
    }
}
