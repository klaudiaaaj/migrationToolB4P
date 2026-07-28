using MigrationTool.Core.Runtime;

namespace MigrationTool.Core.Tests;

[TestFixture]
public sealed class MigrationStateTests
{
    [TestCase(100, 200, MigrationDirection.Up)]
    [TestCase(200, 100, MigrationDirection.Down)]
    [TestCase(200, 200, MigrationDirection.None)]
    public void ResolveDirection_SelectsOperationFromCurrentAndTargetVersion(
        long currentVersion,
        long targetVersion,
        MigrationDirection expected)
    {
        var direction = MigrationToolRunner.ResolveDirection(
            currentVersion,
            targetVersion);

        Assert.That(direction, Is.EqualTo(expected));
    }

    [Test]
    public void ValidateState_AllowsSequentialUp()
    {
        TestAssertions.DoesNotThrow(() => MigrationToolRunner.ValidateState(
            Set(100, 200, 300),
            Set(100, 200),
            currentVersion: 200,
            targetVersion: 300,
            MigrationDirection.Up));
    }

    [Test]
    public void ValidateState_AllowsRollbackToAppliedVersion()
    {
        TestAssertions.DoesNotThrow(() => MigrationToolRunner.ValidateState(
            Set(100, 200, 300),
            Set(100, 200, 300),
            currentVersion: 300,
            targetVersion: 100,
            MigrationDirection.Down));
    }

    [Test]
    public void ValidateState_BlocksTargetMissingFromAssembly()
    {
        var exception = TestAssertions.Throws<MigrationSafetyException>(() =>
            MigrationToolRunner.ValidateState(
                Set(100, 200, 300),
                Set(100, 200),
                currentVersion: 200,
                targetVersion: 250,
                MigrationDirection.Up));

        Assert.That(exception!.Message, Does.Contain("250"));
        Assert.That(exception.Message, Does.Contain("nie istnieje w assembly"));
    }

    [Test]
    public void ValidateState_BlocksAppliedMigrationMissingFromAssembly()
    {
        var exception = TestAssertions.Throws<MigrationSafetyException>(() =>
            MigrationToolRunner.ValidateState(
                Set(100, 200),
                Set(100, 150),
                currentVersion: 150,
                targetVersion: 200,
                MigrationDirection.Up));

        Assert.That(exception!.Message, Does.Contain("150"));
        Assert.That(exception.Message, Does.Contain("których nie ma w assembly"));
    }

    [Test]
    public void ValidateState_BlocksGapBelowCurrentDatabaseVersion()
    {
        var exception = TestAssertions.Throws<MigrationSafetyException>(() =>
            MigrationToolRunner.ValidateState(
                Set(100, 200, 300),
                Set(100, 300),
                currentVersion: 300,
                targetVersion: 300,
                MigrationDirection.None));

        Assert.That(exception!.Message, Does.Contain("200"));
        Assert.That(exception.Message, Does.Contain("pominięte migracje"));
    }

    [Test]
    public void ValidateState_AllowsRollbackToZero()
    {
        TestAssertions.DoesNotThrow(() => MigrationToolRunner.ValidateState(
            Set(100, 200, 300),
            Set(100, 200, 300),
            currentVersion: 300,
            targetVersion: 0,
            MigrationDirection.Down));
    }

    [Test]
    public void VerifyFinalState_AcceptsExactExpectedHistory()
    {
        TestAssertions.DoesNotThrow(() => MigrationToolRunner.VerifyFinalState(
            Set(100, 200, 300),
            Set(100, 200),
            targetVersion: 200));
    }

    [Test]
    public void VerifyFinalState_BlocksMissingMigration()
    {
        var exception = TestAssertions.Throws<MigrationSafetyException>(() =>
            MigrationToolRunner.VerifyFinalState(
                Set(100, 200, 300),
                Set(100),
                targetVersion: 200));

        Assert.That(exception!.Message, Does.Contain("nie osiągnęła wersji 200"));
    }

    [Test]
    public void VerifyFinalState_BlocksVersionAboveTarget()
    {
        var exception = TestAssertions.Throws<MigrationSafetyException>(() =>
            MigrationToolRunner.VerifyFinalState(
                Set(100, 200, 300),
                Set(100, 200, 300),
                targetVersion: 200));

        Assert.That(exception!.Message, Does.Contain("VersionInfo zawiera"));
        Assert.That(exception.Message, Does.Contain("300"));
    }

    private static IReadOnlySet<long> Set(params long[] versions)
        => versions.ToHashSet();
}
