using System.Data.Common;
using System.Reflection;
using FluentMigrator.Runner;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Runtime;

namespace YourCompany.MigrationToolPackage;

// To jest jedyny publiczny punkt wejścia biblioteki używany przez aplikację.
public sealed class MigrationTool
{
    private readonly MigrationToolRunner _runner;

    public MigrationTool(
        IConfiguredMigrationRunnerFactory runnerFactory,
        IMigrationConnectionFactory connectionFactory,
        IMigrationDatabaseLock migrationLock)
    {
        _runner = new MigrationToolRunner(
            new FluentMigrationExecutor(runnerFactory, connectionFactory, migrationLock));
    }

    public Task<MigrationRunResult> Run(
        MigrationOptions options,
        CancellationToken cancellationToken = default)
        => _runner.Run(options, cancellationToken);
}

internal sealed class FluentMigrationExecutor : IMigrationExecutor
{
    private readonly IConfiguredMigrationRunnerFactory _runnerFactory;
    private readonly IMigrationConnectionFactory _connectionFactory;
    private readonly IMigrationDatabaseLock _migrationLock;

    public FluentMigrationExecutor(
        IConfiguredMigrationRunnerFactory runnerFactory,
        IMigrationConnectionFactory connectionFactory,
        IMigrationDatabaseLock migrationLock)
    {
        _runnerFactory = runnerFactory;
        _connectionFactory = connectionFactory;
        _migrationLock = migrationLock;
    }

    public Assembly MigrationsAssembly => typeof(Program).Assembly;

    public DbConnection CreateConnection(MigrationOptions options)
        => _connectionFactory.Create(options.ConnectionString);

    public Task<IAsyncDisposable> AcquireLockAsync(
        MigrationOptions options,
        CancellationToken cancellationToken)
        => _migrationLock.AcquireAsync(options, cancellationToken);

    public Task MigrateUpAsync(
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runner = _runnerFactory.Create(options);
        runner.MigrateUp(options.Version);
        return Task.CompletedTask;
    }

    public Task MigrateDownAsync(
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runner = _runnerFactory.Create(options);
        runner.MigrateDown(options.Version);
        return Task.CompletedTask;
    }
}

// Fabryka powinna skonfigurować FluentMigratora wartościami z MigrationOptions:
// ConnectionString, SchemaName, ReportSchemaName, Timeout oraz PreviewOnly=IsDryRun.
public interface IConfiguredMigrationRunnerFactory
{
    IMigrationRunner Create(MigrationOptions options);
}

public interface IMigrationConnectionFactory
{
    DbConnection Create(string connectionString);
}

public interface IMigrationDatabaseLock
{
    Task<IAsyncDisposable> AcquireAsync(
        MigrationOptions options,
        CancellationToken cancellationToken);
}
