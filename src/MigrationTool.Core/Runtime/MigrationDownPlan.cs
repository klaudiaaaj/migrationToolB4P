using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Runtime;

public sealed record MigrationDownPlan(
    long TargetVersion,
    bool TargetExists,
    bool TargetApplied,
    IReadOnlyList<AppliedMigration> Applied,
    IReadOnlyList<RuntimeMigration> ToRollback,
    IReadOnlyList<AppliedMigration> UnavailableToRollback)
{
    public long HighestApplied => Applied.Select(x => x.Version).DefaultIfEmpty(0).Max();

    public bool IsSafe =>
        TargetExists &&
        TargetApplied &&
        UnavailableToRollback.Count == 0;
}

public static class MigrationDownPlanBuilder
{
    public static MigrationDownPlan Build(
        IReadOnlyList<RuntimeMigration> available,
        IReadOnlyList<AppliedMigration> applied,
        long targetVersion)
    {
        if (targetVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetVersion),
                "Wersja docelowa rollbacku nie może być ujemna.");
        }

        var availableByVersion = available
            .GroupBy(x => x.Version)
            .ToDictionary(x => x.Key, x => x.Single());
        var appliedVersions = applied.Select(x => x.Version).ToHashSet();
        var targetExists = targetVersion == 0 || availableByVersion.ContainsKey(targetVersion);
        var targetApplied = targetVersion == 0 || appliedVersions.Contains(targetVersion);

        var appliedAboveTarget = applied
            .Where(x => x.Version > targetVersion)
            .OrderByDescending(x => x.Version)
            .ToArray();

        var toRollback = appliedAboveTarget
            .Where(x => availableByVersion.ContainsKey(x.Version))
            .Select(x => availableByVersion[x.Version])
            .ToArray();

        var unavailableToRollback = appliedAboveTarget
            .Where(x => !availableByVersion.ContainsKey(x.Version))
            .ToArray();

        return new MigrationDownPlan(
            targetVersion,
            targetExists,
            targetApplied,
            applied.OrderBy(x => x.Version).ToArray(),
            toRollback,
            unavailableToRollback);
    }
}
