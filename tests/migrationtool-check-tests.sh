#!/bin/sh

set -eu

test_root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
check_script="$test_root/scripts/migrationtool.sh"
fixture=$(mktemp -d "${TMPDIR:-/tmp}/migrationtool-shell-tests.XXXXXX")
trap 'rm -rf "$fixture"' EXIT HUP INT TERM

git -C "$fixture" init -q
git -C "$fixture" config user.email "migrationtool-tests@example.test"
git -C "$fixture" config user.name "MigrationTool Tests"

mkdir -p "$fixture/src/Test.Migrations/Migrations/100_Baseline"
printf '%s\n' \
    '[Migration(100)]' \
    'public sealed class Baseline' \
    '{' \
    '    public override void Up()' \
    '    {' \
    '        Create.Table("Baseline");' \
    '    }' \
    '' \
    '    public override void Down()' \
    '    {' \
    '        Delete.Table("Baseline");' \
    '    }' \
    '}' \
    >"$fixture/src/Test.Migrations/Migrations/100_Baseline/Baseline.cs"
printf 'Opis migracji.\n' \
    >"$fixture/src/Test.Migrations/Migrations/100_Baseline/README.md"
printf '{ "TargetVersion": "100" }\n' \
    >"$fixture/src/Test.Migrations/appsettings.json"
printf '{ "projectRoot": "src/Test.Migrations", "namespace": "Test.Migrations" }\n' \
    >"$fixture/migrationtool.json"

git -C "$fixture" add .
git -C "$fixture" commit -qm "baseline"
git -C "$fixture" branch target
git -C "$fixture" switch -q target

mkdir -p "$fixture/src/Test.Migrations/Migrations/300_Hotfix"
printf '%s\n' \
    '[Migration(300)]' \
    'public sealed class Hotfix' \
    '{' \
    '    public override void Up()' \
    '    {' \
    '        Create.Table("Hotfix");' \
    '    }' \
    '    public override void Down()' \
    '    {' \
    '        Delete.Table("Hotfix");' \
    '    }' \
    '}' \
    >"$fixture/src/Test.Migrations/Migrations/300_Hotfix/Hotfix.cs"
printf '{ "TargetVersion": "300" }\n' \
    >"$fixture/src/Test.Migrations/appsettings.json"
git -C "$fixture" add .
git -C "$fixture" commit -qm "hotfix"

git -C "$fixture" switch -q --detach HEAD~1
git -C "$fixture" config core.autocrlf true
printf '%s\r\n' \
    '[Migration(100)]' \
    'public sealed class Baseline' \
    '{' \
    '    public override void Up()' \
    '    {' \
    '        Create.Table("Baseline");' \
    '    }' \
    '' \
    '    public override void Down()' \
    '    {' \
    '        Delete.Table("Baseline");' \
    '    }' \
    '}' \
    >"$fixture/src/Test.Migrations/Migrations/100_Baseline/Baseline.cs"
mkdir -p "$fixture/src/Test.Migrations/Migrations/400_Feature"
printf '%s\n' \
    '[Migration(400)]' \
    'public sealed class Feature' \
    '{' \
    '    public override void Up()' \
    '    {' \
    '        Create.Table("Feature");' \
    '    }' \
    '    public override void Down()' \
    '    {' \
    '        Delete.Table("Feature");' \
    '    }' \
    '}' \
    >"$fixture/src/Test.Migrations/Migrations/400_Feature/Feature.cs"
printf '{ "TargetVersion": "400" }\n' \
    >"$fixture/src/Test.Migrations/appsettings.json"

sh "$check_script" check \
    --repo "$fixture" \
    --config migrationtool.json \
    --target-ref target >/dev/null

mv \
    "$fixture/src/Test.Migrations/Migrations/400_Feature" \
    "$fixture/src/Test.Migrations/Migrations/200_Feature"
sed -i.bak 's/Migration(400)/Migration(200)/' \
    "$fixture/src/Test.Migrations/Migrations/200_Feature/Feature.cs"
rm "$fixture/src/Test.Migrations/Migrations/200_Feature/Feature.cs.bak"
printf '{ "TargetVersion": "200" }\n' \
    >"$fixture/src/Test.Migrations/appsettings.json"

if sh "$check_script" check \
    --repo "$fixture" \
    --config migrationtool.json \
    --target-ref target >/dev/null 2>&1; then
    echo "Expected an old migration to fail." >&2
    exit 1
fi

rm -rf "$fixture/src/Test.Migrations/Migrations/200_Feature"
printf '%s\n' \
    '[Migration(100)]' \
    'public sealed class ChangedBaseline' \
    '{' \
    '    public override void Up()' \
    '    {' \
    '        Create.Table("Baseline");' \
    '    }' \
    '' \
    '    public override void Down()' \
    '    {' \
    '        Delete.Table("Baseline");' \
    '    }' \
    '}' \
    >"$fixture/src/Test.Migrations/Migrations/100_Baseline/Baseline.cs"
printf '{ "TargetVersion": "100" }\n' \
    >"$fixture/src/Test.Migrations/appsettings.json"

if ! sh "$check_script" check \
    --repo "$fixture" \
    --config migrationtool.json \
    --target-ref target >/dev/null 2>&1; then
    echo "Expected a source-code change to be ignored." >&2
    exit 1
fi

echo "MigrationTool shell check tests: OK"
