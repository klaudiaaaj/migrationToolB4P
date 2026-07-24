namespace MigrationTool.Core.Configuration;

public sealed class MigrationOptions
{
    public required string ConnectionString { get; init; }
    public required string SchemaName { get; init; }
    public required string ReportSchemaName { get; init; }
    public required long Version { get; init; }
    public int Timeout { get; init; } = 30;
    public bool IsDryRun { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("ConnectionString nie może być pusty.");
        }

        if (string.IsNullOrWhiteSpace(SchemaName))
        {
            throw new ArgumentException("SchemaName nie może być pusty.");
        }

        if (string.IsNullOrWhiteSpace(ReportSchemaName))
        {
            throw new ArgumentException("ReportSchemaName nie może być pusty.");
        }

        if (Version < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Version),
                "Version nie może być ujemna.");
        }

        if (Timeout <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout),
                "Timeout musi być większy od zera.");
        }
    }
}
