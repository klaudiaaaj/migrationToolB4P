using MigrationTool.Core.Configuration;

namespace MigrationTool.Core.Tests;

[TestFixture]
public sealed class MigrationOptionsTests
{
    [Test]
    public void Validate_AcceptsValidOptions()
    {
        TestAssertions.DoesNotThrow(() => ValidOptions().Validate());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Validate_RejectsMissingConnectionString(string? connectionString)
    {
        var options = OptionsWithConnectionString(connectionString!);

        TestAssertions.Throws<ArgumentException>(() => options.Validate());
    }

    [Test]
    public void Validate_RejectsNegativeVersion()
    {
        var options = new MigrationOptions
        {
            ConnectionString = "Server=test;Database=test",
            SchemaName = "dbo",
            ReportSchemaName = "reports",
            Version = -1,
            Timeout = 30
        };

        TestAssertions.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Validate_RejectsNonPositiveTimeout(int timeout)
    {
        var options = new MigrationOptions
        {
            ConnectionString = "Server=test;Database=test",
            SchemaName = "dbo",
            ReportSchemaName = "reports",
            Version = 100,
            Timeout = timeout
        };

        TestAssertions.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    private static MigrationOptions ValidOptions()
        => new()
        {
            ConnectionString = "Server=test;Database=test",
            SchemaName = "dbo",
            ReportSchemaName = "reports",
            Version = 100,
            Timeout = 30
        };

    private static MigrationOptions OptionsWithConnectionString(string connectionString)
        => new()
        {
            ConnectionString = connectionString,
            SchemaName = "dbo",
            ReportSchemaName = "reports",
            Version = 100,
            Timeout = 30
        };
}
