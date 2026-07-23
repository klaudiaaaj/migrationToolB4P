using System.Text.Json;
using System.Text.Json.Serialization;

namespace MigrationTool.Core.Configuration;

public sealed class MigrationToolConfiguration
{
    public List<MigrationServiceConfiguration> Services { get; init; } = [];

    public MigrationServiceConfiguration GetRequiredService(string name)
    {
        var service = Services.SingleOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        return service ?? throw new InvalidOperationException(
            $"Nie znaleziono konfiguracji mikroserwisu '{name}'.");
    }

    public IReadOnlyList<MigrationServiceConfiguration> SelectServices(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? Services
            : [GetRequiredService(name)];

    public static MigrationToolConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Nie znaleziono pliku konfiguracji MigrationTool.", path);
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        options.Converters.Add(new JsonStringEnumConverter());

        var configuration = JsonSerializer.Deserialize<MigrationToolConfiguration>(json, options)
            ?? throw new InvalidOperationException("Plik konfiguracji jest pusty lub niepoprawny.");

        if (configuration.Services.Count == 0)
        {
            throw new InvalidOperationException("Konfiguracja musi zawierać co najmniej jeden mikroserwis.");
        }

        foreach (var service in configuration.Services)
        {
            service.Validate();
        }

        return configuration;
    }
}

public sealed class MigrationServiceConfiguration
{
    public required string Name { get; init; }
    public required string MigrationRoot { get; init; }
    public required string Namespace { get; init; }
    public List<TargetVersionFileConfiguration> TargetVersionFiles { get; init; } = [];
    public VersionInfoConfiguration VersionInfo { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Nazwa mikroserwisu nie może być pusta.");
        }

        if (string.IsNullOrWhiteSpace(MigrationRoot))
        {
            throw new InvalidOperationException($"MigrationRoot dla '{Name}' nie może być pusty.");
        }

        if (string.IsNullOrWhiteSpace(Namespace))
        {
            throw new InvalidOperationException($"Namespace dla '{Name}' nie może być pusty.");
        }

        if (TargetVersionFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"Mikroserwis '{Name}' musi wskazywać co najmniej jeden plik z target_version.");
        }
    }
}

public sealed class TargetVersionFileConfiguration
{
    public required string Path { get; init; }
    public string PropertyName { get; init; } = "target_version";
}

public sealed class VersionInfoConfiguration
{
    public string? Schema { get; init; }
    public string Table { get; init; } = "VersionInfo";
    public string VersionColumn { get; init; } = "Version";
    public string DescriptionColumn { get; init; } = "Description";
    public string AppliedOnColumn { get; init; } = "AppliedOn";
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.SqlServer;
    public bool FailWhenDatabaseAhead { get; init; } = true;
    public bool TreatMissingVersionInfoAsEmpty { get; init; } = true;
    public bool RequireAppliedVersionsInAssembly { get; init; }
}

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql,
    Sqlite
}
