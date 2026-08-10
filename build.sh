#!/usr/bin/env bash
set -Eeuo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$script_dir"

configuration=${MFP_CONFIGURATION:-Release}
dry_run=${MFP_BUILD_DRY_RUN:-0}
web_dir="$script_dir/Source/MultiFunPlayer.Web"
docs_dir="$script_dir/Docs"
server_project="$script_dir/Source/MultiFunPlayer.Server/MultiFunPlayer.Server.csproj"
desktop_project="$script_dir/Source/MultiFunPlayer/MultiFunPlayer.csproj"
compose_file="$script_dir/deploy/docker/compose.yaml"

usage() {
    cat <<'EOF'
Usage: ./build.sh COMMAND [TARGET] [OPTIONS]

Build and verification:
  doctor                         Show resolved tools and optional capabilities.
  bootstrap                      Restore locked .NET, Web, and Docs dependencies.
  build [Debug|Release]          Build Web, solution, and documentation.
  test [--browser]               Run portable tests and Web/Docs checks.
  validate [--skip-browser] ...  Run the repository validation matrix.
  soak 30|8h|SECONDS             Run the portable process soak.

Run modes:
  run server [options]           Build and run ASP.NET Server + SPA.
  run web [options]              Run the Vite Web Demo development server.
  run desktop [options]          Run the retained Windows WPF host.
  run docker [options]           Start the combined Docker deployment.
  run simulators [options]       Start Docker plus TCP/UDP simulators.
  stop docker [--volumes]        Stop Compose; volumes are preserved by default.
  status docker                  Show Compose service status.
  logs docker [--follow]         Show combined-server container logs.

Package modes:
  package server [target]        Publish and archive the combined Server + SPA.
                                 Targets: portable, linux-x64, linux-arm64.
  package docker [target]        Build a container image.
                                 Targets: local, amd64, arm64, all, web-demo.
  package fnos [target]          Build and validate fnOS FPK files.
                                 Targets: x86, arm, all.
  package validate               Validate Docker/fnOS repository contracts.
  package all                    Build server, Docker, and fnOS deliverables.

Server run options:
  --configuration NAME           Debug or Release (default: Release).
  --url URL                      Listen URL (default: http://127.0.0.1:8080).
  --no-restore                   Skip locked .NET restore.
  --no-web-build                 Use the existing Web dist/static preview.

Web run options:
  --host HOST                    Vite host (default: 0.0.0.0).
  --port PORT                    Vite port (default: 5173).

Docker run options:
  --foreground                   Do not detach Compose.
  --no-build                     Do not rebuild images before starting.

Server package options:
  --output-dir PATH              Artifact directory.
  --self-contained              Force a self-contained RID package.
  --framework-dependent         Require the .NET 10 runtime on the target.

fnOS package options:
  --force                        Replace existing FPK output files.
  --output-dir PATH              Used for a single x86 or arm target.

Environment overrides:
  MFP_DOTNET, MFP_NPM, FNPACK, MFP_CONFIGURATION, MFP_LOCAL_ROOT,
  MFP_IMAGE, MFP_IMAGE_REPOSITORY, MFP_BUILD_DRY_RUN=1.

Examples:
  ./build.sh run server
  ./build.sh run web
  MFP_AUTH_TOKEN='long-random-token' ./build.sh run docker
  ./build.sh package server linux-x64
  ./build.sh package docker all
  FNPACK=/path/to/fnpack ./build.sh package fnos all
EOF
}

log() {
    printf '[build.sh] %s\n' "$*"
}

die() {
    printf '[build.sh] ERROR: %s\n' "$*" >&2
    exit 1
}

print_command() {
    printf '[build.sh] +'
    printf ' %q' "$@"
    printf '\n'
}

run_command() {
    print_command "$@"
    if [[ "$dry_run" != 1 ]]; then
        "$@"
    fi
}

run_in_directory() {
    local directory=$1
    shift
    printf '[build.sh] + cd %q &&' "$directory"
    printf ' %q' "$@"
    printf '\n'
    if [[ "$dry_run" != 1 ]]; then
        (cd "$directory" && "$@")
    fi
}

exec_command() {
    print_command "$@"
    if [[ "$dry_run" == 1 ]]; then
        return
    fi
    exec "$@"
}

resolve_dotnet() {
    if [[ -n ${DOTNET:-} ]]; then
        return
    fi
    if [[ -n ${MFP_DOTNET:-} ]]; then
        DOTNET=$MFP_DOTNET
    elif command -v dotnet >/dev/null 2>&1; then
        DOTNET=$(command -v dotnet)
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET="$HOME/.dotnet/dotnet"
    else
        die '.NET SDK 10.0.302 is required. Install it or set MFP_DOTNET.'
    fi
    [[ -x "$DOTNET" ]] || die "dotnet is not executable: $DOTNET"
    export DOTNET_ROOT=${DOTNET_ROOT:-$(dirname -- "$DOTNET")}
    export PATH="$(dirname -- "$DOTNET"):$PATH"
}

resolve_npm() {
    if [[ -n ${NPM:-} ]]; then
        return
    fi
    if [[ -n ${MFP_NPM:-} ]]; then
        NPM=$MFP_NPM
    elif [[ -x "$HOME/.local/mfp-node/bin/npm" ]]; then
        NPM="$HOME/.local/mfp-node/bin/npm"
    elif command -v npm >/dev/null 2>&1; then
        NPM=$(command -v npm)
    else
        die 'npm is required. Install Node.js or set MFP_NPM.'
    fi
    [[ -x "$NPM" ]] || die "npm is not executable: $NPM"
    export PATH="$(dirname -- "$NPM"):$PATH"
    NODE=$(command -v node || true)
    [[ -n "$NODE" && -x "$NODE" ]] || die "node is not available beside the selected npm: $NPM"
}

resolve_python() {
    if [[ -n ${PYTHON:-} ]]; then
        return
    fi
    PYTHON=${MFP_PYTHON:-$(command -v python3 || true)}
    [[ -n "$PYTHON" && -x "$PYTHON" ]] || die 'python3 is required. Set MFP_PYTHON if it is not on PATH.'
}

resolve_docker() {
    DOCKER=${MFP_DOCKER:-$(command -v docker || true)}
    [[ -n "$DOCKER" && -x "$DOCKER" ]] || die 'Docker is required for this command.'
    if [[ "$dry_run" != 1 ]]; then
        "$DOCKER" compose version >/dev/null 2>&1 || die 'Docker Compose v2 is required.'
    fi
}

ensure_web_dependencies() {
    resolve_npm
    if [[ "$dry_run" == 1 || ! -f "$web_dir/node_modules/.package-lock.json" || "$web_dir/package-lock.json" -nt "$web_dir/node_modules/.package-lock.json" ]]; then
        run_command "$NPM" ci --ignore-scripts --prefix "$web_dir"
    fi
}

ensure_docs_dependencies() {
    resolve_npm
    if [[ "$dry_run" == 1 || ! -f "$docs_dir/node_modules/.package-lock.json" || "$docs_dir/package-lock.json" -nt "$docs_dir/node_modules/.package-lock.json" ]]; then
        run_command "$NPM" ci --ignore-scripts --prefix "$docs_dir"
    fi
}

restore_server() {
    resolve_dotnet
    run_command "$DOTNET" restore "$server_project" --locked-mode --disable-parallel
}

restore_solution() {
    resolve_dotnet
    run_command "$DOTNET" restore "$script_dir/MultiFunPlayer.sln" --locked-mode --disable-parallel -p:EnableWindowsTargeting=true
}

build_web() {
    local checks=${1:-false}
    ensure_web_dependencies
    if [[ "$checks" == true ]]; then
        run_command "$NPM" run typecheck --prefix "$web_dir"
        run_command "$NPM" run check:static --prefix "$web_dir"
    fi
    run_command "$NPM" run build --prefix "$web_dir"
}

build_docs() {
    ensure_docs_dependencies
    run_command "$NPM" run check:dependency-security --prefix "$docs_dir"
    run_command "$NPM" run build --prefix "$docs_dir"
}

prepare_local_roots() {
    local local_root=${MFP_LOCAL_ROOT:-"$script_dir/.local/mfp"}
    mkdir -p "$local_root/config" "$local_root/keys" "$local_root/scripts" "$local_root/logs"
    export MFP_CONFIG_ROOT=${MFP_CONFIG_ROOT:-"$local_root/config"}
    export MFP_KEYS_ROOT=${MFP_KEYS_ROOT:-"$local_root/keys"}
    export MFP_SCRIPTS_ROOT=${MFP_SCRIPTS_ROOT:-"$local_root/scripts"}
    export MFP_LOGS_ROOT=${MFP_LOGS_ROOT:-"$local_root/logs"}
}

require_docker_auth() {
    if [[ -z ${MFP_AUTH_TOKEN:-} && -z ${MFP_PAIRING_TOKEN:-} && ${MFP_ALLOW_UNAUTHENTICATED:-0} != 1 ]]; then
        die 'Docker publishes a host port. Set MFP_AUTH_TOKEN or MFP_PAIRING_TOKEN, or explicitly set MFP_ALLOW_UNAUTHENTICATED=1 for an isolated local run.'
    fi
}

validate_packaging() {
    run_command "$script_dir/deploy/validate-packaging.sh"
}

write_checksum() {
    local file=$1
    local checksum="$file.sha256"
    if [[ "$dry_run" == 1 ]]; then
        log "Would write SHA-256 checksum: $checksum"
        return
    fi
    (cd "$(dirname -- "$file")" && sha256sum "$(basename -- "$file")" > "$(basename -- "$checksum")")
    log "Checksum: $checksum"
}

validate_configuration() {
    case "$1" in
        Debug|Release) ;;
        *) die "unsupported configuration: $1" ;;
    esac
}

cmd_doctor() {
    resolve_dotnet
    resolve_npm
    resolve_python
    printf 'Repository: %s\n' "$script_dir"
    printf 'dotnet:    %s (%s)\n' "$DOTNET" "$("$DOTNET" --version)"
    printf 'npm:       %s (%s)\n' "$NPM" "$("$NPM" --version)"
    printf 'node:      %s (%s)\n' "$NODE" "$("$NODE" --version)"
    printf 'python3:   %s (%s)\n' "$PYTHON" "$("$PYTHON" --version 2>&1)"
    if command -v docker >/dev/null 2>&1; then
        printf 'docker:    %s\n' "$(docker --version)"
        docker compose version || true
    else
        printf 'docker:    unavailable\n'
    fi
    if [[ -n ${FNPACK:-} && -x ${FNPACK:-} ]]; then
        printf 'fnpack:    %s\n' "$FNPACK"
    elif command -v fnpack >/dev/null 2>&1; then
        printf 'fnpack:    %s\n' "$(command -v fnpack)"
    else
        printf 'fnpack:    unavailable (set FNPACK for fnOS packaging)\n'
    fi
}

cmd_bootstrap() {
    restore_solution
    ensure_web_dependencies
    ensure_docs_dependencies
}

cmd_build() {
    local requested_configuration=${1:-$configuration}
    shift || true
    (($# == 0)) || die "unknown build option: $1"
    validate_configuration "$requested_configuration"
    restore_solution
    build_web true
    run_command "$DOTNET" build "$script_dir/MultiFunPlayer.sln" --configuration "$requested_configuration" --no-restore -p:EnableWindowsTargeting=true
    build_docs
}

cmd_run_server() {
    local requested_configuration=$configuration
    local url=${ASPNETCORE_URLS:-http://127.0.0.1:8080}
    local restore=true
    local web_build=true
    while (($# > 0)); do
        case "$1" in
            --configuration) (($# >= 2)) || die '--configuration requires a value'; requested_configuration=$2; shift 2 ;;
            --url) (($# >= 2)) || die '--url requires a value'; url=$2; shift 2 ;;
            --no-restore) restore=false; shift ;;
            --no-web-build) web_build=false; shift ;;
            *) die "unknown server run option: $1" ;;
        esac
    done
    validate_configuration "$requested_configuration"
    resolve_dotnet
    [[ "$restore" == false ]] || restore_server
    [[ "$web_build" == false ]] || build_web false
    prepare_local_roots
    export ASPNETCORE_URLS=$url
    if [[ "$url" != http://127.0.0.1:* && "$url" != http://localhost:* && -z ${MFP_AUTH_TOKEN:-} && -z ${MFP_PAIRING_TOKEN:-} && ${MFP_ALLOW_UNAUTHENTICATED:-0} != 1 ]]; then
        die 'A non-loopback Server URL requires MFP_AUTH_TOKEN or MFP_PAIRING_TOKEN. Set MFP_ALLOW_UNAUTHENTICATED=1 only for an isolated test network.'
    fi
    log "Server URL: $ASPNETCORE_URLS"
    log "Persistent roots: $MFP_CONFIG_ROOT, $MFP_KEYS_ROOT, $MFP_SCRIPTS_ROOT, $MFP_LOGS_ROOT"
    exec_command "$DOTNET" run --project "$server_project" --configuration "$requested_configuration" --no-restore
}

cmd_run_web() {
    local host=0.0.0.0
    local port=5173
    while (($# > 0)); do
        case "$1" in
            --host) (($# >= 2)) || die '--host requires a value'; host=$2; shift 2 ;;
            --port) (($# >= 2)) || die '--port requires a value'; port=$2; shift 2 ;;
            *) die "unknown Web run option: $1" ;;
        esac
    done
    ensure_web_dependencies
    log "Web Demo: http://127.0.0.1:$port/?demo=1"
    if [[ "$dry_run" == 1 ]]; then
        print_command "$NPM" run dev -- --host "$host" --port "$port"
        return
    fi
    cd "$web_dir"
    exec "$NPM" run dev -- --host "$host" --port "$port"
}

cmd_run_desktop() {
    local requested_configuration=${1:-$configuration}
    shift || true
    (($# == 0)) || die "unknown desktop run option: $1"
    validate_configuration "$requested_configuration"
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) ;;
        *) die 'The WPF desktop host must be run on Windows (PowerShell or Git Bash).' ;;
    esac
    resolve_dotnet
    run_command "$DOTNET" restore "$desktop_project" --locked-mode
    exec_command "$DOTNET" run --project "$desktop_project" --configuration "$requested_configuration" --no-restore
}

cmd_run_docker() {
    local simulators=false
    local detached=true
    local build=true
    while (($# > 0)); do
        case "$1" in
            --simulators) simulators=true; shift ;;
            --foreground) detached=false; shift ;;
            --no-build) build=false; shift ;;
            *) die "unknown Docker run option: $1" ;;
        esac
    done
    resolve_docker
    require_docker_auth
    validate_packaging
    local args=(compose -f "$compose_file")
    [[ "$simulators" == false ]] || args+=(--profile simulators)
    args+=(up)
    [[ "$build" == false ]] || args+=(--build)
    [[ "$detached" == false ]] || args+=(-d)
    run_command "$DOCKER" "${args[@]}"
}

cmd_stop_docker() {
    local volumes=false
    while (($# > 0)); do
        case "$1" in
            --volumes) volumes=true; shift ;;
            *) die "unknown Docker stop option: $1" ;;
        esac
    done
    resolve_docker
    local args=(compose -f "$compose_file" down)
    if [[ "$volumes" == true ]]; then
        [[ ${MFP_CONFIRM_DELETE_VOLUMES:-0} == 1 ]] || die 'Deleting persistent volumes requires MFP_CONFIRM_DELETE_VOLUMES=1 together with --volumes.'
        args+=(-v)
    fi
    run_command "$DOCKER" "${args[@]}"
}

cmd_status_docker() {
    resolve_docker
    run_command "$DOCKER" compose -f "$compose_file" ps
}

cmd_logs_docker() {
    local follow=false
    if [[ ${1:-} == --follow ]]; then
        follow=true
        shift
    fi
    (($# == 0)) || die "unknown Docker logs option: $1"
    resolve_docker
    local args=(compose -f "$compose_file" logs --tail=200)
    [[ "$follow" == false ]] || args+=(-f)
    args+=(multifunplayer)
    run_command "$DOCKER" "${args[@]}"
}

cmd_test() {
    local browser=false
    if [[ ${1:-} == --browser ]]; then
        browser=true
        shift
    fi
    (($# == 0)) || die "unknown test option: $1"
    restore_solution
    local projects=(
        Source/MultiFunPlayer.Core.Tests/MultiFunPlayer.Core.Tests.csproj
        Source/MultiFunPlayer.Application.Tests/MultiFunPlayer.Application.Tests.csproj
        Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj
        Source/MultiFunPlayer.Server.Tests/MultiFunPlayer.Server.Tests.csproj
    )
    local project
    for project in "${projects[@]}"; do
        local args=(test "$script_dir/$project" --configuration Release --no-restore -p:EnableWindowsTargeting=true)
        if [[ "$project" == *Server.Tests* ]]; then
            args+=(-- xunit.parallelizeTestCollections=false)
        fi
        run_command "$DOTNET" "${args[@]}"
    done
    build_web true
    build_docs
    if [[ "$browser" == true ]]; then
        run_command "$NPM" run test:e2e --prefix "$web_dir"
    fi
}

cmd_validate() {
    resolve_dotnet
    resolve_npm
    resolve_python
    local args=("$PYTHON" "$script_dir/tools/run-validation-matrix.py")
    if (($# == 0)); then
        args+=(--output "$script_dir/artifacts/validation/validation-matrix.json")
    else
        args+=("$@")
    fi
    print_command env "MFP_DOTNET=$DOTNET" "MFP_NPM=$NPM" "${args[@]}"
    if [[ "$dry_run" != 1 ]]; then
        MFP_DOTNET=$DOTNET MFP_NPM=$NPM "${args[@]}"
    fi
}

cmd_soak() {
    local preset=${1:-30}
    shift || true
    resolve_dotnet
    resolve_python
    restore_server
    build_web false
    run_command "$DOTNET" build "$server_project" --configuration Release --no-restore
    local duration poll output
    case "$preset" in
        30|30s) duration=30; poll=0.5; output="$script_dir/artifacts/soak/portable-soak-30s.json" ;;
        8h) duration=28800; poll=1; output="$script_dir/artifacts/soak/portable-soak-8h.json" ;;
        ''|*[!0-9]*) die 'soak target must be 30, 8h, or an integer number of seconds' ;;
        *) duration=$preset; poll=1; output="$script_dir/artifacts/soak/portable-soak-${duration}s.json" ;;
    esac
    run_command "$PYTHON" "$script_dir/tools/run-portable-soak.py" \
        --dotnet "$DOTNET" \
        --duration-seconds "$duration" \
        --min-duration-seconds "$duration" \
        --poll-seconds "$poll" \
        --max-revision-interval-ms 100 \
        --max-coalesced-frames 0 \
        --output "$output" "$@"
}

cmd_package_server() {
    local target=portable
    local output_root=''
    local self_contained=auto
    if (($# > 0)) && [[ $1 != --* ]]; then
        target=$1
        shift
    fi
    while (($# > 0)); do
        case "$1" in
            --output-dir) (($# >= 2)) || die '--output-dir requires a value'; output_root=$2; shift 2 ;;
            --self-contained) self_contained=true; shift ;;
            --framework-dependent) self_contained=false; shift ;;
            *) die "unknown server package option: $1" ;;
        esac
    done
    case "$target" in
        portable) rid=''; [[ "$self_contained" != auto ]] || self_contained=false ;;
        linux-x64|linux-arm64) rid=$target; [[ "$self_contained" != auto ]] || self_contained=true ;;
        *) die "unsupported server package target: $target" ;;
    esac
    [[ -n "$rid" || "$self_contained" != true ]] || die 'A self-contained package requires linux-x64 or linux-arm64.'
    output_root=${output_root:-"$script_dir/artifacts/server/$target"}
    local publish_dir="$output_root/publish"
    local archive="$output_root/multifunplayer-server-$target.tar.gz"
    local staging_dir="$output_root/.publish-staging"
    mkdir -p "$output_root"
    if [[ "$dry_run" != 1 ]]; then
        staging_dir=$(mktemp -d "$output_root/.publish-staging.XXXXXX")
    fi
    restore_server
    build_web false
    local args=(publish "$server_project" --configuration Release --no-restore --output "$staging_dir")
    if [[ -n "$rid" ]]; then
        args+=(--runtime "$rid" --self-contained "$self_contained")
    else
        args+=(--self-contained false --property:UseAppHost=false)
    fi
    run_command "$DOTNET" "${args[@]}"
    run_command tar -C "$staging_dir" -czf "$archive" .
    run_command rm -rf -- "$publish_dir"
    run_command mv -- "$staging_dir" "$publish_dir"
    write_checksum "$archive"
    log "Server package: $archive"
}

cmd_package_docker() {
    local target=${1:-local}
    shift || true
    (($# == 0)) || die "unknown Docker package option: $1"
    resolve_docker
    validate_packaging
    local repository=${MFP_IMAGE_REPOSITORY:-multifunplayer}
    case "$target" in
        local)
            run_command "$DOCKER" build --tag "${MFP_IMAGE:-multifunplayer:local}" "$script_dir"
            ;;
        amd64|arm64)
            run_command "$DOCKER" buildx build --platform "linux/$target" --tag "$repository:local-$target" --load "$script_dir"
            ;;
        all)
            cmd_package_docker amd64
            cmd_package_docker arm64
            ;;
        web-demo)
            run_command "$DOCKER" build --tag "${MFP_WEB_IMAGE:-multifunplayer-web:local}" "$web_dir"
            ;;
        *) die "unsupported Docker package target: $target" ;;
    esac
}

cmd_package_fnos() {
    local target=${1:-x86}
    shift || true
    local force=false
    local output_dir=''
    while (($# > 0)); do
        case "$1" in
            --force) force=true; shift ;;
            --output-dir) (($# >= 2)) || die '--output-dir requires a value'; output_dir=$2; shift 2 ;;
            *) die "unknown fnOS package option: $1" ;;
        esac
    done
    validate_packaging
    local platforms=()
    case "$target" in
        x86|amd64) platforms=(x86) ;;
        arm|arm64) platforms=(arm) ;;
        all) platforms=(x86 arm) ;;
        *) die "unsupported fnOS package target: $target" ;;
    esac
    [[ ${#platforms[@]} == 1 || -z "$output_dir" ]] || die '--output-dir is only valid for one fnOS platform.'
    local platform
    for platform in "${platforms[@]}"; do
        local platform_output=${output_dir:-"$script_dir/artifacts/fnos/$platform"}
        local args=(--platform "$platform" --output-dir "$platform_output")
        [[ "$force" == false ]] || args+=(--force)
        run_command "$script_dir/deploy/fnos/build-fpk.sh" "${args[@]}"
        run_command "$script_dir/deploy/validate-fpk.sh" "$platform_output/multifunplayer.fpk" "$platform"
        if [[ "$dry_run" != 1 ]]; then
            run_in_directory "$platform_output" sha256sum --check multifunplayer.fpk.sha256
        fi
    done
}

cmd_package_all() {
    cmd_package_server portable
    cmd_package_server linux-x64
    cmd_package_server linux-arm64
    cmd_package_docker all
    cmd_package_fnos all
}

command_name=${1:-help}
shift || true

case "$command_name" in
    help|-h|--help) usage ;;
    doctor) cmd_doctor "$@" ;;
    bootstrap) cmd_bootstrap "$@" ;;
    build) cmd_build "$@" ;;
    test) cmd_test "$@" ;;
    validate) cmd_validate "$@" ;;
    soak) cmd_soak "$@" ;;
    run)
        target=${1:-server}
        shift || true
        case "$target" in
            server) cmd_run_server "$@" ;;
            web|demo) cmd_run_web "$@" ;;
            desktop|wpf) cmd_run_desktop "$@" ;;
            docker) cmd_run_docker "$@" ;;
            simulators) cmd_run_docker --simulators "$@" ;;
            *) die "unknown run target: $target" ;;
        esac
        ;;
    stop)
        target=${1:-docker}
        shift || true
        [[ "$target" == docker ]] || die "unknown stop target: $target"
        cmd_stop_docker "$@"
        ;;
    status)
        target=${1:-docker}
        shift || true
        [[ "$target" == docker ]] || die "unknown status target: $target"
        (($# == 0)) || die "unknown status option: $1"
        cmd_status_docker
        ;;
    logs)
        target=${1:-docker}
        shift || true
        [[ "$target" == docker ]] || die "unknown logs target: $target"
        cmd_logs_docker "$@"
        ;;
    package)
        target=${1:-server}
        shift || true
        case "$target" in
            server) cmd_package_server "$@" ;;
            docker) cmd_package_docker "$@" ;;
            fnos) cmd_package_fnos "$@" ;;
            validate) (($# == 0)) || die "unknown package validate option: $1"; validate_packaging ;;
            all) (($# == 0)) || die "unknown package all option: $1"; cmd_package_all ;;
            *) die "unknown package target: $target" ;;
        esac
        ;;
    server) cmd_run_server "$@" ;;
    web) cmd_run_web "$@" ;;
    docker-up) cmd_run_docker "$@" ;;
    docker-down) cmd_stop_docker "$@" ;;
    publish) cmd_package_server "$@" ;;
    fnos) cmd_package_fnos "$@" ;;
    *) die "unknown command: $command_name (run ./build.sh help)" ;;
esac
