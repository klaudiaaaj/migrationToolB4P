using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Runtime;

public sealed record MigrationPlan(
    long TargetVersion,
    bool TargetExists,
    IReadOnlyList<AppliedMigration> Applied,
    IReadOnlyList<RuntimeMigration> Pending,
    IReadOnlyList<RuntimeMigration> Late,
    IReadOnlyList<AppliedMigration> UnknownApplied,
    bool DatabaseAhead)
{
    public long HighestApplied => Applied.Select(x => x.Version).DefaultIfEmpty(0).Max();
    public bool IsSafe => TargetExists && Late.Count == 0 && !DatabaseAhead && UnknownApplied.Count == 0;
}

public static class MigrationPlanBuilder
{
    public static MigrationPlan Build(
        IReadOnlyList<RuntimeMigration> available,
        IReadOnlyList<AppliedMigration> applied,
        long targetVersion,
        bool failWhenDatabaseAhead,
        bool requireAppliedVersionsInAssembly)
    {
        var appliedVersions = applied.Select(x => x.Version).ToHashSet();
        var availableVersions = available.Select(x => x.Version).ToHashSet();
        var highestApplied = appliedVersions.DefaultIfEmpty(0).Max();
        var targetExists = targetVersion == 0 || availableVersions.Contains(targetVersion);

        var pending = available
            .Where(x => x.Version <= targetVersion)
            .Where(x => !appliedVersions.Contains(x.Version))
            .OrderBy(x => x.Version)
            .ToArray();

        var late = pending
            .Where(x => x.Version < highestApplied)
            .ToArray();

        var unknownApplied = requireAppliedVersionsInAssembly
            ? applied.Where(x => !availableVersions.Contains(x.Version)).OrderBy(x => x.Version).ToArray()
            : Array.Empty<AppliedMigration>();

        var databaseAhead = failWhenDatabaseAhead && highestApplied > targetVersion;

        return new MigrationPlan(
            targetVersion,
            targetExists,
            applied.OrderBy(x => x.Version).ToArray(),
            pending,
            late,
            unknownApplied,
            databaseAhead);
    }
}
