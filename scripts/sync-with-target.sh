#!/usr/bin/env bash
set -euo pipefail

TARGET_BRANCH="${1:?Usage: sync-with-target.sh <target-branch> [migrationtool options]}"
shift

TARGET_REF="origin/${TARGET_BRANCH}"
git fetch origin "+${TARGET_BRANCH}:refs/remotes/origin/${TARGET_BRANCH}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/migrationtool.sh" sync \
  --config migrationtool.json \
  --target-ref "$TARGET_REF" \
  "$@"
