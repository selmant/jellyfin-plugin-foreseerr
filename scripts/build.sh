#!/usr/bin/env bash
# Builds the plugin for one Jellyfin ABI and bundles the compiled sidecars.
#
#   scripts/build.sh [10.11|12] [linux-x64 linux-arm64 windows-x64]
#
# Run scripts/sidecar.sh first; FORESEERR_DIR must match between the two.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FORESEERR_DIR="$(cd "${FORESEERR_DIR:-$ROOT/.foreseerr}" && pwd)"
export FORESEERR_DIR
PLUGIN_ABI="${1:-10.11}"
if [[ "$PLUGIN_ABI" != "10.11" && "$PLUGIN_ABI" != "12" ]]; then
  echo 'usage: scripts/build.sh [10.11|12] [linux-x64 linux-arm64 windows-x64]' >&2
  exit 1
fi
if (( $# > 0 )); then shift; fi
if (( $# == 0 )); then set -- linux-x64 linux-arm64 windows-x64; fi
for target in "$@"; do
  case "$target" in
    linux-x64|linux-arm64) binary="foreseerr-$target" ;;
    windows-x64) binary="foreseerr-$target.exe" ;;
    *) echo "Unsupported sidecar target: $target" >&2; exit 1 ;;
  esac
  test -s "$FORESEERR_DIR/dist/bin/$binary" || { echo "Missing $binary; run scripts/sidecar.sh first" >&2; exit 1; }
done
dotnet_cmd() {
  if command -v mise >/dev/null 2>&1; then
    local root
    root="$(cd "$ROOT" && mise where dotnet)"
    DOTNET_ROOT="$root" "$root/dotnet" "$@"
  else
    dotnet "$@"
  fi
}
PROJECT="$ROOT/Foreseerr.Jellyfin/Foreseerr.Jellyfin.csproj"
PLUGIN_OUT="$ROOT/dist/jellyfin-$PLUGIN_ABI/Foreseerr"
rm -rf "$PLUGIN_OUT"
mkdir -p "$PLUGIN_OUT/sidecar"
dotnet_cmd publish "$PROJECT" -c Release -p:JellyfinTarget="$PLUGIN_ABI" -o "$PLUGIN_OUT"
for target in "$@"; do
  binary="foreseerr-$target"
  if [[ "$target" == "windows-x64" ]]; then binary="$binary.exe"; fi
  cp "$FORESEERR_DIR/dist/bin/$binary" "$PLUGIN_OUT/sidecar/"
done
# Build.props owns the per-ABI plugin version and targetAbi.
PLUGIN_PROPERTIES="$(dotnet_cmd msbuild "$PROJECT" -p:JellyfinTarget="$PLUGIN_ABI" \
  -getProperty:AssemblyVersion -getProperty:TargetAbi -getProperty:ReleaseVersion)"
bun "$ROOT/scripts/pack.mjs" "$PLUGIN_ABI" "$PLUGIN_OUT" "$PLUGIN_PROPERTIES"
