using System.Data.Common;
using System.Reflection;
using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Runtime;

public enum MigrationDirection
{
    None,
    Up,
    Down
}

public sealed record MigrationRunResult(
    MigrationDirection Direction,
    long CurrentVersion,
    long TargetVersion,
    bool IsDryRun,
    string Plan);

public interface IMigrationExecutor
{
    Assembly MigrationsAssembly { get; }

    DbConnection CreateConnection(MigrationOptions options);

    Task<IAsyncDisposable> AcquireLockAsync(
        MigrationOptions options,
        CancellationToken cancellationToken);

    Task MigrateUpAsync(
        MigrationOptions options,
        CancellationToken cancellationToken);

    Task MigrateDownAsync(
        MigrationOptions options,
        CancellationToken cancellationToken);
}

public sealed class MigrationToolRunner
{
    private readonly IMigrationExecutor _executor;
    private readonly VersionInfoConfiguration _versionInfoDefaults;

    public MigrationToolRunner(
        IMigrationExecutor executor,
        VersionInfoConfiguration? versionInfoDefaults = null)
    {
        _executor = executor;
        _versionInfoDefaults = versionInfoDefaults ?? new VersionInfoConfiguration();
    }

    public async Task<MigrationRunResult> Run(
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
        var runToken = timeout.Token;

        await using var migrationLock = await _executor
            .AcquireLockAsync(options, runToken)
            .ConfigureAwait(false);
        await using var connection = _executor.CreateConnection(options);

        var versionInfo = BuildVersionInfoConfiguration(options);
        var applied = await VersionInfoReader
            .ReadAsync(connection, versionInfo, runToken)
            .ConfigureAwait(false);
        var currentVersion = applied.Select(x => x.Version).DefaultIfEmpty(0).Max();
        var direction = ResolveDirection(currentVersion, options.Version);

        return direction switch
        {
            MigrationDirection.Up => await RunUp(
                connection,
                options,
                versionInfo,
                currentVersion,
                runToken).ConfigureAwait(false),
            MigrationDirection.Down => await RunDown(
                connection,
                options,
                versionInfo,
                currentVersion,
                runToken).ConfigureAwait(false),
            _ => new MigrationRunResult(
                MigrationDirection.None,
                currentVersion,
                options.Version,
                options.IsDryRun,
                $"Baza jest już w wersji {currentVersion}. Brak migracji do wykonania.")
        };
    }

    private static MigrationDirection ResolveDirection(long currentVersion, long targetVersion)
        => targetVersion.CompareTo(currentVersion) switch
        {
            > 0 => MigrationDirection.Up,
            < 0 => MigrationDirection.Down,
            _ => MigrationDirection.None
        };

    private async Task<MigrationRunResult> RunUp(
        DbConnection connection,
        MigrationOptions options,
        VersionInfoConfiguration versionInfo,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        var plan = await MigrationRuntimeGuard.ValidateBeforeUpAsync(
            connection,
            _executor.MigrationsAssembly,
            options.Version,
            versionInfo,
            cancellationToken).ConfigureAwait(false);
        var formattedPlan = MigrationRuntimeGuard.FormatPlan(plan);

        await _executor
            .MigrateUpAsync(options, cancellationToken)
            .ConfigureAwait(false);

        if (!options.IsDryRun)
        {
            await MigrationRuntimeGuard.VerifyAfterUpAsync(
                connection,
                _executor.MigrationsAssembly,
                options.Version,
                versionInfo,
                cancellationToken).ConfigureAwait(false);
        }

        return new MigrationRunResult(
            MigrationDirection.Up,
            currentVersion,
            options.Version,
            options.IsDryRun,
            formattedPlan);
    }

    private async Task<MigrationRunResult> RunDown(
        DbConnection connection,
        MigrationOptions options,
        VersionInfoConfiguration versionInfo,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        var plan = await MigrationRuntimeGuard.ValidateBeforeDownAsync(
            connection,
            _executor.MigrationsAssembly,
            options.Version,
            versionInfo,
            cancellationToken).ConfigureAwait(false);
        var formattedPlan = MigrationRuntimeGuard.FormatDownPlan(plan);

        await _executor
            .MigrateDownAsync(options, cancellationToken)
            .ConfigureAwait(false);

        if (!options.IsDryRun)
        {
            await MigrationRuntimeGuard.VerifyAfterDownAsync(
                connection,
                _executor.MigrationsAssembly,
                options.Version,
                versionInfo,
                cancellationToken).ConfigureAwait(false);
        }

        return new MigrationRunResult(
            MigrationDirection.Down,
            currentVersion,
            options.Version,
            options.IsDryRun,
            formattedPlan);
    }

    private VersionInfoConfiguration BuildVersionInfoConfiguration(MigrationOptions options)
        => new()
        {
            Schema = options.SchemaName,
            Table = _versionInfoDefaults.Table,
            VersionColumn = _versionInfoDefaults.VersionColumn,
            DescriptionColumn = _versionInfoDefaults.DescriptionColumn,
            AppliedOnColumn = _versionInfoDefaults.AppliedOnColumn,
            Provider = _versionInfoDefaults.Provider,
            FailWhenDatabaseAhead = _versionInfoDefaults.FailWhenDatabaseAhead,
            TreatMissingVersionInfoAsEmpty = _versionInfoDefaults.TreatMissingVersionInfoAsEmpty,
            RequireAppliedVersionsInAssembly = _versionInfoDefaults.RequireAppliedVersionsInAssembly
        };
}
