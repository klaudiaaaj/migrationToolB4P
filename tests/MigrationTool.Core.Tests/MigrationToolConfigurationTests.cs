using System.Text.Json;
using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Tests;

[TestFixture]
public sealed class MigrationToolConfigurationTests
{
    [Test]
    public void Load_ReadsSingleProjectConfiguration()
    {
        WithConfiguration(
            """
            {
              "projectRoot": "src/Orders.Database.Migrations",
              "namespace": "Orders.Database.Migrations"
            }
            """,
            path =>
            {
                var configuration = MigrationToolConfiguration.Load(path);

                Assert.That(
                    configuration.ProjectRoot,
                    Is.EqualTo("src/Orders.Database.Migrations"));
                Assert.That(
                    configuration.Namespace,
                    Is.EqualTo("Orders.Database.Migrations"));
                Assert.That(
                    configuration.MigrationRoot,
                    Is.EqualTo(Path.Combine(
                        "src/Orders.Database.Migrations",
                        "Migrations")));
                Assert.That(
                    configuration.TargetVersionFile,
                    Is.EqualTo(Path.Combine(
                        "src/Orders.Database.Migrations",
                        "appsettings.json")));
                Assert.That(
                    configuration.TargetVersionProperty,
                    Is.EqualTo("target_version"));
            });
    }

    [Test]
    public void Load_RejectsOldServicesArrayConfiguration()
    {
        WithConfiguration(
            """
            {
              "services": []
            }
            """,
            path => TestAssertions.Throws<JsonException>(
                () => MigrationToolConfiguration.Load(path)));
    }

    [Test]
    public void Validate_RejectsEmptyProjectRoot()
    {
        var configuration = new MigrationToolConfiguration
        {
            ProjectRoot = " ",
            Namespace = "Orders.Migrations",
        };

        TestAssertions.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Test]
    public void Paths_DoNotAddDotPrefixForProjectInRepositoryRoot()
    {
        var configuration = new MigrationToolConfiguration
        {
            ProjectRoot = ".",
            Namespace = "Orders.Migrations"
        };

        Assert.That(configuration.MigrationRoot, Is.EqualTo("Migrations"));
        Assert.That(configuration.TargetVersionFile, Is.EqualTo("appsettings.json"));
    }

    private static void WithConfiguration(string json, Action<string> assertion)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "migration-configuration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "migrationtool.json");
        File.WriteAllText(path, json);

        try
        {
            assertion(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
