#!/usr/bin/env bash
# Compiles the Foreseerr sidecar binaries the plugin bundles.
#
#   scripts/sidecar.sh [linux-x64 linux-arm64 windows-x64]
#
# FORESEERR_DIR points at an existing Foreseerr checkout (for local work on
# both sides). Without it, the ref in foreseerr.version (or FORESEERR_REF) is
# cloned into .foreseerr/.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [[ -n "${FORESEERR_DIR:-}" ]]; then
  dir="$(cd "$FORESEERR_DIR" && pwd)"
else
  dir="$ROOT/.foreseerr"
  ref="${FORESEERR_REF:-$(tr -d '[:space:]' < "$ROOT/foreseerr.version")}"
  if [[ "$(cat "$dir/.foreseerr-ref" 2>/dev/null || true)" != "$ref" ]]; then
    rm -rf "$dir"
    git clone -q --depth 1 --branch "$ref" https://github.com/selmant/foreseerr.git "$dir"
    echo "$ref" > "$dir/.foreseerr-ref"
  fi
fi
if (( $# == 0 )); then set -- linux-x64 linux-arm64 windows-x64; fi
targets=()
for target in "$@"; do
  case "$target" in
    linux-x64|linux-arm64|windows-x64) targets+=("bun-$target") ;;
    *) echo "Unsupported sidecar target: $target" >&2; exit 1 ;;
  esac
done
cd "$dir"
echo "Compiling Foreseerr $(jq -r .version package.json) sidecars from $dir"
CI=true bun install --frozen-lockfile
bun run compile:plugin -- "${targets[@]}"
