# 04 — Markdown Rendering (fidelity-critical)

This is the **highest-fidelity requirement** of the port: text styles, font sizes, and HTML link behavior must match the original. Rendering approach (**ADR-001, decided**): fully **native** — Markdig + a custom `MarkdownView` Avalonia control; no WebView. This doc specifies *what* must be reproduced; the ADR records the approach-specific consequences (native `search-warning` banner, table rendering, mermaid sub-spike).

Source: `Markdown` component at `index.html:210-293`, prose CSS at `index.html:21-37`.

## 4.1 Rendering pipeline (original)

1. **Tag-emoji decoration** (`addTagEmojis`) — before markdown parsing, split the text on ` ```mermaid … ``` ` fences, leave fenced content untouched, and in the remaining text do literal string replacements:

   | Literal | Becomes |
   |---|---|
   | `[VERIFIED]` | `✅ [VERIFIED]` |
   | `[REPORTED]` | `✅ [REPORTED]` |
   | `[LIKELY]` | `🟡 [LIKELY]` |
   | `[SPECULATIVE]` | `❓ [SPECULATIVE]` |
   | `[HIGH]` | `🟢 [HIGH]` |
   | `[MEDIUM]` | `🟡 [MEDIUM]` |
   | `[LOW]` | `🔴 [LOW]` |

2. **`marked.parse(text, { breaks: true })`** — GitHub-flavored markdown, and **`breaks: true`: every single newline becomes a `<br>`**. GFM tables are used heavily by the agent reports. Raw HTML passes through (needed for the server-injected `search-warning` block, §4.5). Port (Markdig): GFM pipeline + `SoftlineBreakAsHardlineBreak`.
3. **`DOMPurify.sanitize(html, { ADD_ATTR: ['class'] })`** — sanitization keeping `class`. Port note (ADR-001): with native rendering there is no HTML execution surface, so DOMPurify has no equivalent; raw HTML is handled per §4.5 (recognized `search-warning` shape → native banner; anything else → visible literal text).
4. Result inserted into a container with class `prose text-slate-300`, then the **mermaid pass** (§4.4) runs over it.

## 4.2 Typography spec (exact)

Root font size **16 px**, font **Inter**, prose body color `#cbd5e1` (slate-300) — **headings inherit body color; they are not white.**

```css
.prose h1,h2,h3,h4 { font-weight:600; margin-top:1em; margin-bottom:0.5em; }
.prose h1 { font-size:1.5rem }    /* 24px */
.prose h2 { font-size:1.25rem }   /* 20px */
.prose h3 { font-size:1.1rem }    /* 17.6px */
.prose p  { margin-bottom:0.75em; line-height:1.7 }
.prose ul,.prose ol { margin-left:1.5em; margin-bottom:0.75em }
.prose li { margin-bottom:0.25em }
.prose strong { font-weight:600 }
.prose code { background:#1e293b; color:#e2e8f0; padding:0.15em 0.4em;
              border-radius:4px; font-size:0.9em }
.prose pre  { background:#0f172a; padding:1em; border-radius:8px;
              overflow-x:auto; margin:1em 0 }
.prose pre code { background:none; padding:0 }
.prose blockquote { border-left:3px solid #6366f1; padding-left:1em;
                    margin:1em 0; color:#94a3b8 }
.prose table { width:100%; border-collapse:collapse; margin:1em 0 }
.prose th,.prose td { border:1px solid #334155; padding:0.5em 0.75em; text-align:left }
.prose th { background:#1e293b; font-weight:600 }
.prose { user-select:text }   /* text must be selectable/copyable */
```

Links have **no explicit prose styling** — and because the original loads Tailwind's CDN build, its preflight reset applies: `a { color: inherit; text-decoration: inherit; }`. Prose links therefore look like body text (no color change, no underline) and are only discoverable by the pointer cursor. *Verified against the original's source (Tailwind Play CDN injects preflight); the port renders links identically — final visual confirmation at side-by-side QA.* (Explicitly styled anchors exist only in About and Configuration, see 03.)

**Selection (port note):** text is selectable per block (`SelectableTextBlock`); selection cannot span across blocks, and inline-code chip text sits outside the surrounding block's selection — both accepted deviations for now (the browser original allows arbitrary selection). Revisit in M3 if it matters in practice; a per-pane "Copy" affordance is the likely mitigation.

## 4.3 Hyperlink behavior (exact)

From `index.html:220-232`, event-delegated on the container:

- Find the closest enclosing `a[href]` of the click target.
- Parse the href as a URL; unparseable → do nothing (allow default, which is inert in-app).
- Only `http:` and `https:` schemes are acted on; **all other schemes are ignored entirely**.
- For http(s): prevent navigation and open the URL in the **OS default browser**.

Avalonia mapping: `Process.Start(ProcessStartInfo { UseShellExecute = true })` on Windows / `open <url>` on macOS via `Platform/ShellOpen`. Nothing ever navigates the in-app pane.

## 4.4 Mermaid diagrams

Post-render pass over every `code.language-mermaid` block. Input normalizations — **each fixes a real LLM-output bug; port all of them**:

1. HTML-entity-decode the code (original uses a throwaway `<textarea>`).
2. Strip backtick markdown-string labels: regex `/\["?`([\s\S]*?)`"?\]/g` → replace with de-bulleted lines joined by `\n`.
3. Replace first `graph TD` → `graph LR` (multiline); `flowchart TD` is **deliberately left alone**.
4. Literal `\n` sequences and any `<br…>` variant → `<br/>`.
5. Balance unclosed `subgraph` blocks by appending `\n    end` per missing `end`.
6. Render with mermaid config `{ theme: 'dark', securityLevel: 'antiscript', fontFamily: 'Inter, sans-serif', suppressErrorRendering: true }`.
7. Success → replace the code block with a horizontally-scrollable diagram container (`my-4 overflow-x-auto`) containing the SVG. The original inserts SVG via `innerHTML`, **not** an XML parser (XML parsing breaks on HTML inside `<foreignObject>`).
8. Failure → leave the code block visible at 80 % opacity.
9. Diagram text CSS override: `svg text, svg tspan { fill/color: #e2e8f0 !important; font-family: 'Inter' }`.

**Port implication (ADR-001, native):** Mermaid is a JS library with no native .NET renderer. The mermaid sub-spike (ADR-001b) evaluates: embedded JS engine (ClearScript V8/Jint + DOM shim) → SVG → `Avalonia.Svg.Skia`, falling back to native layout of the flowchart subset via a .NET graph-layout library. The five normalizations above live in Core regardless of renderer.

The original ships a standalone harness `app/static/mermaid-debug.html` — keep an equivalent debug window/page for testing diagram normalization. *TBD.*

## 4.5 `search-warning` raw-HTML block

When web search fails, the original backend **prepends raw HTML to the markdown report** (three variants: `connection_error`, `no_results`, `not_searched`):

```html
<div class="search-warning">⚠️ <strong>…title…</strong> …body…</div>
```

Style: `background:#431407; border:1px solid #9a3412; border-radius:8px; padding:0.75em 1em; color:#fb923c; margin-bottom:1.25em; font-size:0.95em; line-height:1.6`.

Port (ADR-001, native): the renderer **recognizes this specific HTML shape** at the top of the markdown (existing stored reports contain it inline) and renders it as a native banner with the metrics above; for newly generated reports the port stores the same HTML block for data compatibility. Unrecognized raw HTML elsewhere falls back to visible literal text (accepted narrowing — the original never emits other raw HTML).

## 4.6 Tagged-resume preview (separate renderer, not markdown)

`taggedToHtml` (`index.html:527-556`) renders the resume tag DSL with inline styles — reproduce these metrics:

| Tag | Rendering |
|---|---|
| `[Section Heading]` | bold 700, 1 em, bottom border 1 px `#94a3b8`, padding-bottom 1 px, margin `12px 0 4px` |
| `[Summary]` | paragraph, margin `0 0 4px` |
| `[Job title]` | bold 700, margin `8px 0 1px` |
| `[Job summary]` | paragraph, margin `0 0 2px` |
| `[Job bullet]` | flex row, gap 6 px, margin `0 0 1px`, padding-left 12 px, literal `•` bullet |
| `[Skill]` | bolds up to and including the first `:` |
| `[Additional info]` | paragraph, margin `0 0 1px`, centered |
| untagged/unknown | paragraph, margin `0 0 2px` |

Optional contact header (when "Show contact info" checked): name `1.4em` bold 700 line-height 1.2; a 1 px `#94a3b8` rule; contact line `0.78em` bold 700 `#cbd5e1`.

## 4.7 Where markdown is rendered

Every agent report pane (all agent screens + custom actions), chat bubbles (Mock Interview, Resume Coach), and the Resume Tailor analysis pane. All use the identical pipeline above.

## 4.8 Acceptance tests

- Golden-file corpus: take stored `raw_report` values from a real data file (headings, GFM tables, nested lists, code fences, mermaid, `search-warning` HTML, tag emojis, single-newline paragraphs) and screenshot-compare port vs original at identical window sizes. *Scaffold: build the corpus during implementation.*
- Unit tests for: emoji decoration (fence-safe), mermaid normalization steps 1–5, link-scheme filtering, tagged-preview HTML output.
