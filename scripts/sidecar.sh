#!/usr/bin/env bash
# Fetches the Foreseerr runtimes the plugin bundles and lays them out in
# dist/sidecar/, the plugin's sidecar/ directory:
#
#   foreseerr-linux-x64, foreseerr-linux-arm64, foreseerr-windows-x64.exe
#   seerr-api.yml, committag.json, dist/public/   (one copy, shared by all)
#
#   scripts/sidecar.sh [linux-x64 linux-arm64 windows-x64]
#
# By default it downloads the foreseerr-plugin-<target> assets of the
# Foreseerr release in foreseerr.version and checks them against the
# release's SHA256SUMS (FORESEERR_RELEASE_URL overrides the download base).
# Instead:
#   FORESEERR_DIR=<checkout>     builds them from a local Foreseerr checkout
#   FORESEERR_REF=<tag|branch>   clones that ref into .foreseerr/ and builds it
#   FORESEERR_RUNTIME_DIR=<dir>  uses one plugin runtime built earlier, for the
#                                one target named (FORESEERR_VERSION labels it)
# Building uses Foreseerr's scripts/build-runtime.sh with Bun and its pinned
# Rust toolchain; targets other than the host's need cargo-zigbuild and zig.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/sidecar"
REPO=https://github.com/selmant/foreseerr
if (( $# == 0 )); then set -- linux-x64 linux-arm64 windows-x64; fi
for target in "$@"; do
  case "$target" in
    linux-x64|linux-arm64|windows-x64) ;;
    *) echo "Unsupported sidecar target: $target" >&2; exit 1 ;;
  esac
done
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/runtimes"

build_from_source() {
  local dir="$1" host target bun_root flags=() triples=()
  shift
  # Foreseerr pins the same Bun as this repository.
  if command -v mise >/dev/null 2>&1 && bun_root="$(mise -C "$ROOT" where bun 2>/dev/null)"; then
    PATH="$bun_root/bin:$PATH"
  fi
  if command -v rustup >/dev/null 2>&1; then
    (cd "$dir/backend-rs" && rustup toolchain install --no-self-update)
  fi
  host="$(cd "$dir/backend-rs" && rustc -vV | sed -n 's/^host: //p')"
  for target in "$@"; do
    case "$target" in
      linux-x64) triples+=(x86_64-unknown-linux-gnu) ;;
      linux-arm64) triples+=(aarch64-unknown-linux-gnu) ;;
      windows-x64) triples+=(x86_64-pc-windows-gnu) ;;
    esac
    if [[ "${triples[-1]}" != "$host" ]] && ! command -v cargo-zigbuild >/dev/null 2>&1; then
      echo "Building $target on $host needs cargo-zigbuild and zig; or use a Foreseerr release" >&2
      exit 1
    fi
  done
  # The frontend compares its build's commit with the one /status reports.
  export COMMIT_TAG="${COMMIT_TAG:-$(git -C "$dir" rev-parse HEAD 2>/dev/null || echo local)}"
  version="$(jq -r .version "$dir/package.json")"
  built_from="$dir at $COMMIT_TAG"
  echo "Building Foreseerr $version ($COMMIT_TAG) runtimes from $dir"
  (cd "$dir" && CI=true bun install --frozen-lockfile)
  for target in "$@"; do
    if [[ "${triples[0]}" != "$host" ]] && command -v rustup >/dev/null 2>&1; then
      (cd "$dir/backend-rs" && rustup target add "${triples[0]}")
    fi
    FORESEERR_BASE_PATH=/Foreseerr "$dir/scripts/build-runtime.sh" "$work/runtimes/$target" \
      --target "${triples[0]}" "${flags[@]}"
    # Later targets reuse the frontend the first one built.
    flags=(--skip-web)
    triples=("${triples[@]:1}")
  done
}

if [[ -n "${FORESEERR_RUNTIME_DIR:-}" ]]; then
  (( $# == 1 )) || { echo "FORESEERR_RUNTIME_DIR holds one runtime; name its one target" >&2; exit 1; }
  cp -R "$FORESEERR_RUNTIME_DIR/." "$work/runtimes/$1"
  version="${FORESEERR_VERSION:-local}"
  built_from="$FORESEERR_RUNTIME_DIR"
elif [[ -n "${FORESEERR_DIR:-}" ]]; then
  build_from_source "$(cd "$FORESEERR_DIR" && pwd)" "$@"
elif [[ -n "${FORESEERR_REF:-}" ]]; then
  dir="$ROOT/.foreseerr"
  if [[ "$(cat "$dir/.foreseerr-ref" 2>/dev/null || true)" != "$FORESEERR_REF" ]]; then
    rm -rf "$dir"
    git clone -q --depth 1 --branch "$FORESEERR_REF" "$REPO.git" "$dir"
    echo "$FORESEERR_REF" > "$dir/.foreseerr-ref"
  fi
  build_from_source "$dir" "$@"
else
  tag="$(tr -d '[:space:]' < "$ROOT/foreseerr.version")"
  url="${FORESEERR_RELEASE_URL:-$REPO/releases/download/$tag}"
  echo "Downloading Foreseerr $tag runtimes from $url"
  fetch() {
    curl -fsSL --retry 3 -o "$work/$1" "$url/$1" || {
      echo "Could not download $1 of Foreseerr $tag; FORESEERR_DIR or FORESEERR_REF build from source" >&2
      exit 1
    }
  }
  fetch SHA256SUMS
  for target in "$@"; do
    asset="foreseerr-plugin-$target.tar.gz"
    if [[ "$target" == windows-* ]]; then asset="foreseerr-plugin-$target.zip"; fi
    fetch "$asset"
    # Lines may read "<hash>  <name>" or, from Windows runners, "<hash> *<name>".
    tr -d '\r' < "$work/SHA256SUMS" | grep -E "^[0-9a-f]{64} [ *]$asset\$" > "$work/$asset.sha256" || {
      echo "SHA256SUMS of $tag does not list $asset" >&2
      exit 1
    }
    (cd "$work" && sha256sum --quiet -c "$asset.sha256")
    mkdir -p "$work/runtimes/$target"
    if [[ "$asset" == *.zip ]]; then
      unzip -q "$work/$asset" -d "$work/runtimes/$target"
    else
      tar -xzf "$work/$asset" -C "$work/runtimes/$target"
    fi
  done
  version="${tag#v}"
  built_from="$url"
fi

# One sidecar directory: a binary per target next to one copy of the files
# the binaries read from their working directory (FORESEERR_APP_ROOT).
rm -rf "$OUT" "$ROOT/dist/sidecar.json"
mkdir -p "$OUT"
first=""
for target in "$@"; do
  runtime="$work/runtimes/$target"
  exe=foreseerr
  if [[ "$target" == windows-* ]]; then exe=foreseerr.exe; fi
  for file in "$exe" seerr-api.yml committag.json dist/public/index.html; do
    test -s "$runtime/$file" || { echo "The $target runtime has no $file" >&2; exit 1; }
  done
  # The plugin serves Foreseerr under a sub-path, so its frontend must load
  # assets relative to <base href> (FORESEERR_BASE_PATH=/Foreseerr builds).
  if grep -Eq '(src|href)="/[^/]' "$runtime/dist/public/index.html"; then
    echo "The $target runtime's frontend is not the plugin build (FORESEERR_BASE_PATH=/Foreseerr)" >&2
    exit 1
  fi
  binary="foreseerr-$target"
  if [[ "$target" == windows-* ]]; then binary="$binary.exe"; fi
  cp "$runtime/$exe" "$OUT/$binary"
  if [[ "$target" != windows-* ]]; then chmod 755 "$OUT/$binary"; fi
  if [[ -z "$first" ]]; then
    first="$target"
    mkdir -p "$OUT/dist"
    cp "$runtime/seerr-api.yml" "$runtime/committag.json" "$OUT/"
    cp -R "$runtime/dist/public" "$OUT/dist/public"
    continue
  fi
  # The binaries share these files, so they must come from one Foreseerr build.
  for file in seerr-api.yml committag.json; do
    cmp -s "$OUT/$file" "$runtime/$file" || {
      echo "The $target runtime's $file differs from the $first runtime's" >&2
      exit 1
    }
  done
  # The frontend does not depend on the platform; any copy of the same commit works.
  if ! diff -rq "$OUT/dist/public" "$runtime/dist/public" >/dev/null; then
    echo "Warning: the $target frontend build differs from $first's; bundling $first's" >&2
  fi
done
jq -n --arg version "$version" --arg source "$built_from" '{version: $version, source: $source}' \
  > "$ROOT/dist/sidecar.json"
echo "Foreseerr $version sidecar for $* in $OUT"
