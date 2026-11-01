#!/usr/bin/env bash
# Prepares the next plugin release around a Foreseerr release:
#
#   scripts/next-release.sh v<X.Y.Z>
#
# The plugin version follows Foreseerr's and plugin.channel (alpha, beta, rc
# or stable):
#
#   a new Foreseerr release        X.Y.Z-<channel>.1, or X.Y.Z when stable
#   the Foreseerr in use again     the next X.Y.Z-<channel>.N, or X.Y.Z.N when
#                                  stable: a plugin-only fix
#
# It points foreseerr.version at the tag, sets ReleaseVersion in Build.props
# and the README's "currently" lines, and writes release-notes/<version>.md
# unless it exists. Notes for a plugin-only fix start with a TODO line, which
# the release workflow refuses until it is replaced. Prints the version.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

tag="${1:-}"
[[ "$tag" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || {
  echo "Usage: scripts/next-release.sh v<X.Y.Z> (a stable Foreseerr release tag)" >&2
  exit 2
}
base="${tag#v}"
channel="$(tr -d '[:space:]' < plugin.channel)"
case "$channel" in
  alpha|beta|rc|stable) ;;
  *) echo "plugin.channel is '$channel'; use alpha, beta, rc or stable" >&2; exit 1 ;;
esac
current_tag="$(tr -d '[:space:]' < foreseerr.version)"
current="$(sed -n 's|.*<ReleaseVersion>\(.*\)</ReleaseVersion>.*|\1|p' Build.props)"
[[ -n "$current" ]] || { echo "Build.props has no ReleaseVersion" >&2; exit 1; }

if [[ "$tag" != "$current_tag" ]]; then
  newest="$(printf '%s\n%s\n' "${current_tag#v}" "$base" | sort -V | tail -1)"
  [[ "$newest" == "$base" ]] || {
    echo "foreseerr.version is $current_tag, newer than $tag" >&2
    exit 1
  }
  fix=0
  if [[ "$channel" == stable ]]; then version="$base"; else version="$base-$channel.1"; fi
else
  # The same Foreseerr again: a plugin-only release.
  fix=1
  if [[ "$channel" == stable ]]; then
    if [[ "$current" == "$base" ]]; then
      version="$base.1"
    elif [[ "$current" =~ ^${base//./\\.}\.([0-9]+)$ ]]; then
      version="$base.$((BASH_REMATCH[1] + 1))"
    else
      version="$base" # the first stable release of this Foreseerr
    fi
  elif [[ "$current" =~ ^${base//./\\.}-$channel\.([0-9]+)$ ]]; then
    version="$base-$channel.$((BASH_REMATCH[1] + 1))"
  else
    version="$base-$channel.1"
  fi
fi

# Jellyfin's revisions, as Build.props computes them (README "Versions").
case "$version" in
  *-alpha.*) rev=1000 ;; *-beta.*) rev=2000 ;; *-rc.*) rev=3000 ;; *) rev=9000 ;;
esac
n=0
if [[ "$version" =~ -[a-z]+\.([0-9]+)$ || "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.([0-9]+)$ ]]; then
  n="${BASH_REMATCH[1]}"
fi
(( n <= 99 )) || { echo "$version: N must stay below 100" >&2; exit 1; }
rev=$((rev + n * 10))

printf '%s\n' "$tag" > foreseerr.version
sed -i "s|<ReleaseVersion>.*</ReleaseVersion>|<ReleaseVersion>$version</ReleaseVersion>|" Build.props
sed -i -E "s|(\`ReleaseVersion\` in \`Build.props\` \(currently \`)[^\`]*|\1$version|; s|(in \`foreseerr.version\` \(currently \`)[^\`]*|\1$tag|" README.md

notes="release-notes/$version.md"
if [[ ! -e "$notes" ]]; then
  {
    if (( fix )); then
      echo "TODO: describe the plugin changes in this release, then delete this line."
      echo
    fi
    echo "**Foreseerr for Jellyfin $version**, bundling Foreseerr $tag."
    echo
    if [[ "$channel" != stable ]]; then
      cat <<'EOF'
> [!WARNING]
> **Alpha, in active development.** Anything can change between releases: settings, data, URLs, and behavior. There is **no backward compatibility** between plugin versions, and no upgrade path is promised. If an update misbehaves, uninstall the plugin, delete `plugins/configurations/Foreseerr` in Jellyfin's data folder, and install again. Do not rely on it for data you cannot recreate.

EOF
    fi
    echo "## Changes"
    echo
    if (( fix )); then
      echo "- Still bundles Foreseerr $tag."
    else
      echo "- Bundles Foreseerr $tag: the runtimes it publishes for the plugin, checked against the release's \`SHA256SUMS\`. See its release notes below for what changed."
    fi
    echo "- Ships builds for Jellyfin 10.11 and 12. The plugin's numeric versions are \`$base.$rev\` and \`$base.$((rev + 1))\`, respectively."
    echo
    echo "## Upgrade notes"
    echo
    echo "- The Linux server needs glibc, so Alpine-based Jellyfin images cannot run it, and NixOS needs \`programs.nix-ld.enable = true;\`."
    echo "- Back up Jellyfin's plugin configuration before updating."
    echo
    echo "Foreseerr release notes: https://github.com/selmant/foreseerr/blob/$tag/docs/release-notes-$base.md"
    echo
    echo "Guide: https://github.com/selmant/jellyfin-plugin-foreseerr#readme · Report plugin problems: https://github.com/selmant/jellyfin-plugin-foreseerr/issues"
  } > "$notes"
fi
echo "$version"
