#!/usr/bin/env bash
# Fetches the mermaid UMD bundle for the ADR-001b Jint spike (not committed).
# macOS/Linux twin of get-mermaid.ps1; pins mermaid 11.4.0 to match the
# original app (app/static/index.html).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dest="$root/spikes/assets"
url="https://cdn.jsdelivr.net/npm/mermaid@11.4.0/dist/mermaid.min.js"
mkdir -p "$dest"
echo "Downloading $url"
curl -fsSL "$url" -o "$dest/mermaid.min.js"
echo "Saved to $dest/mermaid.min.js"
