#!/usr/bin/env bash
# Builds the macOS .app bundle (docs/01-architecture.md §Packaging, TODO §M9).
#
#   bash tools/publish-macos.sh [osx-arm64|osx-x64]
#
# Produces dist/Interview Flow.app, self-contained (no .NET runtime needed on
# the target Mac) and AD-HOC signed — no notarization. macOS may require
# first-launch approval in System Settings > Privacy & Security.
set -euo pipefail

RID="${1:-osx-arm64}"
case "$RID" in osx-arm64) arch=arm64 ;; osx-x64) arch=x86_64 ;; *) echo "Unsupported runtime: $RID" >&2; exit 1 ;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/src/InterviewFlow.App/InterviewFlow.App.csproj"
stage="$root/artifacts/publish-$RID"
app="$root/dist/Interview Flow.app"

if [[ "$(uname -s)" != "Darwin" ]]; then
  # dotnet cross-publishes fine, but codesign only exists on macOS.
  echo "warning: not running on macOS — the bundle will not be signed." >&2
fi

version="$(grep -o '<Version>[^<]*</Version>' "$proj" | head -n1 | sed -e 's/<[^>]*>//g')"
version="${version:-0.0.0}"

# Trimmed by default (mode + rationale live in the csproj); TRIM=0 opts out.
trim="${TRIM:-1}"
trim_args=()
[[ "$trim" == "1" ]] && trim_args+=("-p:PublishTrimmed=true")

echo "==> publishing $RID (self-contained, trimmed=$trim, v$version)"
rm -rf "$stage" "$app"
dotnet publish "$proj" -c Release -r "$RID" --self-contained true "${trim_args[@]}" -o "$stage"

echo "==> assembling bundle"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$stage"/. "$app/Contents/MacOS/"
sed "s/__VERSION__/$version/g" "$root/tools/macos/Info.plist" > "$app/Contents/Info.plist"
# CFBundleIconFile points at this name; regenerate with tools/make-icons.py.
cp "$root/tools/macos/InterviewFlow.icns" "$app/Contents/Resources/InterviewFlow.icns"
chmod +x "$app/Contents/MacOS/InterviewFlow.App"

# MermaidHost probes <app dir>/Assets/mermaid.min.js first; the repo-root walk it
# falls back to cannot reach spikes/ from inside a bundle, so ship the bundle.
if [[ -f "$root/spikes/assets/mermaid.min.js" ]]; then
  mkdir -p "$app/Contents/MacOS/Assets"
  cp "$root/spikes/assets/mermaid.min.js" "$app/Contents/MacOS/Assets/mermaid.min.js"
else
  echo "note: spikes/assets/mermaid.min.js missing — run tools/get-mermaid.sh first" >&2
  echo "      (mermaid diagrams fall back to a code block without it)" >&2
fi

if [[ -n "${SPARKLE_PUBLIC_ED_KEY:-}" ]]; then
  [[ "$(uname -s)" == "Darwin" ]] || { echo "Sparkle packaging requires macOS" >&2; exit 1; }
  sparkle="$(bash "$root/tools/macos/get-sparkle.sh")"
  mkdir -p "$app/Contents/Frameworks"
  ditto "$sparkle/Sparkle.framework" "$app/Contents/Frameworks/Sparkle.framework"
  cp "$sparkle/LICENSE" "$app/Contents/Resources/Sparkle-LICENSE.txt"
  # The bridge is loaded from Contents/MacOS; Sparkle and all its helpers stay
  # inside the macOS bundle and never enter a Windows publish directory.
  clang -dynamiclib -fobjc-arc -arch "$arch" -mmacosx-version-min=12.0 \
    -framework Cocoa -framework Sparkle -F "$app/Contents/Frameworks" \
    -Wl,-rpath,@loader_path/../Frameworks \
    "$root/tools/macos/Updater.m" -o "$app/Contents/MacOS/libInterviewFlow.Updater.dylib"
  dotnet run --project "$root/tools/InterviewFlow.MacPackaging" --configuration Release -- \
    configure "$app/Contents/Info.plist" "$RID"
  # Both the bridge and framework must support the selected runtime even when
  # Intel packages are cross-built on an Apple Silicon runner.
  lipo "$app/Contents/MacOS/libInterviewFlow.Updater.dylib" -verify_arch "$arch"
  lipo "$app/Contents/Frameworks/Sparkle.framework/Sparkle" -verify_arch "$arch"
fi

if command -v codesign >/dev/null 2>&1; then
  echo "==> ad-hoc signing"
  codesign --force --deep --sign - "$app"
  codesign --verify --deep --strict --verbose "$app"
fi

echo
echo "Built: $app"
echo "First launch may require System Settings > Privacy & Security approval (not notarized)."
