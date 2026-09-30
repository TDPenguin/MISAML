#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

log() { echo "[build] $*"; }
err() { echo "[build] ERROR: $*" >&2; }

SHIM="target/release/libmisaml_shim.so"
CLI="target/release/misaml-cli"
CORE_PUBLISH="MISAML.Core/bin/Release/net8.0/publish"

RUNTIME_VERSION="8.0.30"
RUNTIME_URL="https://builds.dotnet.microsoft.com/dotnet/Runtime/${RUNTIME_VERSION}/dotnet-runtime-${RUNTIME_VERSION}-linux-x64.tar.gz"

do_build() {
    log "Building Rust..."
    cargo build --release

    log "Publishing MISAML.Core..."
    (cd MISAML.Core && dotnet publish -c Release)

    log "Staging dist..."
    rm -rf dist
    mkdir -p dist/core

    for f in "$SHIM" "$CLI"; do
        [ -f "$f" ] || { err "Missing: $f"; exit 1; }
    done

    [ -d "$CORE_PUBLISH" ] || {
        err "Missing: $CORE_PUBLISH"
        exit 1
    }

    log "Downloading .NET runtime ${RUNTIME_VERSION}..."
    curl -fL "$RUNTIME_URL" | tar -xz -C dist/core

    cp "$SHIM" dist/
    cp "$CLI" dist/

    cp "$CORE_PUBLISH"/*.dll dist/

    log "dist/ ready, run: ./dist/misaml-cli"
}

do_rebuild() {
    log "Rebuilding Rust..."
    cargo build --release

    log "Publishing MISAML.Core..."
    (cd MISAML.Core && dotnet publish -c Release)

    [ -d "dist/core" ] || {
        err "dist/core does not exist. Run './build.sh build' first."
        exit 1
    }

    for f in "$SHIM" "$CLI"; do
        [ -f "$f" ] || { err "Missing: $f"; exit 1; }
    done

    [ -d "$CORE_PUBLISH" ] || {
        err "Missing: $CORE_PUBLISH"
        exit 1
    }

    log "Updating dist/ without reinstalling .NET runtime..."

    cp "$SHIM" dist/
    cp "$CLI" dist/

    find dist -maxdepth 1 -type f -name '*.dll' -delete

    cp "$CORE_PUBLISH"/*.dll dist/

    log "Rebuild complete, run: ./dist/misaml-cli"
}

do_run() {
    [ -x "./dist/misaml-cli" ] || {
        err "./dist/misaml-cli not found. Run './build.sh build' first."
        exit 1
    }

    ./dist/misaml-cli
}

do_clean() {
    log "Cleaning build artifacts..."
    cargo clean
    rm -rf \
        MISAML.Core/bin \
        MISAML.Core/obj
}

do_clean_dist() {
    log "Cleaning dist..."
    rm -rf dist
}

do_clean_all() {
    do_clean
    do_clean_dist
}

usage() {
    cat <<EOF
Usage: ./build.sh <command>

Commands:
  build        Build everything and stage dist/
  rebuild      Rebuild Rust/Core without reinstalling .NET runtime
  run          Launch ./dist/misaml-cli
  clean        Remove build artifacts
  clean-dist   Remove dist/
  clean-all    Remove all build artifacts
EOF
}

case "${1:-}" in
    build)      do_build ;;
    rebuild)    do_rebuild ;;
    run)        do_run ;;
    clean)      do_clean ;;
    clean-dist) do_clean_dist ;;
    clean-all)  do_clean_all ;;
    *)          usage; exit 1 ;;
esac