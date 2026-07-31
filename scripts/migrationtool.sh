#!/bin/sh

set -u

usage()
{
    cat <<'EOF'
MigrationTool shell

Komendy:
  check --target-ref REF [--repo PATH] [--config FILE]

Jeżeli --target-ref nie został podany, skrypt użyje:
  origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME

Komenda check wymaga wyłącznie: sh, git, find, sed, awk oraz sort.
Nie wymaga środowiska .NET.
EOF
}

error_count=0
warning_count=0

error()
{
    error_count=$((error_count + 1))
    printf 'ERROR %s: %s\n' "$1" "$2"
}

warning()
{
    warning_count=$((warning_count + 1))
    printf 'WARNING %s: %s\n' "$1" "$2"
}

fail()
{
    printf 'Migration check zakończył pracę błędem: %s\n' "$1" >&2
    exit 2
}

require_command()
{
    command -v "$1" >/dev/null 2>&1 ||
        fail "Brak wymaganego polecenia '$1' w obrazie pipeline."
}

absolute_path()
{
    case "$1" in
        /*) printf '%s\n' "$1" ;;
        *) printf '%s/%s\n' "$repository_root" "$1" ;;
    esac
}

read_json_string()
{
    property=$1
    file=$2

    sed -n \
        "s/.*\"$property\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" \
        "$file" |
        head -n 1
}

read_json_integer()
{
    property=$1
    file=$2

    sed -n \
        "s/.*\"$property\"[[:space:]]*:[[:space:]]*\\([0-9][0-9]*\\).*/\\1/p" \
        "$file" |
        head -n 1
}

extract_attribute_versions()
{
    awk '
        { source = source " " $0 }
        END {
            pattern = "\\[[[:space:]]*(global::)?([A-Za-z_][A-Za-z0-9_]*\\.)*Migration(Attribute)?[[:space:]]*\\([[:space:]]*[0-9]+"
            while (match(source, pattern)) {
                value = substr(source, RSTART, RLENGTH)
                sub(/^.*\(/, "", value)
                gsub(/[[:space:]]/, "", value)
                print value
                source = substr(source, RSTART + RLENGTH)
            }
        }
    '
}

write_working_tree_records()
{
    output=$1
    : >"$output"

    for directory in "$migration_root_absolute"/*; do
        [ -d "$directory" ] || continue

        folder_name=${directory##*/}
        version=${folder_name%%_*}
        name=${folder_name#*_}

        case "$folder_name" in
            *_*) ;;
            *) continue ;;
        esac
        case "$version" in
            ''|*[!0-9]*) continue ;;
        esac
        [ "$name" != "$folder_name" ] || continue

        relative_folder=${directory#"$repository_root"/}
        printf '%s|%s|%s\n' "$version" "$relative_folder" "$name" >>"$output"
    done

    sort -t '|' -k1,1n -k2,2 "$output" -o "$output"
}

write_target_records()
{
    output=$1
    : >"$output"

    git -C "$repository_root" ls-tree -r --name-only "$target_ref" -- "$migration_root_relative" |
        while IFS= read -r path; do
            case "$path" in
                "$migration_root_relative"/*/*.cs)
                    relative=${path#"$migration_root_relative"/}
                    folder_name=${relative%%/*}
                    version=${folder_name%%_*}
                    name=${folder_name#*_}

                    case "$version" in
                        ''|*[!0-9]*) continue ;;
                    esac

                    printf '%s|%s/%s|%s\n' \
                        "$version" "$migration_root_relative" "$folder_name" "$name"
                    ;;
            esac
        done |
        sort -u -t '|' -k1,1n -k2,2 >"$output"
}

validate_duplicates()
{
    records=$1
    code=$2
    prefix=$3
    duplicates="$temporary_directory/duplicates"

    awk -F '|' '
        {
            count[$1]++
            folders[$1] = folders[$1] (folders[$1] == "" ? "" : ", ") $2
        }
        END {
            for (version in count) {
                if (count[version] > 1) {
                    print version "|" folders[version]
                }
            }
        }
    ' "$records" >"$duplicates"

    while IFS='|' read -r version folders; do
        error "$code" "$prefix $version: $folders"
    done <"$duplicates"
}

validate_working_tree_structure()
{
    migration_count=$(wc -l <"$current_records" | tr -d ' ')
    if [ "$migration_count" -eq 0 ]; then
        warning "NO_MIGRATIONS" "Nie znaleziono migracji."
        return
    fi

    while IFS='|' read -r version folder name; do
        files_list="$temporary_directory/current-files"
        find "$repository_root/$folder" -type f -name '*.cs' -print |
            sort >"$files_list"

        file_count=$(wc -l <"$files_list" | tr -d ' ')
        if [ "$file_count" -eq 0 ]; then
            error "EMPTY_MIGRATION_FOLDER" \
                "Folder '$folder' nie zawiera pliku .cs."
            continue
        fi

        attributes="$temporary_directory/attributes"
        : >"$attributes"
        while IFS= read -r file; do
            extract_attribute_versions <"$file" >>"$attributes"
        done <"$files_list"

        attribute_count=$(wc -l <"$attributes" | tr -d ' ')
        if [ "$attribute_count" -ne 1 ]; then
            error "MIGRATION_ATTRIBUTE_MISSING_OR_AMBIGUOUS" \
                "W folderze '$folder' oczekiwano dokładnie jednego atrybutu [Migration(...)], znaleziono: $attribute_count."
            continue
        fi

        attribute_version=$(sed -n '1p' "$attributes")
        if [ "$attribute_version" != "$version" ]; then
            error "FOLDER_ATTRIBUTE_MISMATCH" \
                "Folder '$folder' ma wersję $version, ale atrybut [Migration(...)] ma wersję $attribute_version."
        fi
    done <"$current_records"

    current_maximum=$(cut -d '|' -f1 "$current_records" | sort -n | tail -n 1)
    [ -f "$target_version_file" ] || {
        error "TARGET_VERSION_READ_ERROR" \
            "Nie znaleziono pliku '$target_version_file_relative'."
        return
    }

    configured_target=$(read_json_integer "TargetVersion" "$target_version_file")
    if [ -z "$configured_target" ]; then
        error "TARGET_VERSION_READ_ERROR" \
            "Nie znaleziono liczbowej właściwości TargetVersion w '$target_version_file_relative'."
    elif [ "$configured_target" != "$current_maximum" ]; then
        error "TARGET_VERSION_MISMATCH" \
            "Plik '$target_version_file_relative' ma TargetVersion=$configured_target, ale najwyższa migracja ma wersję $current_maximum."
    fi
}

validate_against_target()
{
    target_maximum=0
    if [ -s "$target_records" ]; then
        target_maximum=$(cut -d '|' -f1 "$target_records" | sort -n | tail -n 1)
    fi

    while IFS='|' read -r version current_folder current_name; do
        target_line=$(awk -F '|' -v requested="$version" '$1 == requested { print; exit }' "$target_records")

        if [ -n "$target_line" ]; then
            target_name=$(printf '%s\n' "$target_line" | cut -d '|' -f3)

            if [ "$current_name" != "$target_name" ]; then
                error "VERSION_COLLISION" \
                    "Wersja $version ma inną nazwę w kodzie i w '$target_ref'. Source: '${version}_$current_name', target: '${version}_$target_name'. Zrób rebase albo uruchom sync."
            fi
            continue
        fi

        if [ "$version" -le "$target_maximum" ]; then
            error "MIGRATION_OLDER_THAN_TARGET_HEAD" \
                "Nowa migracja '${version}_$current_name' nie jest większa od najwyższej migracji $target_maximum w '$target_ref'. Uruchom migrationtool sync."
        fi
    done <"$current_records"

    current_maximum=0
    if [ -s "$current_records" ]; then
        current_maximum=$(cut -d '|' -f1 "$current_records" | sort -n | tail -n 1)
    fi
    printf 'source=%s target=%s\n' "$current_maximum" "$target_maximum"
}

command_name=${1:-help}
[ "$#" -eq 0 ] || shift

repository_path=.
configuration_path=migrationtool.json
target_ref=

while [ "$#" -gt 0 ]; do
    case "$1" in
        --repo)
            [ "$#" -ge 2 ] || fail "Opcja --repo wymaga wartości."
            repository_path=$2
            shift 2
            ;;
        --config)
            [ "$#" -ge 2 ] || fail "Opcja --config wymaga wartości."
            configuration_path=$2
            shift 2
            ;;
        --target-ref)
            [ "$#" -ge 2 ] || fail "Opcja --target-ref wymaga wartości."
            target_ref=$2
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            fail "Nieznana opcja '$1'."
            ;;
    esac
done

case "$command_name" in
    help|--help|-h)
        usage
        exit 0
        ;;
    check) ;;
    *) fail "Nieznana komenda '$command_name'." ;;
esac

for required_command in git find sed awk sort mktemp; do
    require_command "$required_command"
done

repository_root=$(git -C "$repository_path" rev-parse --show-toplevel 2>/dev/null) ||
    fail "Katalog '$repository_path' nie jest repozytorium Git."

case "$configuration_path" in
    /*) configuration_file=$configuration_path ;;
    *) configuration_file="$repository_root/$configuration_path" ;;
esac
[ -f "$configuration_file" ] ||
    fail "Nie znaleziono konfiguracji '$configuration_file'."

project_root=$(read_json_string "projectRoot" "$configuration_file")
[ -n "$project_root" ] ||
    fail "Nie znaleziono właściwości projectRoot w '$configuration_file'."

case "$project_root" in
    /*) fail "projectRoot musi być ścieżką względną wobec repozytorium." ;;
    .)
        migration_root_relative=Migrations
        target_version_file_relative=appsettings.json
        ;;
    *)
        normalized_project_root=${project_root#./}
        normalized_project_root=${normalized_project_root%/}
        migration_root_relative=$normalized_project_root/Migrations
        target_version_file_relative=$normalized_project_root/appsettings.json
        ;;
esac
migration_root_absolute=$(absolute_path "$migration_root_relative")
target_version_file=$(absolute_path "$target_version_file_relative")

[ -d "$migration_root_absolute" ] ||
    fail "Nie znaleziono katalogu migracji '$migration_root_absolute'."

if [ -z "$target_ref" ]; then
    target_branch=${CI_MERGE_REQUEST_TARGET_BRANCH_NAME:-}
    [ -n "$target_branch" ] ||
        fail "Podaj --target-ref albo ustaw CI_MERGE_REQUEST_TARGET_BRANCH_NAME."
    target_ref="origin/$target_branch"
fi

git -C "$repository_root" rev-parse --verify "$target_ref" >/dev/null 2>&1 ||
    fail "Git ref '$target_ref' nie istnieje. Pobierz branch docelowy przed uruchomieniem check."

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/migrationtool-check.XXXXXX") ||
    fail "Nie udało się utworzyć katalogu tymczasowego."
trap 'rm -rf "$temporary_directory"' EXIT HUP INT TERM

current_records="$temporary_directory/current-records"
target_records="$temporary_directory/target-records"

write_working_tree_records "$current_records"
write_target_records "$target_records"

validate_duplicates \
    "$current_records" \
    "DUPLICATE_VERSION" \
    "Wersja występuje więcej niż raz:"
validate_duplicates \
    "$target_records" \
    "TARGET_DUPLICATE_VERSION" \
    "Branch '$target_ref' zawiera więcej niż jedną migrację o wersji"
validate_working_tree_structure
validate_against_target

if [ "$error_count" -gt 0 ]; then
    printf 'Migration check: FAILED (%s błędów, %s ostrzeżeń)\n' \
        "$error_count" "$warning_count"
    exit 2
fi

printf 'Migration check: OK (%s ostrzeżeń)\n' "$warning_count"
