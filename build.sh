#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

log() { echo "[build] $*"; }
err() { echo "[build] ERROR: $*" >&2; }

SHIM="target/release/libmisaml_shim.so"
CLI="target/release/misaml-cli"
CORE_PUBLISH="MISAML.Core/bin/Release/net8.0/publish"

compile() {
    log "Building Rust..."
    cargo build --release

    log "Publishing MISAML.Core..."
    (cd MISAML.Core && dotnet publish -c Release)

    for f in "$SHIM" "$CLI"; do
        [ -f "$f" ] || { err "Missing: $f"; exit 1; }
    done

    [ -d "$CORE_PUBLISH" ] || { err "Missing: $CORE_PUBLISH"; exit 1; }
}

stage_binaries() {
    mkdir -p dist/MISAML

    cp "$CLI" dist/
    cp "$SHIM" dist/MISAML/
    cp "$CORE_PUBLISH"/MISAML.Core.dll dist/MISAML/
    cp "$CORE_PUBLISH"/0Harmony.dll dist/MISAML/
}

do_build() {
    compile

    log "Staging dist..."
    rm -rf dist
    stage_binaries

    log "dist/ ready:"
    find dist -maxdepth 4 | sort

    log "Run with: ./dist/misaml-cli"
}

do_rebuild() {
    [ -d "dist" ] || {
        err "dist/ not found. Run './build.sh build' first."
        exit 1
    }

    compile

    log "Restaging binaries..."
    stage_binaries

    log "Rebuild complete:"
    find dist -maxdepth 4 | sort

    log "Run with: ./dist/misaml-cli"
}

do_run() {
    [ -x "./dist/misaml-cli" ] || {
        err "./dist/misaml-cli not found. Run './build.sh build' first."
        exit 1
    }

    ./dist/misaml-cli
}

do_clean() {
    log "Cleaning code build artifacts..."
    cargo clean
    rm -rf MISAML.Core/bin MISAML.Core/obj
}

do_clean_all() {
    do_clean
    rm -rf dist
}

usage() {
    cat <<EOF
Usage: ./build.sh <command>

Commands:
  build        Build Rust + MISAML.Core and stage dist/
  rebuild      Rebuild and restage binaries
  run          Launch ./dist/misaml-cli
  clean        Remove code build artifacts (target/, bin/, obj/)
  clean-all    Remove everything (clean + dist/)
EOF
}

case "${1:-}" in
    build)   do_build ;;
    rebuild) do_rebuild ;;
    run)     do_run ;;
    clean)   do_clean ;;
    clean-all) do_clean_all ;;
    *)       usage; exit 1 ;;
esac