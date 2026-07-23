$ErrorActionPreference = "Stop"

$repositoryRoot = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "MigrationTool must be located inside a Git repository."
}

$toolProject = Join-Path $PSScriptRoot "../src/MigrationTool.Cli/MigrationTool.Cli.csproj"
dotnet run --project $toolProject -- --repo $repositoryRoot @args
exit $LASTEXITCODE
