using System.Text.Json;

namespace MigrationTool.Core.Configuration;

public sealed class MigrationToolConfiguration
{
    public required string ProjectRoot { get; init; }
    public required string Namespace { get; init; }

    public string MigrationRoot => BuildProjectPath("Migrations");
    public string TargetVersionFile => BuildProjectPath("appsettings.json");
    public string TargetVersionProperty => "target_version";

    public static MigrationToolConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Nie znaleziono pliku konfiguracji MigrationTool.",
                path);
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var configuration = JsonSerializer.Deserialize<MigrationToolConfiguration>(json, options)
            ?? throw new InvalidOperationException(
                "Plik konfiguracji jest pusty lub niepoprawny.");

        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectRoot))
        {
            throw new InvalidOperationException("ProjectRoot nie może być pusty.");
        }

        if (string.IsNullOrWhiteSpace(Namespace))
        {
            throw new InvalidOperationException("Namespace nie może być pusty.");
        }

    }

    private string BuildProjectPath(string name)
    {
        var projectRoot = ProjectRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return projectRoot == "."
            ? name
            : Path.Combine(projectRoot, name);
    }
}
