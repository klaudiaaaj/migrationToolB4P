using Microsoft.Extensions.DependencyInjection;

namespace MigrationTool.Core.Services;

public static class MigrationToolServiceCollectionExtensions
{
    public static IServiceCollection AddMigrationToolServices(
        this IServiceCollection services,
        string repositoryPath,
        string configurationPath = "migrationtool.json")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        services.AddSingleton(_ =>
            new MigrationWorkspaceService(repositoryPath, configurationPath));

        return services;
    }
}
