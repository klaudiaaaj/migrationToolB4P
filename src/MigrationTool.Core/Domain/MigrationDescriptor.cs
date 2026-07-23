namespace MigrationTool.Core.Domain;

public sealed record MigrationDescriptor(
    long Version,
    long? AttributeVersion,
    int AttributeCount,
    string Name,
    string FolderPath,
    IReadOnlyList<string> Files,
    string ContentHash)
{
    public string DisplayName => $"{Version}_{Name}";
}

public sealed record AppliedMigration(
    long Version,
    string? Description,
    DateTimeOffset? AppliedOn);
