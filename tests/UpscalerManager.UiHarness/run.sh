#!/usr/bin/env bash
# Runs the UI harness against a throwaway HOME. Screenshots go to the folder given as the
# first argument (default: docs/screenshots of this repository).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
shots="${1:-$here/../../docs/screenshots}"
home="$(mktemp -d -t um-harness-XXXXXX)"
trap 'rm -rf "$home"' EXIT
export HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" XDG_CACHE_HOME="$home/.cache"
export UM_HARNESS=1
dotnet run --project "$here" -c Debug -- "$(realpath -m "$shots")"
