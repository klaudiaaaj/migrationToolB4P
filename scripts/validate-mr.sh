#!/usr/bin/env bash
set -euo pipefail

: "${CI_MERGE_REQUEST_TARGET_BRANCH_NAME:?This script must run in a GitLab merge request pipeline}"

TARGET_REF="origin/${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}"
git fetch origin \
  "+${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}:refs/remotes/origin/${CI_MERGE_REQUEST_TARGET_BRANCH_NAME}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/migrationtool.sh" check \
  --config migrationtool.json \
  --target-ref "$TARGET_REF"
