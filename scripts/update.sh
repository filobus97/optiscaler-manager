#!/bin/sh
# OptiScaler Manager - in-place updater for Linux and macOS.
# GPL-3.0-or-later. See repository LICENSE.
#
# Updates the installed OptiScaler Manager to the latest GitHub release,
# in place, WITHOUT touching your data: all settings, imported DLLs, .ini
# profiles, backups and the download cache live in your OS config directory
# (~/.config/OptiscalerManager or ~/Library/Application Support/OptiscalerManager),
# not in the install folder — so replacing the program here leaves them intact.
#
# Usage:  sh update.sh [--force] [--dir <install-dir>]
#   --force           reinstall even if already on the latest version
#   --dir <path>      install dir to update (default: the folder this script is in)
#
# The whole script is wrapped in main() so it is fully parsed before it runs,
# which makes it safe for the update to overwrite this very file mid-run.

REPO="filobus97/optiscaler-manager"
ASSET_PREFIX="OptiscalerManager"

main() {
    set -eu

    FORCE=0
    INSTALL_DIR=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --force) FORCE=1 ;;
            --dir) shift; INSTALL_DIR="${1:-}" ;;
            -h|--help) usage; exit 0 ;;
            *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
        esac
        shift
    done

    # Default install dir = the directory this script lives in.
    if [ -z "$INSTALL_DIR" ]; then
        INSTALL_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
    fi
    [ -d "$INSTALL_DIR" ] || die "Install dir not found: $INSTALL_DIR"

    need curl
    need unzip

    RID=$(detect_rid)
    echo "Platform: $RID"
    echo "Install dir: $INSTALL_DIR"

    echo "Checking the latest release…"
    JSON=$(curl -fsSL -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/$REPO/releases/latest") \
        || die "Could not reach GitHub. Check your connection (or GitHub rate limits) and retry."

    LATEST=$(printf '%s' "$JSON" | tr ',{}' '\n' \
        | grep '"tag_name"' | head -n1 \
        | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
    [ -n "$LATEST" ] || die "Could not determine the latest version from GitHub."
    LATEST_NUM=$(printf '%s' "$LATEST" | sed 's/^v//')
    echo "Latest release: $LATEST"

    CURRENT=""
    if [ -f "$INSTALL_DIR/VERSION" ]; then
        CURRENT=$(tr -d ' \t\r\n' < "$INSTALL_DIR/VERSION")
        echo "Installed version: $CURRENT"
    fi

    if [ "$FORCE" -eq 0 ] && [ -n "$CURRENT" ] && [ "$CURRENT" = "$LATEST_NUM" ]; then
        echo "Already up to date ($CURRENT). Use --force to reinstall."
        exit 0
    fi

    URL=$(printf '%s' "$JSON" | tr ',{}' '\n' \
        | grep 'browser_download_url' | grep -- "-$RID\.zip" | head -n1 \
        | sed -E 's/.*(https:[^"]+).*/\1/')
    [ -n "$URL" ] || die "No asset for $RID in release $LATEST (expected ${ASSET_PREFIX}-${LATEST_NUM}-${RID}.zip)."

    TMP=$(mktemp -d 2>/dev/null || mktemp -d -t osmupd)
    trap 'rm -rf "$TMP"' EXIT

    echo "Downloading $URL"
    curl -fL --progress-bar -o "$TMP/pkg.zip" "$URL" || die "Download failed."

    echo "Extracting…"
    mkdir -p "$TMP/extract"
    unzip -oq "$TMP/pkg.zip" -d "$TMP/extract" || die "Extraction failed (corrupt download?)."

    echo "Installing to $INSTALL_DIR …"
    # Copy the new payload over the install dir. User data is elsewhere, so this
    # only replaces the program, its bundled config.json template and these scripts.
    cp -a "$TMP/extract/." "$INSTALL_DIR/" || die "Copy failed. Is the app closed and the folder writable?"

    # Ensure the app and updater stay executable.
    for f in "$ASSET_PREFIX" update.sh; do
        [ -f "$INSTALL_DIR/$f" ] && chmod +x "$INSTALL_DIR/$f" 2>/dev/null || true
    done

    echo "Done. Updated to $LATEST. Your settings, profiles, DLLs and backups were untouched."
}

usage() {
    cat <<EOF
OptiScaler Manager updater (Linux/macOS)
Usage: sh update.sh [--force] [--dir <install-dir>]
  --force        reinstall even if already on the latest version
  --dir <path>   install dir to update (default: this script's folder)
EOF
}

die() { echo "Error: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed."; }

detect_rid() {
    os=$(uname -s); arch=$(uname -m)
    case "$os" in
        Linux)
            case "$arch" in
                x86_64|amd64) echo "linux-x64" ;;
                *) die "Unsupported Linux architecture: $arch (only linux-x64 is published)." ;;
            esac ;;
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                x86_64) echo "osx-x64" ;;
                *) die "Unsupported macOS architecture: $arch." ;;
            esac ;;
        *) die "Unsupported OS: $os. Use update.ps1 on Windows." ;;
    esac
}

main "$@"
