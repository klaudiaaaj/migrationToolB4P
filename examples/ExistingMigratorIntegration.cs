using System.Data.Common;
using FluentMigrator.Runner;
using Microsoft.Extensions.Logging;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Runtime;

namespace YourCompany.MigrationToolPackage;

public sealed class SafeMigrationRunner
{
    private readonly IMigrationRunner _runner;
    private readonly DbConnection _connection;
    private readonly ILogger<SafeMigrationRunner> _logger;
    private readonly IMigrationDatabaseLock _migrationLock;
    private readonly VersionInfoConfiguration _versionInfo;

    public SafeMigrationRunner(
        IMigrationRunner runner,
        DbConnection connection,
        ILogger<SafeMigrationRunner> logger,
        IMigrationDatabaseLock migrationLock,
        VersionInfoConfiguration versionInfo)
    {
        _runner = runner;
        _connection = connection;
        _logger = logger;
        _migrationLock = migrationLock;
        _versionInfo = versionInfo;
    }

    public async Task UpAsync(
        long targetVersion,
        CancellationToken cancellationToken = default)
    {
        var migrationsAssembly = typeof(Program).Assembly;

        // Lock musi obejmować walidację, wykonanie i weryfikację końcową.
        await using var migrationLock = await _migrationLock.AcquireAsync(cancellationToken);

        // Guard czyta całą tabelę VersionInfo. Nie opiera się na MAX(Version).
        var plan = await MigrationRuntimeGuard.ValidateBeforeUpAsync(
            _connection,
            migrationsAssembly,
            targetVersion,
            _versionInfo,
            cancellationToken);

        _logger.LogInformation("{MigrationPlan}", MigrationRuntimeGuard.FormatPlan(plan));

        // Tutaj pozostaje Wasz obecny runner, logowanie, obsługa schematów i lock DB.
        _runner.MigrateUp(targetVersion);

        // Ochrona przed sytuacją: runner zakończył się bez błędu, ale czegoś nie zapisał.
        await MigrationRuntimeGuard.VerifyAfterUpAsync(
            _connection,
            migrationsAssembly,
            targetVersion,
            _versionInfo,
            cancellationToken);
    }
}


public interface IMigrationDatabaseLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}
