using System.Globalization;
using System.Text.RegularExpressions;
using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;

namespace MigrationTool.Core.Services;

public sealed class MigrationSynchronizer
{
    private readonly MigrationSourceScanner _scanner;
    private readonly TargetVersionStore _targetVersionStore;

    public MigrationSynchronizer(
        MigrationSourceScanner scanner,
        TargetVersionStore targetVersionStore)
    {
        _scanner = scanner;
        _targetVersionStore = targetVersionStore;
    }

    public SynchronizationPlan BuildPlan(
        IReadOnlyList<MigrationDescriptor> current,
        IReadOnlyList<MigrationDescriptor> target)
    {
        EnsureUnique(current, "branch źródłowy");
        EnsureUnique(target, "branch docelowy");

        var targetByVersion = target.ToDictionary(x => x.Version);
        var candidates = new List<MigrationDescriptor>();
        var collisionVersions = new HashSet<long>();

        foreach (var migration in current)
        {
            if (!targetByVersion.TryGetValue(migration.Version, out var targetMigration))
            {
                candidates.Add(migration);
                continue;
            }

            if (string.Equals(migration.ContentHash, targetMigration.ContentHash, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(migration.Name, targetMigration.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migracja '{migration.DisplayName}' istnieje w target branchu, ale ma inną zawartość. " +
                    "MigrationTool nie zmieni jej numeru automatycznie. Wykonaj rebase i wyjaśnij zmianę.");
            }

            candidates.Add(migration);
            collisionVersions.Add(migration.Version);
        }

        var targetMaximum = target.Select(x => x.Version).DefaultIfEmpty(0).Max();
        var requiresSynchronization = candidates.Any(x =>
            x.Version <= targetMaximum || collisionVersions.Contains(x.Version));

        if (!requiresSynchronization)
        {
            return new SynchronizationPlan(targetMaximum, []);
        }

        var orderedCandidates = candidates
            .OrderBy(x => x.Version)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        var protectedVersions = current
            .Except(orderedCandidates)
            .Select(x => x.Version)
            .Concat(target.Select(x => x.Version))
            .ToHashSet();

        var timestamp = long.Parse(
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        var nextVersion = Math.Max(timestamp, targetMaximum + 1);
        var changes = new List<SynchronizationChange>();

        foreach (var migration in orderedCandidates)
        {
            while (protectedVersions.Contains(nextVersion))
            {
                nextVersion++;
            }

            changes.Add(new SynchronizationChange(migration, nextVersion));
            protectedVersions.Add(nextVersion);
            nextVersion++;
        }

        return new SynchronizationPlan(targetMaximum, changes);
    }

    public void Apply(
        string repositoryRoot,
        MigrationServiceConfiguration service,
        SynchronizationPlan plan)
    {
        if (plan.Changes.Count == 0)
        {
            return;
        }

        var staged = new List<StagedDirectory>();
        var targetFileBackups = service.TargetVersionFiles
            .Select(x => Path.GetFullPath(Path.Combine(repositoryRoot, x.Path)))
            .ToDictionary(x => x, File.ReadAllBytes, StringComparer.Ordinal);

        try
        {
            foreach (var change in plan.Changes)
            {
                var sourcePath = Path.GetFullPath(Path.Combine(repositoryRoot, change.Migration.FolderPath));
                var temporaryPath = sourcePath + ".migrationtool-" + Guid.NewGuid().ToString("N");

                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException($"Nie znaleziono folderu '{sourcePath}'.");
                }

                var fileBackups = Directory
                    .EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                    .ToDictionary(
                        path => Path.GetRelativePath(sourcePath, path),
                        File.ReadAllBytes,
                        StringComparer.Ordinal);

                Directory.Move(sourcePath, temporaryPath);
                staged.Add(new StagedDirectory(change, sourcePath, temporaryPath, fileBackups));
            }

            foreach (var item in staged)
            {
                ReplaceAttributeVersion(item.TemporaryPath, item.Change.Migration.Version, item.Change.NewVersion);

                var parent = Path.GetDirectoryName(item.OriginalPath)
                    ?? throw new InvalidOperationException("Folder migracji nie ma katalogu nadrzędnego.");
                var destination = Path.Combine(
                    parent,
                    $"{item.Change.NewVersion}_{item.Change.Migration.Name}");

                if (Directory.Exists(destination))
                {
                    throw new IOException($"Docelowy folder '{destination}' już istnieje.");
                }

                Directory.Move(item.TemporaryPath, destination);
                item.FinalPath = destination;
            }

            var refreshed = _scanner.ScanWorkingTree(repositoryRoot, service);
            var targetVersion = Math.Max(
                plan.TargetMaximum,
                refreshed.Select(x => x.Version).DefaultIfEmpty(0).Max());

            foreach (var targetFile in service.TargetVersionFiles)
            {
                _targetVersionStore.Write(repositoryRoot, targetFile, targetVersion);
            }
        }
        catch
        {
            TryRestore(staged);
            foreach (var backup in targetFileBackups)
            {
                File.WriteAllBytes(backup.Key, backup.Value);
            }

            throw;
        }
    }

    private static void ReplaceAttributeVersion(string folder, long oldVersion, long newVersion)
    {
        var pattern = new Regex(
            $@"(?<prefix>\[\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*Migration(?:Attribute)?\s*\(\s*){oldVersion}(?<suffix>\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var replacements = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            var updated = pattern.Replace(content, match =>
            {
                replacements++;
                return match.Groups["prefix"].Value + newVersion + match.Groups["suffix"].Value;
            });

            if (!string.Equals(content, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(file, updated);
            }
        }

        if (replacements == 0)
        {
            throw new InvalidOperationException(
                $"Nie znaleziono atrybutu [Migration({oldVersion})] w folderze '{folder}'.");
        }
    }

    private static void TryRestore(IEnumerable<StagedDirectory> staged)
    {
        foreach (var item in staged.Reverse())
        {
            try
            {
                var currentPath = item.FinalPath ?? item.TemporaryPath;
                if (Directory.Exists(currentPath) && !Directory.Exists(item.OriginalPath))
                {
                    Directory.Move(currentPath, item.OriginalPath);
                }

                if (Directory.Exists(item.OriginalPath))
                {
                    foreach (var backup in item.FileBackups)
                    {
                        var path = Path.Combine(item.OriginalPath, backup.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, backup.Value);
                    }
                }
            }
            catch
            {
                // Nie przesłaniamy pierwotnego wyjątku. Git nadal pozwala odzyskać pliki.
            }
        }
    }

    private static void EnsureUnique(IReadOnlyList<MigrationDescriptor> migrations, string location)
    {
        var duplicate = migrations.GroupBy(x => x.Version).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"W {location} wersja {duplicate.Key} występuje więcej niż raz.");
        }
    }

    private sealed class StagedDirectory
    {
        public StagedDirectory(
            SynchronizationChange change,
            string originalPath,
            string temporaryPath,
            IReadOnlyDictionary<string, byte[]> fileBackups)
        {
            Change = change;
            OriginalPath = originalPath;
            TemporaryPath = temporaryPath;
            FileBackups = fileBackups;
        }

        public SynchronizationChange Change { get; }
        public string OriginalPath { get; }
        public string TemporaryPath { get; }
        public IReadOnlyDictionary<string, byte[]> FileBackups { get; }
        public string? FinalPath { get; set; }
    }
}

public sealed record SynchronizationChange(
    MigrationDescriptor Migration,
    long NewVersion);

public sealed record SynchronizationPlan(
    long TargetMaximum,
    IReadOnlyList<SynchronizationChange> Changes);
