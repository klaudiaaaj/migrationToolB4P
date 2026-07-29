using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Git;

namespace MigrationTool.Core.Services;

public sealed record GenerateMigrationResult(MigrationDescriptor Migration);

public sealed record MigrationValidationResult(
    ValidationResult Validation,
    long CurrentMaximum,
    long? TargetMaximum = null,
    IReadOnlyList<MigrationDescriptor>? SourceOnlyMigrations = null)
{
    public bool IsValid => Validation.IsValid;
}

public sealed record MigrationSynchronizationResult(
    bool IsDryRun,
    long TargetMaximum,
    IReadOnlyList<SynchronizationChange> Changes,
    ValidationResult Validation)
{
    public bool IsValid => Validation.IsValid;
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Publiczne API do pracy z migracjami jednego projektu w repozytorium Git.
/// Nie parsuje argumentów CLI, nie używa Console i nie zwraca kodów procesu.
/// </summary>
public sealed class MigrationWorkspaceService
{
    private readonly string _repositoryRoot;
    private readonly MigrationToolConfiguration _configuration;
    private readonly GitClient _git;
    private readonly MigrationSourceScanner _scanner = new();
    private readonly TargetVersionStore _targetVersionStore = new();
    private readonly MigrationValidator _validator;
    private readonly MigrationGenerator _generator;
    private readonly MigrationSynchronizer _synchronizer;

    public MigrationWorkspaceService(
        string repositoryPath,
        string configurationPath = "migrationtool.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        var initialGit = new GitClient(repositoryPath);
        _repositoryRoot = Path.GetFullPath(initialGit.FindRepositoryRoot());
        _git = new GitClient(_repositoryRoot);

        var absoluteConfigurationPath = Path.IsPathRooted(configurationPath)
            ? configurationPath
            : Path.Combine(_repositoryRoot, configurationPath);
        _configuration = MigrationToolConfiguration.Load(
            Path.GetFullPath(absoluteConfigurationPath));

        _validator = new MigrationValidator(_targetVersionStore);
        _generator = new MigrationGenerator(_scanner, _targetVersionStore);
        _synchronizer = new MigrationSynchronizer(_scanner, _targetVersionStore);
    }

    public Task<GenerateMigrationResult> GenerateAsync(
        string migrationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        cancellationToken.ThrowIfCancellationRequested();

        var migration = _generator.Create(
            _repositoryRoot,
            _configuration,
            migrationName);

        return Task.FromResult(new GenerateMigrationResult(migration));
    }

    public Task<MigrationValidationResult> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var migrations = _scanner.ScanWorkingTree(_repositoryRoot, _configuration);
        var validation = _validator.ValidateStructure(
            _repositoryRoot,
            _configuration,
            migrations);

        return Task.FromResult(new MigrationValidationResult(
            validation,
            migrations.Select(migration => migration.Version).DefaultIfEmpty(0).Max()));
    }

    public Task<MigrationValidationResult> CheckAsync(
        string targetRef,
        CancellationToken cancellationToken = default)
    {
        EnsureTargetRefExists(targetRef);
        cancellationToken.ThrowIfCancellationRequested();

        var current = _scanner.ScanWorkingTree(_repositoryRoot, _configuration);
        var target = _scanner.ScanGitRef(_git, targetRef, _configuration);
        var validation = _validator.ValidateStructure(
            _repositoryRoot,
            _configuration,
            current);
        validation.Merge(_validator.ValidateAgainstTarget(
            current,
            target,
            targetRef));

        var targetVersions = target
            .Select(migration => migration.Version)
            .ToHashSet();
        var sourceOnly = current
            .Where(migration => !targetVersions.Contains(migration.Version))
            .OrderBy(migration => migration.Version)
            .ToArray();

        return Task.FromResult(new MigrationValidationResult(
            validation,
            current.Select(migration => migration.Version).DefaultIfEmpty(0).Max(),
            target.Select(migration => migration.Version).DefaultIfEmpty(0).Max(),
            sourceOnly));
    }

    public Task<MigrationSynchronizationResult> SynchronizeAsync(
        string targetRef,
        bool isDryRun = false,
        CancellationToken cancellationToken = default)
    {
        EnsureTargetRefExists(targetRef);
        cancellationToken.ThrowIfCancellationRequested();

        var current = _scanner.ScanWorkingTree(_repositoryRoot, _configuration);
        var validation = _validator.ValidateStructure(
            _repositoryRoot,
            _configuration,
            current);

        if (!validation.IsValid)
        {
            return Task.FromResult(new MigrationSynchronizationResult(
                isDryRun,
                TargetMaximum: 0,
                Changes: [],
                validation));
        }

        var target = _scanner.ScanGitRef(_git, targetRef, _configuration);
        var synchronization = _synchronizer.BuildPlan(current, target);

        if (!isDryRun)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _synchronizer.Apply(
                _repositoryRoot,
                _configuration,
                synchronization);

            var refreshed = _scanner.ScanWorkingTree(
                _repositoryRoot,
                _configuration);
            validation.Merge(_validator.ValidateStructure(
                _repositoryRoot,
                _configuration,
                refreshed));
        }

        return Task.FromResult(new MigrationSynchronizationResult(
            isDryRun,
            synchronization.TargetMaximum,
            synchronization.Changes,
            validation));
    }

    private void EnsureTargetRefExists(string targetRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRef);

        if (!_git.RefExists(targetRef))
        {
            throw new InvalidOperationException(
                $"Git ref '{targetRef}' nie istnieje. " +
                "Pobierz branch docelowy przed wywołaniem API.");
        }
    }
}
