#!/usr/bin/env bash
set -Eeuo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_dir="$script_dir/fnpack"
output_dir=${MFP_FNOS_OUTPUT_DIR:-"$script_dir/../../artifacts/fnos"}
platform=x86
force=false
fnpack_cmd=${FNPACK:-fnpack}

usage() {
    cat <<'EOF'
Usage: deploy/fnos/build-fpk.sh [options]

Options:
  --platform x86|arm       Build the fnOS x86 or ARM package (default: x86).
  --output-dir PATH        Write the FPK to PATH (default: artifacts/fnos).
  --force                  Replace an existing multifunplayer.fpk.
  -h, --help               Show this help.

The image referenced by app/docker/docker-compose.yaml must be published or
loaded on the target fnOS host before installation. This script only builds
the FPK; it does not install or claim target-device compatibility.
EOF
}

while (($# > 0)); do
    case "$1" in
        --platform)
            (($# >= 2)) || { printf '%s\n' "--platform requires a value" >&2; exit 2; }
            platform=$2
            shift 2
            ;;
        --output-dir)
            (($# >= 2)) || { printf '%s\n' "--output-dir requires a value" >&2; exit 2; }
            output_dir=$2
            shift 2
            ;;
        --force)
            force=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

case "$platform" in
    amd64|x86) platform=x86 ;;
    arm64|arm) platform=arm ;;
    *)
        printf 'unsupported fnOS platform: %s (expected x86 or arm)\n' "$platform" >&2
        exit 2
        ;;
esac

test -d "$project_dir/app" || { printf '%s\n' "missing fnpack project: $project_dir" >&2; exit 1; }
test -f "$project_dir/manifest" || { printf '%s\n' "missing fnpack manifest" >&2; exit 1; }

if ! command -v "$fnpack_cmd" >/dev/null 2>&1 && ! test -x "$fnpack_cmd"; then
    printf 'fnpack is required; set FNPACK to the official binary path\n' >&2
    exit 127
fi

mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
output_file="$output_dir/multifunplayer.fpk"
if test -e "$output_file" && [[ "$force" != true ]]; then
    printf 'refusing to overwrite %s; pass --force to replace it\n' "$output_file" >&2
    exit 2
fi

tmp_root=$(mktemp -d)
cleanup() {
    rm -rf "$tmp_root"
}
trap cleanup EXIT

tmp_project="$tmp_root/fnpack"
cp -a "$project_dir" "$tmp_project"
sed -i -E "s/^platform[[:space:]]*=.*/platform              = $platform/" "$tmp_project/manifest"
find "$tmp_project/cmd" -type f -exec chmod 0755 {} +

build_dir="$output_dir"
cd "$build_dir"
"$fnpack_cmd" build --directory "$tmp_project"

test -f "$output_file" || { printf 'fnpack did not create %s\n' "$output_file" >&2; exit 1; }
printf 'Built %s (%s)\n' "$output_file" "$(du -h "$output_file" | cut -f1)"
checksum_file="$output_file.sha256"
(cd "$output_dir" && sha256sum "$(basename "$output_file")") | tee "$checksum_file"
printf 'Checksum: %s\n' "$checksum_file"
