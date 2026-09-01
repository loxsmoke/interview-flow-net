# 10 — Building & running

How to get the app building, running, and packaged on **Windows** and **macOS**.
Everything here is repo-relative; the design specs are `00`–`09`, this one is the
practical counterpart.

## Prerequisites

| | Windows | macOS |
|---|---|---|
| SDK | .NET 10 SDK (built against 10.0.400) | same |
| Extras | none | Xcode command line tools for `codesign` (`xcode-select --install`) — only needed to package |

There is no `global.json`, so any 10.0.x SDK is used. Check with `dotnet --version`.

No workloads, no Node, no Python: `Avalonia.Desktop` brings its own native
renderer, and the mermaid bundle is a plain file download (below).

## First run

```
# 1. settings — copy the template and add one API key
copy .env.example .env          # Windows
cp .env.example .env            # macOS

# 2. mermaid bundle for diagram rendering in dev builds (optional)
powershell -ExecutionPolicy Bypass -File tools\get-mermaid.ps1   # Windows
bash tools/get-mermaid.sh                                        # macOS

# 3. build + launch
run.cmd                         # Windows
bash run.sh                     # macOS
```

`run.cmd`/`run.sh` are one-liners over `dotnet run --project src/InterviewFlow.App`
that first `cd` to the repo root. That working directory matters: the app reads
`./.env` and `./data` before falling back to the per-user locations
([08-configuration.md](08-configuration.md) §8.2/§8.3), which is what lets this
checkout share a data folder with the original Python app.

Without `spikes/assets/mermaid.min.js` the app still runs — mermaid code blocks
render as plain code blocks instead of diagrams (`MermaidHost.LocateBundle`
probes `<app dir>/Assets/mermaid.min.js` first, then walks up for `spikes/assets/`).

## Building and testing

```
dotnet build InterviewFlow.slnx
dotnet test InterviewFlow.slnx
dotnet test --filter "FullyQualifiedName~ShellLogicTests"    # one class
```

Notes:

- `InterviewFlow.Core` builds with `TreatWarningsAsErrors=true`; the App and Tests
  projects opt out locally (XAML codegen and test readability). Keep Core clean.
- The UI tests are headless (`Avalonia.Headless.XUnit`) — no display needed, and
  they run the same on both platforms.
- Test parallelization is disabled assembly-wide (`TestSetup.cs`): several tests
  mutate process-wide state (`Environment.CurrentDirectory`, env vars).
- **Gotcha:** on Windows, building while the app is running fails with
  `MSB3027 … InterviewFlow.App.exe … locked by: "InterviewFlow.App"`. Close the app
  and rebuild — the compile itself succeeded, only the copy to `bin/` failed.

## Packaging

### App icon

Both icons are generated from `src/InterviewFlow.App/Assets/logo.png`:

```
python tools/make-icons.py               # needs Pillow (pip install pillow)
```

It writes `Assets/icon.ico` (Windows exe icon via `<ApplicationIcon>`, and the
`Window.Icon` in `MainWindow.axaml`) and `tools/macos/InterviewFlow.icns` (the
`.app` bundle icon). Both are committed — rerun the script only after the logo
changes. The white rounded plate keeps the dark line art legible on a dark
taskbar/Dock; sizes ≤ 64 px use a tighter crop so 16 px stays readable.

### macOS — `.app` bundle

```
bash tools/publish-macos.sh              # osx-arm64 (default)
bash tools/publish-macos.sh osx-x64      # Intel
```

Produces `dist/Interview Flow.app`: self-contained (no .NET runtime needed on the
target Mac), `Info.plist` from `tools/macos/Info.plist` with the version taken
from the csproj, `InterviewFlow.icns` in `Contents/Resources` (`CFBundleIconFile`),
`mermaid.min.js` copied into `Contents/MacOS/Assets`, and an
**ad-hoc signature** (`codesign -s -`). Ad-hoc signing is the decided approach —
no notarization ([09-porting-plan.md](09-porting-plan.md)) — so **on first launch
the user must right-click the app and choose Open** once.

The publish step cross-compiles from Windows too (`dotnet publish -r osx-arm64`
produces a Mach-O apphost and the mac natives); only the `codesign` step needs a
Mac, and the script warns instead of failing when it is absent.

A bundle launched from Finder inherits `/` as its working directory, so it uses
the per-user locations — `~/Library/Application Support/InterviewFlow/` for both
`.env` and `data`. `INTERVIEW_DATA_DIR` still overrides. To point a bundle at a
checkout's data folder, set that variable rather than relying on the cwd.

### Windows — not scripted yet

The plan ([01-architecture.md](01-architecture.md) §Cross-platform strategy) is a
self-contained publish plus an Inno Setup installer and a portable zip. Neither
the installer script nor a publish script exists yet; the raw publish is:

```
dotnet publish src/InterviewFlow.App -c Release -r win-x64 --self-contained true -o artifacts/publish-win-x64
```

Copy `spikes/assets/mermaid.min.js` to `artifacts/publish-win-x64/Assets/` for
diagram support, the same way the macOS script does.

## Cross-platform rules to keep

Enforced by convention, not by the compiler — see
[01-architecture.md](01-architecture.md) §Cross-platform strategy:

- All projects target `net10.0` — never `net10.0-windows`.
- No `\` in paths: `Path.Combine`, and forward slashes in csproj item paths.
- Platform branches live in `Core/Paths.cs` and `App/Platform/ShellOpen.cs`. Add
  new ones there, not inline.
- Fonts come from `.WithInterFont()`; monospace runs name a fallback chain
  (`Cascadia Mono,Consolas,Menlo,monospace`) so macOS resolves Menlo.
- macOS keyboard: F-keys are media keys by default, so the F12 dev harness needs
  `fn`+`F12` there.

## Not done yet

- **CI**: no workflow is committed. The intent is build + test on `windows-latest`
  and `macos-latest` (TODO §1).
- **Version stamping** and the Windows installer (TODO §11). The app icon is done
  (see [App icon](#app-icon)); the `.icns` has not been checked on a Mac yet.
- The macOS bundle: the publish step is verified (an `osx-arm64` cross-publish from
  Windows produces the Mach-O apphost and mac natives), but the script's assemble
  and `codesign` steps have **not been run on a Mac**, and the bundle has never
  been launched there (TODO §1).
