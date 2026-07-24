namespace MigrationTool.Core.Runtime;

public sealed class MigrationRollbackException : Exception
{
    public MigrationRollbackException(string message, MigrationDownPlan plan)
        : base(message)
    {
        Plan = plan;
    }

    public MigrationDownPlan Plan { get; }
}
