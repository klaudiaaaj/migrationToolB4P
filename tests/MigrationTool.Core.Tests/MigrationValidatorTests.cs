using MigrationTool.Core.Configuration;
using MigrationTool.Core.Domain;
using MigrationTool.Core.Services;

namespace MigrationTool.Core.Tests;

[TestFixture]
public sealed class MigrationValidatorTests
{
    private readonly MigrationValidator _validator = new(new TargetVersionStore());

    [Test]
    public void ValidateAgainstTarget_BlocksMigrationOlderThanTargetHead()
    {
        var current = new[]
        {
            Migration(100, "Baseline", "same"),
            Migration(200, "Feature", "feature")
        };
        var target = new[]
        {
            Migration(100, "Baseline", "same"),
            Migration(300, "Hotfix", "hotfix")
        };

        var result = _validator.ValidateAgainstTarget(
            Service(),
            current,
            target,
            "origin/develop");

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Messages.Select(message => message.Code),
            Does.Contain("MIGRATION_OLDER_THAN_TARGET_HEAD"));
    }

    [Test]
    public void ValidateAgainstTarget_BlocksModifiedExistingMigration()
    {
        var current = new[] { Migration(100, "Baseline", "changed") };
        var target = new[] { Migration(100, "Baseline", "original") };

        var result = _validator.ValidateAgainstTarget(
            Service(),
            current,
            target,
            "origin/develop");

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Messages.Select(message => message.Code),
            Does.Contain("VERSION_COLLISION_OR_MODIFIED_MIGRATION"));
    }

    [Test]
    public void ValidateAgainstTarget_AcceptsNewMigrationAboveTargetHead()
    {
        var current = new[]
        {
            Migration(100, "Baseline", "same"),
            Migration(300, "Feature", "feature")
        };
        var target = new[] { Migration(100, "Baseline", "same") };

        var result = _validator.ValidateAgainstTarget(
            Service(),
            current,
            target,
            "origin/develop");

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void ValidateStructure_ReportsDuplicateAndAttributeMismatch()
    {
        var migrations = new[]
        {
            Migration(100, "First", "first"),
            Migration(100, "Second", "second", attributeVersion: 200)
        };

        var result = _validator.ValidateStructure(
            repositoryRoot: ".",
            Service(),
            migrations);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Messages.Select(message => message.Code),
            Does.Contain("DUPLICATE_VERSION"));
        Assert.That(
            result.Messages.Select(message => message.Code),
            Does.Contain("FOLDER_ATTRIBUTE_MISMATCH"));
    }

    private static MigrationServiceConfiguration Service()
        => new()
        {
            Name = "Orders",
            MigrationRoot = "Migrations",
            Namespace = "Orders.Migrations",
            TargetVersionFiles = []
        };

    private static MigrationDescriptor Migration(
        long version,
        string name,
        string hash,
        long? attributeVersion = null)
        => new(
            version,
            attributeVersion ?? version,
            AttributeCount: 1,
            name,
            $"Migrations/{version}_{name}",
            [$"Migrations/{version}_{name}/{name}.cs"],
            hash);
}
