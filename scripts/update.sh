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

# Fallback only. The app binary is discovered from the payload rather than assumed,
# so that renaming the application does not strand users: the updater shipped with the
# *old* release is the one that performs the renaming update, and if it relaunches a
# hardcoded name it restarts the version it just replaced — forever re-offering the
# same update.
LEGACY_APP_NAME="OptiscalerManager"

RELAUNCH=0
INSTALL_DIR=""
APP_BIN=""          # resolved once the payload is extracted
COPY_STATE="none"   # none | started | done — lets cleanup warn on an interrupted copy

# Echoes the name of the application executable inside a directory: the one file that
# is executable and is not one of the things we ship beside it.
discover_app_binary() {
    dir="$1"
    [ -d "$dir" ] || return 0
    for f in "$dir"/*; do
        [ -f "$f" ] || continue
        [ -x "$f" ] || continue
        name=$(basename "$f")
        case "$name" in
            update.sh|update.ps1|config.json|VERSION|*.json|*.so|*.dylib|*.dll) continue ;;
        esac
        echo "$name"
        return 0
    done
}

# The binary to start: whatever we just installed, else whatever is already there,
# else the name this app used to have.
app_to_launch() {
    if [ -n "$APP_BIN" ]; then echo "$APP_BIN"; return 0; fi
    found=$(discover_app_binary "$INSTALL_DIR")
    if [ -n "$found" ]; then echo "$found"; return 0; fi
    echo "$LEGACY_APP_NAME"
}

# Runs from the EXIT trap: remove the temp dir (if any) then bring the app back
# regardless of how the update went (so the in-app flow never strands the user).
cleanup() {
    [ -n "${TMP:-}" ] && rm -rf "$TMP"
    launch=$(app_to_launch)
    if [ "$RELAUNCH" -eq 1 ] && [ -n "$launch" ] && [ -x "$INSTALL_DIR/$launch" ]; then
        # If the file copy was interrupted, the binary may be half-written — we still
        # relaunch (better than leaving the user with nothing) but say so clearly.
        [ "$COPY_STATE" = "started" ] && \
            echo "WARNING: the update was interrupted while copying files; the install may be incomplete. Re-run the updater." >&2
        echo "Relaunching $launch …"
        # Detach fully so the app outlives this script.
        (cd "$INSTALL_DIR" && nohup "./$launch" >/dev/null 2>&1 &)
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
    trap 'cleanup' EXIT

    # In-app flow: the app spawns us and then quits — wait for it to be gone so
    # we never race its shutdown (Linux allows in-place overwrite, but waiting
    # keeps the sequence clean and is required on macOS translocation setups).
    if [ -n "$WAIT_PID" ]; then
        # Guard against a non-numeric value making `kill` error out (and, under
        # `set -e`, skip the wait or abort). Integer `sleep 1` keeps us portable to
        # shells whose sleep rejects fractional seconds (busybox/strict POSIX).
        case "$WAIT_PID" in
            ''|*[!0-9]*) die "Invalid --wait-pid value: $WAIT_PID" ;;
        esac
        echo "Waiting for the app (pid $WAIT_PID) to exit…"
        i=0
        while kill -0 "$WAIT_PID" 2>/dev/null; do
            i=$((i + 1))
            [ "$i" -ge 60 ] && die "Process $WAIT_PID is still running after 60s; aborting update."
            sleep 1
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
        [ -n "$URL" ] || die "No asset for $RID in release $LATEST (expected an asset ending in -${RID}.zip)."
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

    # Resolve both names before copying: if the application was renamed between these
    # two releases, the old executable has to be removed or it lingers next to the new
    # one and anything pointing at the old name keeps starting the previous version.
    OLD_BIN=$(discover_app_binary "$INSTALL_DIR")
    APP_BIN=$(discover_app_binary "$TMP/extract")
    [ -n "$APP_BIN" ] || APP_BIN="$OLD_BIN"

    echo "Installing to $INSTALL_DIR …"
    # Copy the new payload over the install dir. User data is elsewhere, so this
    # only replaces the program, its bundled config.json template and these scripts.
    COPY_STATE="started"
    cp -a "$TMP/extract/." "$INSTALL_DIR/" || die "Copy failed. Is the app closed and the folder writable?"
    COPY_STATE="done"

    if [ -n "$OLD_BIN" ] && [ -n "$APP_BIN" ] && [ "$OLD_BIN" != "$APP_BIN" ]; then
        echo "The application was renamed: $OLD_BIN -> $APP_BIN"
        rm -f "$INSTALL_DIR/$OLD_BIN" || true
    fi

    # Ensure the app and updater stay executable.
    for f in "$APP_BIN" update.sh; do
        [ -n "$f" ] && [ -f "$INSTALL_DIR/$f" ] && chmod +x "$INSTALL_DIR/$f" 2>/dev/null || true
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
