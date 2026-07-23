using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Git;

namespace MigrationTool.Core.Services;

public sealed class MigrationSourceScanner
{
    private static readonly Regex FolderPattern = new(
        "^(?<version>[0-9]{1,18})_(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributePattern = new(
        @"\[\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*Migration(?:Attribute)?\s*\(\s*(?<version>[0-9]{1,18})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<MigrationDescriptor> ScanWorkingTree(
        string repositoryRoot,
        MigrationServiceConfiguration service)
    {
        var absoluteRoot = Path.GetFullPath(Path.Combine(repositoryRoot, service.MigrationRoot));
        if (!Directory.Exists(absoluteRoot))
        {
            throw new DirectoryNotFoundException(
                $"Nie znaleziono katalogu migracji '{absoluteRoot}' dla '{service.Name}'.");
        }

        var descriptors = new List<MigrationDescriptor>();

        foreach (var directory in Directory.EnumerateDirectories(absoluteRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            var folderMatch = FolderPattern.Match(folderName);
            if (!folderMatch.Success)
            {
                continue;
            }

            var files = Directory
                .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            descriptors.Add(CreateFromWorkingDirectory(repositoryRoot, directory, files, folderMatch));
        }

        return descriptors.OrderBy(x => x.Version).ToArray();
    }

    public IReadOnlyList<MigrationDescriptor> ScanGitRef(
        GitClient git,
        string gitRef,
        MigrationServiceConfiguration service)
    {
        var migrationRoot = NormalizePath(service.MigrationRoot).TrimEnd('/');
        var files = git.ListFiles(gitRef, migrationRoot)
            .Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var groups = files
            .Select(path => new { Path = path, Folder = TryGetMigrationFolder(migrationRoot, path) })
            .Where(x => x.Folder is not null)
            .GroupBy(x => x.Folder!, StringComparer.Ordinal);

        var descriptors = new List<MigrationDescriptor>();

        foreach (var group in groups)
        {
            var folderName = group.Key.Split('/').Last();
            var folderMatch = FolderPattern.Match(folderName);
            if (!folderMatch.Success)
            {
                continue;
            }

            var contents = group
                .OrderBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => new SourceFile(x.Path, git.ReadFile(gitRef, x.Path)))
                .ToArray();

            descriptors.Add(CreateFromContents(group.Key, contents, folderMatch));
        }

        return descriptors.OrderBy(x => x.Version).ToArray();
    }

    private static MigrationDescriptor CreateFromWorkingDirectory(
        string repositoryRoot,
        string directory,
        IReadOnlyList<string> files,
        Match folderMatch)
    {
        var sourceFiles = files
            .Select(path => new SourceFile(
                NormalizePath(Path.GetRelativePath(repositoryRoot, path)),
                File.ReadAllText(path)))
            .ToArray();

        var relativeFolder = NormalizePath(Path.GetRelativePath(repositoryRoot, directory));
        return CreateFromContents(relativeFolder, sourceFiles, folderMatch);
    }

    private static MigrationDescriptor CreateFromContents(
        string folderPath,
        IReadOnlyList<SourceFile> files,
        Match folderMatch)
    {
        var folderVersion = long.Parse(folderMatch.Groups["version"].Value);
        var name = folderMatch.Groups["name"].Value;

        var attributeOccurrences = files
            .SelectMany(file => AttributePattern.Matches(file.Content).Cast<Match>().Select(match =>
                long.Parse(match.Groups["version"].Value)))
            .ToArray();
        var attributeVersions = attributeOccurrences.Distinct().ToArray();

        long? attributeVersion = attributeVersions.Length == 1
            ? attributeVersions[0]
            : null;

        return new MigrationDescriptor(
            folderVersion,
            attributeVersion,
            attributeOccurrences.Length,
            name,
            NormalizePath(folderPath),
            files.Select(x => NormalizePath(x.Path)).ToArray(),
            CalculateHash(folderPath, files));
    }

    private static string CalculateHash(string folderPath, IReadOnlyList<SourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var file in files.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            var relativePath = NormalizePath(Path.GetRelativePath(folderPath, file.Path));
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(Encoding.UTF8.GetBytes(file.Content.Replace("\r\n", "\n", StringComparison.Ordinal)));
            hash.AppendData(new byte[] { 0 });
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? TryGetMigrationFolder(string migrationRoot, string filePath)
    {
        var normalized = NormalizePath(filePath);
        var prefix = migrationRoot + "/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var relative = normalized[prefix.Length..];
        var firstSlash = relative.IndexOf('/');
        if (firstSlash <= 0)
        {
            return null;
        }

        var folderName = relative[..firstSlash];
        return FolderPattern.IsMatch(folderName)
            ? prefix + folderName
            : null;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private sealed record SourceFile(string Path, string Content);
}
