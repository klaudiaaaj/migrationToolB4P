using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
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
    bool IsDryRun);

public sealed class MigrationSafetyException(string message) : Exception(message);

public sealed class MigrationToolRunner
{
    private readonly Assembly _migrationsAssembly;

    public MigrationToolRunner(Assembly migrationsAssembly)
    {
        _migrationsAssembly = migrationsAssembly ??
            throw new ArgumentNullException(nameof(migrationsAssembly));
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

        using var serviceProvider = BuildFluentMigratorServices(options);
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();
        var availableVersions = runner.MigrationLoader
            .LoadMigrations()
            .Keys
            .ToHashSet();
        var applied = ReadAppliedMigrations(versionLoader);
        var currentVersion = applied.DefaultIfEmpty(0).Max();
        var direction = ResolveDirection(currentVersion, options.Version);

        ValidateState(availableVersions, applied, currentVersion, options.Version, direction);

        if (direction == MigrationDirection.None)
        {
            return new MigrationRunResult(
                MigrationDirection.None,
                currentVersion,
                options.Version,
                options.IsDryRun);
        }

        runToken.ThrowIfCancellationRequested();

        if (direction == MigrationDirection.Up)
        {
            runner.MigrateUp(options.Version);
        }
        else
        {
            runner.MigrateDown(options.Version);
        }

        if (!options.IsDryRun)
        {
            var appliedAfter = ReadAppliedMigrations(versionLoader);
            VerifyFinalState(availableVersions, appliedAfter, options.Version);
        }

        return new MigrationRunResult(
            direction,
            currentVersion,
            options.Version,
            options.IsDryRun);
    }

    private ServiceProvider BuildFluentMigratorServices(MigrationOptions options)
    {
        var versionTable = new MigrationVersionTable(options.SchemaName);

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

    private static void ValidateState(
        IReadOnlySet<long> available,
        IReadOnlySet<long> applied,
        long currentVersion,
        long targetVersion,
        MigrationDirection direction)
    {
        if (targetVersion != 0 && !available.Contains(targetVersion))
        {
            throw new MigrationSafetyException(
                $"Wersja docelowa {targetVersion} nie istnieje w assembly migracyjnym.");
        }

        var missingFromAssembly = applied
            .Where(version => !available.Contains(version))
            .OrderBy(version => version)
            .ToArray();
        if (missingFromAssembly.Length > 0)
        {
            throw new MigrationSafetyException(
                "VersionInfo zawiera migracje, których nie ma w assembly: " +
                string.Join(", ", missingFromAssembly));
        }

        var skipped = available
            .Where(version => version < currentVersion && !applied.Contains(version))
            .OrderBy(version => version)
            .ToArray();
        if (skipped.Length > 0)
        {
            throw new MigrationSafetyException(
                "Wykryto pominięte migracje starsze od aktualnej wersji bazy: " +
                string.Join(", ", skipped));
        }

        if (direction == MigrationDirection.Down &&
            targetVersion != 0 &&
            !applied.Contains(targetVersion))
        {
            throw new MigrationSafetyException(
                $"Nie można wykonać down. Wersja {targetVersion} nie istnieje w VersionInfo.");
        }
    }

    private static void VerifyFinalState(
        IReadOnlySet<long> available,
        IReadOnlySet<long> applied,
        long targetVersion)
    {
        var expected = available
            .Where(version => version <= targetVersion)
            .ToHashSet();

        if (!applied.SetEquals(expected))
        {
            throw new MigrationSafetyException(
                $"Migracja nie osiągnęła wersji {targetVersion}. " +
                $"Oczekiwano [{string.Join(", ", expected.Order())}], " +
                $"VersionInfo zawiera [{string.Join(", ", applied.Order())}].");
        }
    }

    private static IReadOnlySet<long> ReadAppliedMigrations(IVersionLoader versionLoader)
    {
        versionLoader.LoadVersionInfo();

        return versionLoader.VersionInfo
            .AppliedMigrations()
            .ToHashSet();
    }

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
        private readonly string _schemaName;

        public MigrationVersionTable(string schemaName)
        {
            _schemaName = schemaName;
        }

        public bool OwnsSchema => true;
        public string SchemaName => _schemaName;
        public string TableName => "VersionInfo";
        public string ColumnName => "Version";
        public string DescriptionColumnName => "Description";
        public string UniqueIndexName => "UC_Version";
        public string AppliedOnColumnName => "AppliedOn";
        public bool CreateWithPrimaryKey => false;
    }
}
