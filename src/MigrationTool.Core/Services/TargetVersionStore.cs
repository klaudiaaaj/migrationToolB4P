using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Services;

public sealed class TargetVersionStore
{
    public long Read(
        string repositoryRoot,
        MigrationToolConfiguration configuration)
    {
        var path = Resolve(repositoryRoot, configuration.TargetVersionFile);
        var text = File.ReadAllText(path);
        var matches = BuildPattern(configuration.TargetVersionProperty).Matches(text);

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"W pliku '{configuration.TargetVersionFile}' oczekiwano dokładnie jednej właściwości " +
                $"'{configuration.TargetVersionProperty}', znaleziono: {matches.Count}.");
        }

        return long.Parse(matches[0].Groups["value"].Value);
    }

    public void Write(
        string repositoryRoot,
        MigrationToolConfiguration configuration,
        long version)
    {
        var path = Resolve(repositoryRoot, configuration.TargetVersionFile);
        var text = File.ReadAllText(path);
        var pattern = BuildPattern(configuration.TargetVersionProperty);
        var matches = pattern.Matches(text);

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"W pliku '{configuration.TargetVersionFile}' oczekiwano dokładnie jednej właściwości " +
                $"'{configuration.TargetVersionProperty}', znaleziono: {matches.Count}.");
        }

        var updated = pattern.Replace(
            text,
            match => match.Groups["prefix"].Value + version + match.Groups["suffix"].Value,
            1);

        File.WriteAllText(path, updated);
    }

    private static Regex BuildPattern(string propertyName)
    {
        var escapedName = Regex.Escape(propertyName);
        return new Regex(
            $"(?<prefix>\\\"{escapedName}\\\"\\s*:\\s*\\\"?)(?<value>[0-9]{{1,18}})(?<suffix>\\\"?)(?![0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static string Resolve(string repositoryRoot, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Nie znaleziono pliku z TargetVersion.", path);
        }

        return path;
    }
}
