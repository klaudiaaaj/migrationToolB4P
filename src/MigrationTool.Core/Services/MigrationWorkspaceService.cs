using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Git;

namespace MigrationTool.Core.Services;

public sealed record GenerateMigrationRequest(
    string ServiceName,
    string MigrationName);

public sealed record GenerateMigrationResult(
    string ServiceName,
    MigrationDescriptor Migration);

public sealed record ValidateMigrationsRequest(string? ServiceName = null);

public sealed record CheckMigrationsRequest(
    string TargetRef,
    string? ServiceName = null);

public sealed record SynchronizeMigrationsRequest(
    string TargetRef,
    string? ServiceName = null,
    bool IsDryRun = false);

public sealed record ServiceValidationResult(
    string ServiceName,
    ValidationResult Validation,
    long CurrentMaximum,
    long? TargetMaximum = null,
    IReadOnlyList<MigrationDescriptor>? SourceOnlyMigrations = null);

public sealed record MigrationsValidationResult(
    IReadOnlyList<ServiceValidationResult> Services)
{
    public bool IsValid => Services.All(service => service.Validation.IsValid);
}

public sealed record ServiceSynchronizationResult(
    string ServiceName,
    long TargetMaximum,
    IReadOnlyList<SynchronizationChange> Changes,
    ValidationResult Validation);

public sealed record MigrationsSynchronizationResult(
    bool IsDryRun,
    IReadOnlyList<ServiceSynchronizationResult> Services)
{
    public bool IsValid => Services.All(service => service.Validation.IsValid);
    public bool HasChanges => Services.Any(service => service.Changes.Count > 0);
}

/// <summary>
/// Publiczne API do pracy z plikami migracji w repozytorium.
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
        GenerateMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var service = _configuration.GetRequiredService(request.ServiceName);
        var migration = _generator.Create(
            _repositoryRoot,
            service,
            request.MigrationName);

        return Task.FromResult(new GenerateMigrationResult(service.Name, migration));
    }

    public Task<MigrationsValidationResult> ValidateAsync(
        ValidateMigrationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<ServiceValidationResult>();

        foreach (var service in _configuration.SelectServices(request.ServiceName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var migrations = _scanner.ScanWorkingTree(_repositoryRoot, service);
            var validation = _validator.ValidateStructure(
                _repositoryRoot,
                service,
                migrations);

            results.Add(new ServiceValidationResult(
                service.Name,
                validation,
                migrations.Select(migration => migration.Version).DefaultIfEmpty(0).Max()));
        }

        return Task.FromResult(new MigrationsValidationResult(results));
    }

    public Task<MigrationsValidationResult> CheckAsync(
        CheckMigrationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTargetRefExists(request.TargetRef);
        var results = new List<ServiceValidationResult>();

        foreach (var service in _configuration.SelectServices(request.ServiceName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _scanner.ScanWorkingTree(_repositoryRoot, service);
            var target = _scanner.ScanGitRef(_git, request.TargetRef, service);
            var validation = _validator.ValidateStructure(
                _repositoryRoot,
                service,
                current);
            validation.Merge(_validator.ValidateAgainstTarget(
                service,
                current,
                target,
                request.TargetRef));

            var targetVersions = target.Select(migration => migration.Version).ToHashSet();
            var sourceOnly = current
                .Where(migration => !targetVersions.Contains(migration.Version))
                .OrderBy(migration => migration.Version)
                .ToArray();

            results.Add(new ServiceValidationResult(
                service.Name,
                validation,
                current.Select(migration => migration.Version).DefaultIfEmpty(0).Max(),
                target.Select(migration => migration.Version).DefaultIfEmpty(0).Max(),
                sourceOnly));
        }

        return Task.FromResult(new MigrationsValidationResult(results));
    }

    public Task<MigrationsSynchronizationResult> SynchronizeAsync(
        SynchronizeMigrationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTargetRefExists(request.TargetRef);
        var results = new List<ServiceSynchronizationResult>();

        foreach (var service in _configuration.SelectServices(request.ServiceName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _scanner.ScanWorkingTree(_repositoryRoot, service);
            var validation = _validator.ValidateStructure(
                _repositoryRoot,
                service,
                current);

            if (!validation.IsValid)
            {
                results.Add(new ServiceSynchronizationResult(
                    service.Name,
                    0,
                    [],
                    validation));
                break;
            }

            var target = _scanner.ScanGitRef(_git, request.TargetRef, service);
            var synchronization = _synchronizer.BuildPlan(current, target);

            if (!request.IsDryRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _synchronizer.Apply(_repositoryRoot, service, synchronization);
                var refreshed = _scanner.ScanWorkingTree(_repositoryRoot, service);
                validation.Merge(_validator.ValidateStructure(
                    _repositoryRoot,
                    service,
                    refreshed));
            }

            results.Add(new ServiceSynchronizationResult(
                service.Name,
                synchronization.TargetMaximum,
                synchronization.Changes,
                validation));

            if (!validation.IsValid)
            {
                break;
            }
        }

        return Task.FromResult(new MigrationsSynchronizationResult(
            request.IsDryRun,
            results));
    }

    private void EnsureTargetRefExists(string targetRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRef);

        if (!_git.RefExists(targetRef))
        {
            throw new InvalidOperationException(
                $"Git ref '{targetRef}' nie istnieje. Pobierz branch docelowy przed wywołaniem API.");
        }
    }
}
