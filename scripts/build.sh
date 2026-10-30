#!/usr/bin/env bash
# Builds the plugin for one Jellyfin ABI and bundles the Foreseerr sidecar.
#
#   scripts/build.sh [10.11|12] [linux-x64 linux-arm64 windows-x64]
#
# Run scripts/sidecar.sh first; it leaves the runtimes in dist/sidecar/.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SIDECAR="$ROOT/dist/sidecar"
PLUGIN_ABI="${1:-10.11}"
if [[ "$PLUGIN_ABI" != "10.11" && "$PLUGIN_ABI" != "12" ]]; then
  echo 'usage: scripts/build.sh [10.11|12] [linux-x64 linux-arm64 windows-x64]' >&2
  exit 1
fi
if (( $# > 0 )); then shift; fi
if (( $# == 0 )); then set -- linux-x64 linux-arm64 windows-x64; fi
binaries=()
for target in "$@"; do
  case "$target" in
    linux-x64|linux-arm64) binary="foreseerr-$target" ;;
    windows-x64) binary="foreseerr-$target.exe" ;;
    *) echo "Unsupported sidecar target: $target" >&2; exit 1 ;;
  esac
  test -s "$SIDECAR/$binary" || { echo "Missing $binary; run scripts/sidecar.sh first" >&2; exit 1; }
  binaries+=("$binary")
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
mkdir -p "$PLUGIN_OUT/sidecar/dist"
dotnet_cmd publish "$PROJECT" -c Release -p:JellyfinTarget="$PLUGIN_ABI" -o "$PLUGIN_OUT"
# The binaries share one copy of the files they read from their directory.
for binary in "${binaries[@]}"; do
  cp -p "$SIDECAR/$binary" "$PLUGIN_OUT/sidecar/"
done
cp "$SIDECAR/seerr-api.yml" "$SIDECAR/committag.json" "$PLUGIN_OUT/sidecar/"
cp -R "$SIDECAR/dist/public" "$PLUGIN_OUT/sidecar/dist/public"
# Build.props owns the per-ABI plugin version and targetAbi.
PLUGIN_PROPERTIES="$(dotnet_cmd msbuild "$PROJECT" -p:JellyfinTarget="$PLUGIN_ABI" \
  -getProperty:AssemblyVersion -getProperty:TargetAbi -getProperty:ReleaseVersion)"
bun "$ROOT/scripts/pack.mjs" "$PLUGIN_ABI" "$PLUGIN_OUT" "$PLUGIN_PROPERTIES"
