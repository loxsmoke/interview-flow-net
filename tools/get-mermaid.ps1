# Fetches the mermaid UMD bundle for the ADR-001b Jint spike (not committed — see .gitignore).
# The version pins to the original app's mermaid 11.4.0 (app/static/index.html).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'spikes\assets'
New-Item -ItemType Directory -Force $dest | Out-Null
$url = 'https://cdn.jsdelivr.net/npm/mermaid@11.4.0/dist/mermaid.min.js'
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile (Join-Path $dest 'mermaid.min.js')
Write-Host "Saved to $dest\mermaid.min.js"
