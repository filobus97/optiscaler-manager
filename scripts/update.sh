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
# Usage:  sh update.sh [--force] [--dir <install-dir>] [--wait-pid <pid>] [--relaunch]
#   --force           reinstall even if already on the latest version
#   --dir <path>      install dir to update (default: the folder this script is in)
#   --wait-pid <pid>  wait (up to 60s) for that process to exit before updating —
#                     used by the app's in-app "Update now" button
#   --relaunch        start the app again when the updater finishes (on ANY outcome,
#                     so a failed download still brings the app back)
#
# Testability overrides (used by the repo's harness, not for normal use):
#   OSM_UPDATE_URL   direct URL of the zip to install (skips the GitHub query)
#   OSM_UPDATE_TAG   version tag to report for that zip (e.g. v9.9.9)
#
# The whole script is wrapped in main() so it is fully parsed before it runs,
# which makes it safe for the update to overwrite this very file mid-run.

REPO="filobus97/optiscaler-manager"
ASSET_PREFIX="OptiscalerManager"

RELAUNCH=0
INSTALL_DIR=""

# Runs from the EXIT trap: bring the app back regardless of how the update went.
relaunch_app() {
    if [ "$RELAUNCH" -eq 1 ] && [ -x "$INSTALL_DIR/$ASSET_PREFIX" ]; then
        echo "Relaunching $ASSET_PREFIX …"
        # Detach fully so the app outlives this script.
        (cd "$INSTALL_DIR" && nohup "./$ASSET_PREFIX" >/dev/null 2>&1 &)
    fi
}

main() {
    set -eu

    FORCE=0
    WAIT_PID=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --force) FORCE=1 ;;
            --dir) shift; INSTALL_DIR="${1:-}" ;;
            --wait-pid) shift; WAIT_PID="${1:-}" ;;
            --relaunch) RELAUNCH=1 ;;
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

    # From here on, any exit (success, up-to-date, or failure) relaunches the app
    # when --relaunch was requested, so the in-app flow never strands the user.
    TMP=""
    trap 'rm -rf "$TMP"; relaunch_app' EXIT

    # In-app flow: the app spawns us and then quits — wait for it to be gone so
    # we never race its shutdown (Linux allows in-place overwrite, but waiting
    # keeps the sequence clean and is required on macOS translocation setups).
    if [ -n "$WAIT_PID" ]; then
        echo "Waiting for the app (pid $WAIT_PID) to exit…"
        i=0
        while kill -0 "$WAIT_PID" 2>/dev/null; do
            i=$((i + 1))
            [ "$i" -ge 120 ] && die "Process $WAIT_PID is still running after 60s; aborting update."
            sleep 0.5
        done
        echo "App closed."
    fi

    need curl
    need unzip

    RID=$(detect_rid)
    echo "Platform: $RID"
    echo "Install dir: $INSTALL_DIR"

    if [ -n "${OSM_UPDATE_URL:-}" ]; then
        # Test override: install a specific zip without querying GitHub.
        LATEST="${OSM_UPDATE_TAG:-v0.0.0-test}"
        LATEST_NUM=$(printf '%s' "$LATEST" | sed 's/^v//')
        URL="$OSM_UPDATE_URL"
        echo "Override release: $LATEST ($URL)"
    else
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

        URL=$(printf '%s' "$JSON" | tr ',{}' '\n' \
            | grep 'browser_download_url' | grep -- "-$RID\.zip" | head -n1 \
            | sed -E 's/.*(https:[^"]+).*/\1/')
        [ -n "$URL" ] || die "No asset for $RID in release $LATEST (expected ${ASSET_PREFIX}-${LATEST_NUM}-${RID}.zip)."
    fi

    CURRENT=""
    if [ -f "$INSTALL_DIR/VERSION" ]; then
        CURRENT=$(tr -d ' \t\r\n' < "$INSTALL_DIR/VERSION")
        echo "Installed version: $CURRENT"
    fi

    if [ "$FORCE" -eq 0 ] && [ -n "$CURRENT" ] && [ "$CURRENT" = "$LATEST_NUM" ]; then
        echo "Already up to date ($CURRENT). Use --force to reinstall."
        exit 0
    fi

    TMP=$(mktemp -d 2>/dev/null || mktemp -d -t osmupd)

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
Usage: sh update.sh [--force] [--dir <install-dir>] [--wait-pid <pid>] [--relaunch]
  --force           reinstall even if already on the latest version
  --dir <path>      install dir to update (default: this script's folder)
  --wait-pid <pid>  wait for that process to exit before updating
  --relaunch        start the app again when the updater finishes
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
