#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)"
TOOL_PROJECT="$SCRIPT_DIR/../src/MigrationTool.Cli/MigrationTool.Cli.csproj"

dotnet run --project "$TOOL_PROJECT" -- --repo "$REPOSITORY_ROOT" "$@"
