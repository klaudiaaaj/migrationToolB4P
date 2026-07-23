using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Services;

public sealed class TargetVersionStore
{
    public long Read(string repositoryRoot, TargetVersionFileConfiguration configuration)
    {
        var path = Resolve(repositoryRoot, configuration.Path);
        var text = File.ReadAllText(path);
        var matches = BuildPattern(configuration.PropertyName).Matches(text);

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"W pliku '{configuration.Path}' oczekiwano dokładnie jednej właściwości " +
                $"'{configuration.PropertyName}', znaleziono: {matches.Count}.");
        }

        return long.Parse(matches[0].Groups["value"].Value);
    }

    public void Write(string repositoryRoot, TargetVersionFileConfiguration configuration, long version)
    {
        var path = Resolve(repositoryRoot, configuration.Path);
        var text = File.ReadAllText(path);
        var pattern = BuildPattern(configuration.PropertyName);
        var matches = pattern.Matches(text);

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"W pliku '{configuration.Path}' oczekiwano dokładnie jednej właściwości " +
                $"'{configuration.PropertyName}', znaleziono: {matches.Count}.");
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
            throw new FileNotFoundException("Nie znaleziono pliku target_version.", path);
        }

        return path;
    }
}
