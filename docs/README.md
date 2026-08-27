# Interview Flow .NET — Design Documentation

Design specs for porting **Interview Flow** (Python/FastAPI + React SPA hosted in pywebview, at `c:\dev\interview-flow`) to a **.NET 10 Avalonia desktop application** running on **Windows and macOS**.

The architectural template is **openlogi-net** (`c:\dev\openlogi-net`) — a .NET 10 + Avalonia 12 desktop app whose conventions (project layout, MVVM style, persistence patterns) this port follows, adjusted where those conventions are Windows-only.

## Port ground rules

1. **Same data formats.** The port reads and writes the exact same files as the original (`interview-flow-data.json`, `custom-actions.json`, resume tag DSL, `section-headings.md`, `resume-template.docx`). A user must be able to point both apps at the same data folder.
2. **Same outputs on screen.** Markdown rendering of text styles, font sizes, and HTML link behavior must match the original. Minor chrome differences (button/textbox frames, native control colors) are acceptable.
3. **Cross-platform.** Runs on Windows and macOS. No Windows-only packages or APIs in the main code path; platform-specific behavior (shell-open, paths, packaging) goes behind small platform helpers.

## Document index

| Doc | Contents |
|---|---|
| [00-overview.md](00-overview.md) | What the app is, port scope, fidelity rules, non-goals |
| [01-architecture.md](01-architecture.md) | Target solution structure, MVVM conventions, cross-platform strategy |
| [02-data-formats.md](02-data-formats.md) | Every file format read/written, with schemas and samples |
| [03-ui-spec.md](03-ui-spec.md) | Screen-by-screen UI specification (shell, sidebar, all views) |
| [04-markdown-rendering.md](04-markdown-rendering.md) | **Fidelity-critical**: markdown/mermaid/link rendering spec |
| [05-llm-providers.md](05-llm-providers.md) | Provider abstraction, streaming event contract, temperatures, pricing |
| [06-resume-pipeline.md](06-resume-pipeline.md) | PDF/DOCX parsing, tagging heuristic, preview, .docx export |
| [07-queue-and-streaming.md](07-queue-and-streaming.md) | Background queue semantics and event pub/sub |
| [08-configuration.md](08-configuration.md) | Settings storage, secrets, `.env` migration, data-folder migration wizard |
| [09-porting-plan.md](09-porting-plan.md) | Milestones, parity checklist, risks |
| [adr/](adr/) | Architecture decision records (open and resolved) |

## Decisions (see `adr/`)

All three ADRs are **decided** (2026-08-26):

- **ADR-001** — fully **native** markdown rendering (Markdig + custom Avalonia control); no WebView. Mermaid approach is an open sub-spike (ADR-001b inside ADR-001).
- **ADR-002** — config stays **env-file compatible, as-is** (plaintext keys, rewrite-in-place codec); OS-protected secrets deferred as a fast-follow.
- **ADR-003** — **fully in-process** services; no HTTP layer; NDJSON vocabulary kept as the typed internal event contract.

## Source references

- Original app: `c:\dev\interview-flow` (Python 3.10+, FastAPI, React 18 UMD SPA in `app/static/index.html`, pywebview shell)
- Template app: `c:\dev\openlogi-net` (.NET 10, Avalonia 12.0.5, CommunityToolkit.Mvvm 8.4.1)
- Original's own design docs: `c:\dev\interview-flow\docs\hld\`, `docs\lld\`
