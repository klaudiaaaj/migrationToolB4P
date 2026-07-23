using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Runtime;

public static class VersionInfoReader
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<AppliedMigration>> ReadAsync(
        DbConnection connection,
        VersionInfoConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(configuration.Table, nameof(configuration.Table));
        ValidateIdentifier(configuration.VersionColumn, nameof(configuration.VersionColumn));
        ValidateIdentifier(configuration.DescriptionColumn, nameof(configuration.DescriptionColumn));
        ValidateIdentifier(configuration.AppliedOnColumn, nameof(configuration.AppliedOnColumn));

        if (!string.IsNullOrWhiteSpace(configuration.Schema))
        {
            ValidateIdentifier(configuration.Schema, nameof(configuration.Schema));
        }

        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (configuration.TreatMissingVersionInfoAsEmpty &&
                !await TableExistsAsync(connection, configuration, cancellationToken).ConfigureAwait(false))
            {
                return Array.Empty<AppliedMigration>();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = BuildSelectSql(configuration);

            var result = new List<AppliedMigration>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var version = Convert.ToInt64(
                    reader.GetValue(0),
                    System.Globalization.CultureInfo.InvariantCulture);
                var description = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
                var appliedOn = reader.IsDBNull(2) ? null : ToDateTimeOffset(reader.GetValue(2));
                result.Add(new AppliedMigration(version, description, appliedOn));
            }

            return result;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        VersionInfoConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = configuration.Provider switch
        {
            DatabaseProvider.SqlServer =>
                "SELECT CASE WHEN OBJECT_ID(@fullName, 'U') IS NULL THEN 0 ELSE 1 END;",
            DatabaseProvider.PostgreSql =>
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = @schema AND table_name = @table) THEN 1 ELSE 0 END;",
            DatabaseProvider.MySql when string.IsNullOrWhiteSpace(configuration.Schema) =>
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = DATABASE() AND table_name = @table) THEN 1 ELSE 0 END;",
            DatabaseProvider.MySql =>
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = @schema AND table_name = @table) THEN 1 ELSE 0 END;",
            DatabaseProvider.Sqlite =>
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM sqlite_master " +
                "WHERE type = 'table' AND name = @table) THEN 1 ELSE 0 END;",
            _ => throw new ArgumentOutOfRangeException()
        };

        if (configuration.Provider == DatabaseProvider.SqlServer)
        {
            var fullName = string.IsNullOrWhiteSpace(configuration.Schema)
                ? configuration.Table
                : configuration.Schema + "." + configuration.Table;
            AddParameter(command, "@fullName", fullName);
        }
        else
        {
            AddParameter(command, "@table", configuration.Table);

            if (configuration.Provider == DatabaseProvider.PostgreSql)
            {
                AddParameter(command, "@schema", configuration.Schema ?? "public");
            }
            else if (configuration.Provider == DatabaseProvider.MySql &&
                     !string.IsNullOrWhiteSpace(configuration.Schema))
            {
                AddParameter(command, "@schema", configuration.Schema);
            }
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static string BuildSelectSql(VersionInfoConfiguration configuration)
    {
        var table = Quote(configuration.Table, configuration.Provider);
        if (!string.IsNullOrWhiteSpace(configuration.Schema))
        {
            table = Quote(configuration.Schema, configuration.Provider) + "." + table;
        }

        var version = Quote(configuration.VersionColumn, configuration.Provider);
        var description = Quote(configuration.DescriptionColumn, configuration.Provider);
        var appliedOn = Quote(configuration.AppliedOnColumn, configuration.Provider);

        return $"SELECT {version}, {description}, {appliedOn} FROM {table} ORDER BY {version};";
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string identifier, DatabaseProvider provider)
        => provider switch
        {
            DatabaseProvider.SqlServer => $"[{identifier}]",
            DatabaseProvider.MySql => $"`{identifier}`",
            DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite => $"\"{identifier}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    private static void ValidateIdentifier(string identifier, string parameterName)
    {
        if (!IdentifierPattern.IsMatch(identifier))
        {
            throw new ArgumentException(
                $"Niepoprawny identyfikator SQL '{identifier}'. Dozwolone są litery, cyfry i znak '_'.",
                parameterName);
        }
    }

    private static DateTimeOffset? ToDateTimeOffset(object value)
        => value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            _ when DateTimeOffset.TryParse(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                out var parsed) => parsed,
            _ => null
        };
}
