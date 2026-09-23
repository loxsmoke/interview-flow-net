#!/usr/bin/env bash
# Builds a signed Sparkle appcast per architecture; no Apple credentials needed.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
rid="${1:?Usage: package-update.sh osx-arm64|osx-x64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo "Unsupported runtime" >&2; exit 1 ;; esac
: "${VERSION:?VERSION is required}"
: "${SPARKLE_PRIVATE_ED_KEY:?Set the free Sparkle signing key in GitHub Actions secrets}"
sparkle="$(bash "$root/tools/macos/get-sparkle.sh")"
updates="$(mktemp -d)"
trap 'rm -rf "$updates"' EXIT
name="InterviewFlow-${VERSION}-${rid}.zip"
ditto -c -k --keepParent "$root/dist/Interview Flow.app" "$updates/$name"
# The private key goes over stdin, never in process arguments or output files.
printf '%s\n' "$SPARKLE_PRIVATE_ED_KEY" | "$sparkle/bin/generate_appcast" \
  --ed-key-file - --maximum-deltas 0 \
  --download-url-prefix "https://github.com/${GITHUB_REPOSITORY:-loxsmoke/interview-flow-net}/releases/download/v${VERSION}/" \
  -o "$updates/appcast-$rid.xml" "$updates"
# Refuse to publish unsigned enclosures (e.g. missing or mismatched keys).
dotnet run --project "$root/tools/InterviewFlow.MacPackaging" --configuration Release -- \
  verify "$updates/appcast-$rid.xml" "$updates/$name"
mkdir -p "$root/artifacts"
cp "$updates/$name" "$updates/appcast-$rid.xml" "$root/artifacts/"
