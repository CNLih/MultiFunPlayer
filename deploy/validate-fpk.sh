#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: deploy/validate-fpk.sh PACKAGE [EXPECTED_PLATFORM]

Validate the repository-owned structure of an fnOS FPK produced by fnpack.
This does not install the package or replace target-device validation.
EOF
}

if (($# < 1 || $# > 2)); then
    usage >&2
    exit 2
fi

package=$1
expected_platform=${2:-}
test -f "$package" || { printf 'missing FPK: %s\n' "$package" >&2; exit 1; }

archive_list=$(tar -tzf "$package")
contains() {
    printf '%s\n' "$archive_list" | grep -Fxq "$1"
}

for entry in ICON.PNG ICON_256.PNG app.tgz manifest cmd/main cmd/install_init \
    cmd/install_callback cmd/upgrade_init cmd/upgrade_callback \
    cmd/uninstall_init cmd/uninstall_callback cmd/config_init cmd/config_callback \
    config/privilege config/resource wizard; do
    contains "$entry" || { printf 'FPK missing entry: %s\n' "$entry" >&2; exit 1; }
done

manifest=$(tar -xOf "$package" manifest)
for required in \
    '^appname[[:space:]]*=[[:space:]]*multifunplayer$' \
    '^version[[:space:]]*=[[:space:]]*[0-9]+\.[0-9]+\.[0-9]+$' \
    '^service_port[[:space:]]*=[[:space:]]*8080$' \
    '^desktop_uidir[[:space:]]*=[[:space:]]*ui$'; do
    printf '%s\n' "$manifest" | grep -Eq "$required" || {
        printf 'FPK manifest check failed: %s\n' "$required" >&2
        exit 1
    }
done

if [[ -n "$expected_platform" ]]; then
    printf '%s\n' "$manifest" | grep -Eq "^platform[[:space:]]*=[[:space:]]*$expected_platform$" || {
        printf 'FPK platform mismatch: expected %s\n' "$expected_platform" >&2
        exit 1
    }
fi

app_archive=$(mktemp)
cleanup() {
    rm -f "$app_archive"
}
trap cleanup EXIT
tar -xOf "$package" app.tgz > "$app_archive"
app_list=$(tar -tzf "$app_archive")
app_contains() {
    printf '%s\n' "$app_list" | grep -Fxq "$1"
}

for entry in docker/docker-compose.yaml ui/config ui/images/icon_64.png ui/images/icon_256.png; do
    app_contains "$entry" || { printf 'FPK app.tgz missing entry: %s\n' "$entry" >&2; exit 1; }
done

printf 'FPK valid: %s\n' "$package"
