#!/usr/bin/env bash
# ============================================================================
# install-deps.sh — One-click dependency installer for MultiFunPlayer
#
# What it installs (the "libs" needed to build & run this project):
#   [Toolchains / runtimes]
#     * .NET SDK 10.0.302  (pinned by ./global.json; required by build.sh)
#     * Node.js >= 20 + npm  (Web SPA + Docs)
#     * python3            (required by build.sh)
#     * git                (used by GitVersion for versioning)
#   [Optional]
#     * Docker + Docker Compose v2   (--with-docker : ./build.sh run/package docker)
#     * fnpack                      (--with-fnos  : ./build.sh package fnos)
#   [Project dependencies — restored locally]
#     * NuGet packages     (dotnet restore --locked-mode)
#     * Web  npm packages  (npm ci)
#     * Docs npm packages  (npm ci)
#
# Usage:
#   ./install-deps.sh                 # system tools + restore project deps
#   ./install-deps.sh --with-docker   # also install Docker
#   ./install-deps.sh --with-fnos     # also install fnpack (fnOS packaging)
#   ./install-deps.sh --no-restore    # only install toolchains, skip restore
# ============================================================================
set -Eeuo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

INSTALL_DOCKER=0
INSTALL_FNPACK=0
DO_RESTORE=1
DOTNET_VERSION="10.0.302"          # matches ./global.json
DOTNET_DIR="${DOTNET_DIR:-$HOME/.dotnet}"
NODE_MAJOR="${NODE_MAJOR:-22}"     # Node 22 LTS (>= 20 required by Docs)

for arg in "$@"; do
    case "$arg" in
        --with-docker) INSTALL_DOCKER=1 ;;
        --with-fnos)   INSTALL_FNPACK=1 ;;
        --no-restore)  DO_RESTORE=0 ;;
        -h|--help)
            sed -n '1,30p' "$0" | sed -n 's/^# \{0,3\}//p'
            exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 1 ;;
    esac
done

log() { printf '\n\033[1;32m[install-deps]\033[0m %s\n' "$*"; }
info() { printf '\033[1;34m[install-deps]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[install-deps]\033[0m WARN: %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[install-deps]\033[0m ERROR: %s\n' "$*" >&2; exit 1; }

# --- Run a command as root (sudo if not already root) -----------------------
maybe_sudo() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
    elif command -v sudo >/dev/null 2>&1; then
        sudo "$@"
    else
        die "Not running as root and 'sudo' not found. Please run as root or install sudo."
    fi
}

# --- Detect package manager -------------------------------------------------
DISTRO=""
if [[ -f /etc/os-release ]]; then
    . /etc/os-release
    DISTRO="$ID"
fi

case "$DISTRO" in
    ubuntu|debian)
        PKG_INSTALL=(apt-get install -y)
        ;;
    *)
        warn "Unsupported distro '$DISTRO' — only Debian/Ubuntu (apt) are auto-installed here."
        warn "Install .NET SDK $DOTNET_VERSION, Node.js >= 20, python3 and git manually, then re-run."
        DISTRO="unknown"
        ;;
esac

# ============================================================================
# 1. System packages
# ============================================================================
if [[ "$DISTRO" != "unknown" ]]; then
    log "Installing system packages (curl, ca-certificates, git, python3, build tools)..."
    maybe_sudo apt-get update
    maybe_sudo "${PKG_INSTALL[@]}" \
        curl wget ca-certificates unzip \
        git build-essential \
        python3 \
        libicu-dev \
        libssl-dev \
        >/dev/null
fi

# ============================================================================
# 2. .NET SDK
# ============================================================================
if ! command -v dotnet >/dev/null 2>&1 && [[ ! -x "$DOTNET_DIR/dotnet" ]]; then
    log "Installing .NET SDK $DOTNET_VERSION to $DOTNET_DIR ..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --version "$DOTNET_VERSION" --install-dir "$DOTNET_DIR"
    rm -f /tmp/dotnet-install.sh
else
    info ".NET SDK already present."
fi

# Make dotnet available on PATH for this shell & future shells
if [[ -x "$DOTNET_DIR/dotnet" ]] && ! command -v dotnet >/dev/null 2>&1; then
    export PATH="$DOTNET_DIR:$PATH"
    export DOTNET_ROOT="$DOTNET_DIR"
    if [[ -f "$HOME/.bashrc" ]]; then
        grep -q "DOTNET_ROOT" "$HOME/.bashrc" || {
            printf '\nexport DOTNET_ROOT="%s"\nexport PATH="%s:$PATH"\n' "$DOTNET_DIR" "$DOTNET_DIR" >> "$HOME/.bashrc"
        }
    fi
fi
command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH after install. Re-open your shell or add $DOTNET_DIR to PATH."
DOTNET=$(command -v dotnet)
info "dotnet: $DOTNET ($("$DOTNET" --version))"

# ============================================================================
# 3. Node.js + npm
# ============================================================================
install_node() {
    log "Installing Node.js $NODE_MAJOR LTS via NodeSource ..."
    if [[ "$DISTRO" != "unknown" ]]; then
        curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" -o /tmp/nodesource_setup.sh
        maybe_sudo bash /tmp/nodesource_setup.sh
        rm -f /tmp/nodesource_setup.sh
        maybe_sudo "${PKG_INSTALL[@]}" nodejs >/dev/null
    else
        die "Cannot auto-install Node on this distro. Install Node.js >= 20 manually and re-run."
    fi
}

if ! command -v node >/dev/null 2>&1 || ! command -v npm >/dev/null 2>&1; then
    install_node
else
    info "Node.js already present: $(node --version) / npm $(npm --version)"
fi
NODE_VER=$(node --version | sed 's/^v//; s/\..*//')
[[ "$NODE_VER" -ge 20 ]] || warn "Node.js >= 20 is recommended (Docs requires >= 20)."
command -v npm >/dev/null 2>&1 || die "npm not found on PATH after install."

# ============================================================================
# 4. Optional: Docker + Compose v2
# ============================================================================
if [[ "$INSTALL_DOCKER" == 1 ]]; then
    if command -v docker >/dev/null 2>&1; then
        info "Docker already present: $(docker --version)"
        docker compose version >/dev/null 2>&1 || warn "Docker present but 'docker compose' (v2 plugin) not found."
    else
        log "Installing Docker Engine + Compose v2 ..."
        curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
        maybe_sudo sh /tmp/get-docker.sh
        rm -f /tmp/get-docker.sh
    fi
fi

# ============================================================================
# 5. Optional: fnpack (fnOS packaging)
#    Pinned official binary — the same source/checksum the CI uses.
# ============================================================================
if [[ "$INSTALL_FNPACK" == 1 ]]; then
    if command -v fnpack >/dev/null 2>&1 || [[ -n "${FNPACK:-}" ]]; then
        info "fnpack already present: $(command -v fnpack || echo "$FNPACK")"
    else
        FNPACK_URL="https://static2.fnnas.com/fnpack/fnpack-1.2.3-linux-amd64"
        FNPACK_SHA256="54b97fa7b70968c4d05c79840f5daeff508957d0bb2062fdb0376d00d9615c93"
        FNPACK_DIR="${FNPACK_DIR:-$HOME/.local/bin}"
        FNPACK_BIN="$FNPACK_DIR/fnpack"
        mkdir -p "$FNPACK_DIR"
        log "Downloading pinned official fnpack (v1.2.3, linux-amd64) ..."
        curl --fail --location --silent --show-error "$FNPACK_URL" --output "$FNPACK_BIN"
        echo "$FNPACK_SHA256  $FNPACK_BIN" | sha256sum --check || {
            rm -f "$FNPACK_BIN"; die "fnpack checksum mismatch — refusing to keep the downloaded binary."
        }
        chmod 0755 "$FNPACK_BIN"
        export FNPACK="$FNPACK_BIN"
        info "fnpack installed: $FNPACK_BIN (export FNPACK=$FNPACK_BIN when packaging, or add $FNPACK_DIR to PATH)"
    fi
fi

# ============================================================================
# 6. Restore project dependencies (NuGet + npm)
# ============================================================================
if [[ "$DO_RESTORE" == 1 ]]; then
    log "Restoring NuGet packages (dotnet restore --locked-mode) ..."
    "$DOTNET" restore MultiFunPlayer.sln --locked-mode --disable-parallel -p:EnableWindowsTargeting=true

    log "Installing Web dependencies (npm ci) ..."
    (cd Source/MultiFunPlayer.Web && npm ci --ignore-scripts)

    log "Installing Docs dependencies (npm ci) ..."
    (cd Docs && npm ci --ignore-scripts)
else
    info "Skipping project dependency restore (--no-restore)."
fi

# ============================================================================
# 7. Summary
# ============================================================================
echo
echo "================================================================"
echo "  MultiFunPlayer — dependency install complete"
echo "================================================================"
printf '  dotnet : %s (%s)\n' "$(command -v dotnet)" "$(dotnet --version)"
printf '  node   : %s\n' "$(node --version)"
printf '  npm    : %s\n' "$(npm --version)"
printf '  python3: %s\n' "$(command -v python3 >/dev/null && python3 --version || echo 'missing')"
printf '  git    : %s\n' "$(command -v git >/dev/null && git --version || echo 'missing')"
printf '  docker : %s\n' "$(command -v docker >/dev/null && docker --version || echo 'not installed')"
printf '  fnpack : %s\n' "$(command -v fnpack >/dev/null && echo "$(command -v fnpack)" || echo 'not installed (use --with-fnos)')"
echo "---------------------------------------------------------------"
echo "  Next steps:"
echo "    ./build.sh doctor          # verify the full toolchain"
echo "    ./build.sh run server      # run the ASP.NET Server + SPA"
echo "    ./build.sh run web         # run the Vite Web demo (dev)"
echo "    ./build.sh run docker      # run the Docker deployment"
echo "================================================================"
