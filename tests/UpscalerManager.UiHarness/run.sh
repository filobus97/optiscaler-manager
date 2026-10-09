#!/usr/bin/env bash
# Runs the UI harness against a throwaway HOME. Screenshots go to the folder given as the
# first argument (default: docs/screenshots of this repository).
set -euo pipefail
export DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
here="$(cd "$(dirname "$0")" && pwd)"
shots="${1:-$here/../../docs/screenshots}"
dotnet build "$here" -c Debug -v q --nologo >/dev/null

# Each pass gets its own throwaway HOME. The second is an RX 9000, where the faster FSR 4
# builds must be refused.
run_pass() {
  local home
  home="$(mktemp -d -t um-harness-XXXXXX)"
  HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" \
    XDG_CACHE_HOME="$home/.cache" UM_HARNESS=1 UM_HARNESS_GPU="$1" \
    dotnet run --project "$here" -c Debug --no-build -- "$(realpath -m "$shots")"
  local status=$?
  rm -rf "$home"
  return $status
}

run_pass rdna3
run_pass rdna4
