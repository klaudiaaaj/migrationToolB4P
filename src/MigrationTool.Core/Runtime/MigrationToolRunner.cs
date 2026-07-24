using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;

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

public sealed class MigrationToolRunner
{
    private readonly Assembly _migrationsAssembly;
    private readonly VersionInfoConfiguration _versionInfoDefaults;

    public MigrationToolRunner(
        Assembly migrationsAssembly,
        VersionInfoConfiguration? versionInfoDefaults = null)
    {
        _migrationsAssembly = migrationsAssembly ??
            throw new ArgumentNullException(nameof(migrationsAssembly));
        _versionInfoDefaults = versionInfoDefaults ?? new VersionInfoConfiguration();

        if (_versionInfoDefaults.Provider != DatabaseProvider.SqlServer)
        {
            throw new NotSupportedException(
                "MigrationToolRunner jest skonfigurowany bezpośrednio dla SQL Server.");
        }
    }

    public async Task<MigrationRunResult> Run(
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
        var runToken = timeout.Token;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(runToken).ConfigureAwait(false);
        await AcquireDatabaseLock(connection, options, runToken).ConfigureAwait(false);

        var versionInfo = BuildVersionInfoConfiguration(options);
        using var serviceProvider = BuildFluentMigratorServices(options, versionInfo);
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();
        var applied = ReadAppliedMigrations(versionLoader);
        var currentVersion = applied.Select(x => x.Version).DefaultIfEmpty(0).Max();
        var direction = ResolveDirection(currentVersion, options.Version);

        if (direction == MigrationDirection.None)
        {
            return new MigrationRunResult(
                MigrationDirection.None,
                currentVersion,
                options.Version,
                options.IsDryRun,
                $"Baza jest już w wersji {currentVersion}. Brak migracji do wykonania.");
        }

        return direction == MigrationDirection.Up
            ? RunUp(
                runner,
                versionLoader,
                options,
                versionInfo,
                currentVersion,
                runToken)
            : RunDown(
                runner,
                versionLoader,
                options,
                currentVersion,
                runToken);
    }

    private ServiceProvider BuildFluentMigratorServices(
        MigrationOptions options,
        VersionInfoConfiguration versionInfo)
    {
        var versionTable = new MigrationVersionTable(versionInfo);

        return new ServiceCollection()
            .AddSingleton(options)
            .AddFluentMigratorCore()
            .ConfigureRunner(builder => builder
                .AddSqlServer()
                .WithGlobalConnectionString(options.ConnectionString)
                .WithGlobalCommandTimeout(TimeSpan.FromSeconds(options.Timeout))
                .WithVersionTable(versionTable)
                .ConfigureGlobalProcessorOptions(processor =>
                {
                    processor.PreviewOnly = options.IsDryRun;
                    processor.Timeout = TimeSpan.FromSeconds(options.Timeout);
                })
                .ScanIn(_migrationsAssembly).For.All())
            .AddLogging(logging => logging.AddFluentMigratorConsole())
            .BuildServiceProvider(validateScopes: true);
    }

    private static MigrationDirection ResolveDirection(long currentVersion, long targetVersion)
        => targetVersion.CompareTo(currentVersion) switch
        {
            > 0 => MigrationDirection.Up,
            < 0 => MigrationDirection.Down,
            _ => MigrationDirection.None
        };

    private MigrationRunResult RunUp(
        IMigrationRunner runner,
        IVersionLoader versionLoader,
        MigrationOptions options,
        VersionInfoConfiguration versionInfo,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        var applied = ReadAppliedMigrations(versionLoader);
        var plan = MigrationRuntimeGuard.ValidateBeforeUp(
            _migrationsAssembly,
            applied,
            options.Version,
            versionInfo);
        var formattedPlan = MigrationRuntimeGuard.FormatPlan(plan);

        cancellationToken.ThrowIfCancellationRequested();
        runner.MigrateUp(options.Version);

        if (!options.IsDryRun)
        {
            var appliedAfter = ReadAppliedMigrations(versionLoader);
            MigrationRuntimeGuard.VerifyAfterUp(
                _migrationsAssembly,
                appliedAfter,
                options.Version,
                versionInfo);
        }

        return new MigrationRunResult(
            MigrationDirection.Up,
            currentVersion,
            options.Version,
            options.IsDryRun,
            formattedPlan);
    }

    private MigrationRunResult RunDown(
        IMigrationRunner runner,
        IVersionLoader versionLoader,
        MigrationOptions options,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        var applied = ReadAppliedMigrations(versionLoader);
        var plan = MigrationRuntimeGuard.ValidateBeforeDown(
            _migrationsAssembly,
            applied,
            options.Version);
        var formattedPlan = MigrationRuntimeGuard.FormatDownPlan(plan);

        cancellationToken.ThrowIfCancellationRequested();
        runner.MigrateDown(options.Version);

        if (!options.IsDryRun)
        {
            var appliedAfter = ReadAppliedMigrations(versionLoader);
            MigrationRuntimeGuard.VerifyAfterDown(
                _migrationsAssembly,
                appliedAfter,
                options.Version);
        }

        return new MigrationRunResult(
            MigrationDirection.Down,
            currentVersion,
            options.Version,
            options.IsDryRun,
            formattedPlan);
    }

    private static IReadOnlyList<AppliedMigration> ReadAppliedMigrations(
        IVersionLoader versionLoader)
    {
        versionLoader.LoadVersionInfo();

        return versionLoader.VersionInfo
            .AppliedMigrations()
            .Distinct()
            .OrderBy(version => version)
            .Select(version => new AppliedMigration(version))
            .ToArray();
    }

    private VersionInfoConfiguration BuildVersionInfoConfiguration(MigrationOptions options)
        => new()
        {
            Schema = options.SchemaName,
            Table = _versionInfoDefaults.Table,
            VersionColumn = _versionInfoDefaults.VersionColumn,
            DescriptionColumn = _versionInfoDefaults.DescriptionColumn,
            AppliedOnColumn = _versionInfoDefaults.AppliedOnColumn,
            Provider = DatabaseProvider.SqlServer,
            FailWhenDatabaseAhead = _versionInfoDefaults.FailWhenDatabaseAhead,
            TreatMissingVersionInfoAsEmpty = _versionInfoDefaults.TreatMissingVersionInfoAsEmpty,
            RequireAppliedVersionsInAssembly = _versionInfoDefaults.RequireAppliedVersionsInAssembly
        };

    private static async Task AcquireDatabaseLock(
        SqlConnection connection,
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.Timeout;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = @lockTimeout;
            SELECT @result;
            """;
        command.Parameters.AddWithValue("@resource", BuildLockResource(connection, options));
        command.Parameters.AddWithValue(
            "@lockTimeout",
            checked(options.Timeout * 1000));

        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        if (result < 0)
        {
            throw new TimeoutException(
                $"Nie udało się uzyskać blokady migracji. sp_getapplock zwrócił {result}.");
        }
    }

    private static string BuildLockResource(
        SqlConnection connection,
        MigrationOptions options)
        => $"MigrationTool:{connection.Database}:{options.SchemaName}";

    private sealed class MigrationVersionTable : IVersionTableMetaData
    {
        private readonly VersionInfoConfiguration _configuration;

        public MigrationVersionTable(VersionInfoConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool OwnsSchema => true;
        public string SchemaName => _configuration.Schema ?? string.Empty;
        public string TableName => _configuration.Table;
        public string ColumnName => _configuration.VersionColumn;
        public string DescriptionColumnName => _configuration.DescriptionColumn;
        public string UniqueIndexName => "UC_Version";
        public string AppliedOnColumnName => _configuration.AppliedOnColumn;
        public bool CreateWithPrimaryKey => false;
    }
}
