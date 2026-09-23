#!/usr/bin/env bash
# Pinned upstream binary; also used for the free, one-time signing-key setup.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version=2.10.0
sha256=c2bf58aa8387266ac179357b1415d6f2635f044da8be41042af32425dae6da0c
dest="$root/artifacts/sparkle-$version"
archive="$root/artifacts/Sparkle-$version.tar.xz"
mkdir -p "$dest"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --retry 3 --output "$archive.tmp" \
    "https://github.com/sparkle-project/Sparkle/releases/download/$version/Sparkle-$version.tar.xz"
  mv "$archive.tmp" "$archive"
fi
echo "$sha256  $archive" | shasum -a 256 --check >&2
tar -xf "$archive" -C "$dest"
printf '%s\n' "$dest"
