using MigrationTool.Core.Domain;
using MigrationTool.Core.Services;

namespace MigrationTool.Core.Tests;

[TestFixture]
public sealed class MigrationSynchronizerTests
{
    private readonly MigrationSynchronizer _synchronizer =
        new(new MigrationSourceScanner(), new TargetVersionStore());

    [Test]
    public void BuildPlan_ReturnsNoChangesWhenNewMigrationIsAboveTargetHead()
    {
        var baseline = Migration(100, "Baseline", "same");
        var current = new[]
        {
            baseline,
            Migration(300, "Feature", "feature")
        };
        var target = new[] { baseline };

        var result = _synchronizer.BuildPlan(current, target);

        Assert.That(result.TargetMaximum, Is.EqualTo(100));
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    public void BuildPlan_RenumbersMigrationOlderThanTargetHead()
    {
        var baseline = Migration(100, "Baseline", "same");
        var current = new[]
        {
            baseline,
            Migration(200, "Feature", "feature")
        };
        var target = new[]
        {
            baseline,
            Migration(300, "Hotfix", "hotfix")
        };

        var result = _synchronizer.BuildPlan(current, target);

        Assert.That(result.Changes, Has.Count.EqualTo(1));
        Assert.That(result.Changes[0].Migration.Name, Is.EqualTo("Feature"));
        Assert.That(result.Changes[0].NewVersion, Is.GreaterThan(300));
    }

    [Test]
    public void BuildPlan_PreservesOrderOfMultipleFeatureMigrations()
    {
        var baseline = Migration(100, "Baseline", "same");
        var current = new[]
        {
            baseline,
            Migration(200, "FeatureA", "a"),
            Migration(250, "FeatureB", "b")
        };
        var target = new[]
        {
            baseline,
            Migration(300, "Hotfix", "hotfix")
        };

        var result = _synchronizer.BuildPlan(current, target);

        Assert.That(result.Changes, Has.Count.EqualTo(2));
        Assert.That(
            result.Changes.Select(change => change.Migration.Name),
            Is.EqualTo(new[] { "FeatureA", "FeatureB" }));
        Assert.That(
            result.Changes[1].NewVersion,
            Is.GreaterThan(result.Changes[0].NewVersion));
    }

    [Test]
    public void BuildPlan_RenumbersVersionCollisionWithDifferentMigration()
    {
        var baseline = Migration(100, "Baseline", "same");
        var current = new[]
        {
            baseline,
            Migration(200, "Feature", "feature")
        };
        var target = new[]
        {
            baseline,
            Migration(200, "Hotfix", "hotfix")
        };

        var result = _synchronizer.BuildPlan(current, target);

        Assert.That(result.Changes, Has.Count.EqualTo(1));
        Assert.That(result.Changes[0].Migration.Name, Is.EqualTo("Feature"));
        Assert.That(result.Changes[0].NewVersion, Is.GreaterThan(200));
    }

    [Test]
    public void BuildPlan_BlocksModifiedMigrationAlreadyPresentInTarget()
    {
        var current = new[] { Migration(100, "Baseline", "changed") };
        var target = new[] { Migration(100, "Baseline", "original") };

        var exception = TestAssertions.Throws<InvalidOperationException>(() =>
            _synchronizer.BuildPlan(current, target));

        Assert.That(exception!.Message, Does.Contain("ma inną zawartość"));
    }

    [Test]
    public void BuildPlan_BlocksDuplicateVersions()
    {
        var current = new[]
        {
            Migration(100, "First", "first"),
            Migration(100, "Second", "second")
        };

        TestAssertions.Throws<InvalidOperationException>(() =>
            _synchronizer.BuildPlan(current, []));
    }

    private static MigrationDescriptor Migration(
        long version,
        string name,
        string hash)
        => new(
            version,
            version,
            AttributeCount: 1,
            name,
            $"Migrations/{version}_{name}",
            [$"Migrations/{version}_{name}/{name}.cs"],
            hash);
}
