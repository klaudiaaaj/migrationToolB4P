namespace MigrationTool.Core.Runtime;

public sealed class MigrationHistoryException : Exception
{
    public MigrationHistoryException(string message, MigrationPlan plan)
        : base(message)
    {
        Plan = plan;
    }

    public MigrationPlan Plan { get; }
}
