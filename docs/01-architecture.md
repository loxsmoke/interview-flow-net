# 01 — Target Architecture

## Shape of the port

The original is a local FastAPI server + React SPA hosted in a pywebview window. The port collapses this into a **single native Avalonia desktop app**: ViewModels call provider/agent services in-process; there is no HTTP server (see ADR-003). The original's NDJSON streaming event vocabulary is retained as the **internal** event contract between agents and the UI (see [07-queue-and-streaming.md](07-queue-and-streaming.md)) because the entire UI is built around it.

```
┌──────────────────────────────────────────────────┐
│ InterviewFlow.App (Avalonia, net10.0)            │
│   Views (axaml) ── ViewModels (CommunityToolkit) │
│        │                    │                    │
│        │ markdown pane      │ IAsyncEnumerable   │
│        ▼ (ADR-001)          ▼ <AgentEvent>       │
├──────────────────────────────────────────────────┤
│ InterviewFlow.Core (net10.0)                     │
│   Models · StateStore · CustomActions · Queue    │
│   ResumePipeline (parse/tag/export) · Prompts    │
│   Agents (research, stories, mock, …)            │
│   Providers (Anthropic/OpenAI/Gemini/Ollama)     │
│   Config · Paths · Logging                       │
└──────────────────────────────────────────────────┘
            │ HTTPS to LLM APIs only
```

## Solution layout

Follows openlogi-net conventions (`.slnx` solution, `src/` + `tests/`), simplified to what this app needs:

```
InterviewFlow.slnx
Directory.Build.props        LangVersion 14, Nullable, ImplicitUsings,
                             TreatWarningsAsErrors=true (App & Tests opt out)
src/
  InterviewFlow.App/         Avalonia UI — TFM net10.0 (NOT net10.0-windows)
    Program.cs  App.axaml(.cs)
    Assets/                  icon, fonts if bundled
    Controls/                reusable UserControls (MarkdownView, LiveTracePanel,
                             CostBadge, StepNavItem, SplitterPane, …)
    Views/                   one Window (MainWindow) + one UserControl per screen
    ViewModels/              *ViewModel.cs, partial-class split by feature
    Platform/                ShellOpen.cs, RevealInFileManager.cs (win/mac branches)
  InterviewFlow.Core/        net10.0 — domain, no Avalonia reference
    Models/                  InterviewState, Resume, Story, MockSession, …
    State/                   StateStore (atomic JSON), ResumeLibrary, CustomActionStore
    Agents/                  one class per section + MockInterviewSession, ResumeChatSession
    Providers/               IStreamingProvider + 4 implementations, pricing tables
    ResumePipeline/          PdfExtractor, DocxExtractor, ResumeTagger,
                             SectionHeadingMap, DocxExporter
    Queue/                   QueueManager (single-slot, ordered)
    Config/                  AppConfig (env-file compatible load/save), Paths
    Prompts/                 embedded prompt .md resources + PromptLoader
tests/
  InterviewFlow.Tests/       xunit, mirrors src/ in subfolders; fixture corpus copied
                             from original repo (parsed-resume.txt, test docx/pdf, sample JSON)
```

### Differences from openlogi-net (deliberate)

| openlogi-net | This port | Why |
|---|---|---|
| `net10.0-windows` App TFM | `net10.0` | macOS support |
| `Microsoft.Win32.SystemEvents`, Autostart, tray icon | omitted | Windows-only; not needed by interview-flow |
| Tomlyn TOML config | env-file-compatible config codec (ADR-002) | data-format compatibility with original `.env` |
| Window-per-view (`ShowDialog` everywhere) | one main window + view switching | original is an SPA with a persistent sidebar |
| Vestigial `ViewLocator` | dropped; explicit `DataTemplate` per screen VM | commit to view-composition since the app is single-window |
| Inline hex colors per window | central `Theme.axaml` resource dictionary | the app has a real design system (slate palette) that many views share |

## MVVM conventions (copied from openlogi-net)

- **CommunityToolkit.Mvvm 8.4.1**: `[ObservableProperty]` on private `_camelCase` fields, `[RelayCommand]` on private methods, `partial void On<Prop>Changed` for apply-on-change, `_loading` guard flag during initial fill.
- VMs are `sealed partial`, large ones split into dot-suffixed partial files (`MainViewModel.Queue.cs`, `MainViewModel.Navigation.cs`) with an index comment in the root file.
- **No DI container.** `App.axaml.cs` is the hand-rolled composition root; services take optional injectable seams as defaulted parameters (`HttpClient? http = null`) for tests.
- Compiled bindings on by default (`AvaloniaUseCompiledBindingsByDefault=true`); every XAML root declares `x:DataType`; `Design.DataContext` with safe parameterless design-time constructors.
- Collections: get-only `ObservableCollection<T> Xs { get; } = [];`
- VM→View signalling via plain C# events, subscribed in `DataContextChanged`, unsubscribed on unload.
- Code-behind is allowed for view-scoped concerns: dialogs, focus, clipboard, shell-open, scroll pinning.

## Single-window navigation

The original SPA has a fixed 240 px sidebar and swaps the main content area. Port as:

- `MainWindow` hosts `SidebarView` + a `ContentControl` bound to `MainViewModel.CurrentPage` (a screen VM).
- Screen VM → View resolution via typed `DataTemplate`s in `MainWindow.axaml` (or a real ViewLocator — decide at implementation; typed templates preferred, matching openlogi-net's item-template idiom).
- Window title bound: `"{Company} — {Position} | Interview Flow v{version}"`, falling back to `"Interview Flow v{version}"`.
- Window: 1400×900 default, 900×600 minimum (matches pywebview settings).

## Cross-platform strategy (Windows + macOS)

- **TFM**: all projects `net10.0`. CI builds and runs tests on `windows-latest` **and** `macos-latest`.
- **Fonts**: `.WithInterFont()` (`Avalonia.Fonts.Inter`) gives identical Inter rendering on both platforms — do not rely on system fonts.
- **Shell-open** (`Platform/ShellOpen.cs`): `Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })` on Windows; `Process.Start("open", url)` on macOS. Reveal-in-file-manager: `explorer.exe /select,"path"` vs `open -R path`.
- **File dialogs**: Avalonia `StorageProvider` (replaces pywebview's `open_folder_dialog`/`save_file_dialog` bridge).
- **Paths** (`Core/Config/Paths.cs`): default data dir resolution must handle both platforms; see [08-configuration.md](08-configuration.md). Never hard-code `\` separators; the JSON store contents are path-free so files stay portable across OSes.
- **Packaging**: Windows — self-contained publish + installer (Inno Setup, per openlogi-net) and portable zip; macOS — `.app` bundle (`dotnet publish -r osx-arm64/osx-x64` + bundle structure), **ad-hoc signed** (decided; no notarization).
- **Line endings**: state JSON is written with `\n` inside JSON strings by the serializer either way; the env-file codec must preserve the file's existing newline style when rewriting.

## Key NuGet packages (proposed)

| Concern | Package | Notes |
|---|---|---|
| UI | Avalonia 12.x, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | match openlogi-net versions |
| MVVM | CommunityToolkit.Mvvm 8.4.1 | |
| JSON | System.Text.Json (built-in), source-generated context | field order/format parity required — see 02 |
| Markdown | Markdig (GFM + soft-break-as-hard-break) + custom `MarkdownView` control | ADR-001 (decided: native, no WebView) |
| Mermaid | *ADR-001b sub-spike*: ClearScript/Jint + `Avalonia.Svg.Skia`, or MSAGL fallback | |
| PDF text | PdfPig (candidate) | must reproduce font-size heuristics |
| DOCX | *candidate:* Open XML SDK (`DocumentFormat.OpenXml`) | read + styled export |
| HTTP/LLM | raw `HttpClient` + hand-rolled SSE/stream parsing, or official SDKs (`Anthropic.SDK`? `OpenAI`?) | *TBD per provider — see 05* |
| Tests | xunit, coverlet | `[ModuleInitializer]` setup, no mocking framework (hand-written fakes) |

## Logging

Port openlogi-net's `DiagnosticLog` pattern: static no-throw facade, area-tagged lines, lazily created file under the app's local-data logs folder, suppressible for tests.
