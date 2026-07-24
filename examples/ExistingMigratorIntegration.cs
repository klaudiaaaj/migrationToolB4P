using MigrationTool.Core.Configuration;
using MigrationTool.Core.Runtime;

namespace YourCompany.Database.Migrations;

// FluentMigrator jest używany bezpośrednio przez MigrationToolRunner.
// Nie trzeba rejestrować adaptera ani implementować IMigrationExecutor.
public sealed class MigrationTool
{
    private readonly MigrationToolRunner _runner = new(typeof(Program).Assembly);

    public Task<MigrationRunResult> Run(
        MigrationOptions options,
        CancellationToken cancellationToken = default)
        => _runner.Run(options, cancellationToken);
}
