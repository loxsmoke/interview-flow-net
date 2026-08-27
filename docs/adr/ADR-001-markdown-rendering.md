# ADR-001 — Markdown/Mermaid Rendering Approach

**Status:** Accepted — **Option B (fully native)**, decided by the user 2026-08-26. The mermaid sub-approach remains an open sub-spike (see Consequences).
**Drives:** [04-markdown-rendering.md](../04-markdown-rendering.md), M3 on the porting plan.

## Context

The port's hardest fidelity requirement: agent reports render markdown with `marked` (`breaks: true`, GFM tables, raw-HTML passthrough for the `search-warning` block), tag-emoji decoration, and **Mermaid diagrams** (a JS-only library), with exact typography (Inter, specific rem sizes/margins) and links opening the OS browser. No off-the-shelf Avalonia markdown control provides this combination. Must work on Windows **and macOS**.

## Options

### A. Embedded WebView for content panes (native shell, web content)

Keep the original JS pipeline verbatim (marked + DOMPurify + mermaid + the exact prose CSS) inside a WebView control used only for markdown panes and chat bubbles; all other UI is native Avalonia.

- Candidates: `WebViewControl-Avalonia` (OutSystems, CefGlue/Chromium — same engine both OSes), community `Avalonia.WebView` (WebView2 on Windows / WKWebView on macOS — native engines, no Chromium payload).
- **+** Pixel-exact by construction; mermaid, tables, breaks, raw HTML all free; lowest fidelity risk.
- **−** Heavier dependency (Chromium ~100+ MB, or two engine-specific codepaths); JS↔.NET bridge needed for link interception, scroll pinning, streamed-delta appends; chat bubbles containing markdown make per-bubble WebViews impractical → render whole chat transcript as one HTML document.

### B. Fully native: Markdig + custom Avalonia renderer

Markdig (GFM, soft-break-as-hard-break) → custom control building Avalonia `Inline`s/panels per the typography spec. Mermaid via an embedded JS engine (e.g. Jint/ClearScript running mermaid to SVG offscreen) + `Avalonia.Svg.Skia` display — or mermaid shown as code blocks (fidelity violation).

- **+** No web runtime; a real Avalonia app throughout; text selection/theming native.
- **−** Large custom-renderer effort (tables, nested lists, raw-HTML subset for search-warning); mermaid-in-JS-engine is speculative (mermaid needs DOM — would require a DOM shim; likely **not feasible**, pushing toward headless rendering or option C); highest fidelity risk.

### C. Hybrid: native markdown + WebView only for mermaid

Markdig-based native rendering; mermaid blocks rendered in small embedded WebViews (or pre-rendered to SVG by a hidden shared WebView).

- **+** Smaller web surface than A.
- **−** Two rendering systems to keep visually consistent; still ships a WebView; native table/breaks fidelity work remains.

## Decision

**Option B — fully native.** Markdig with GFM extensions and soft-line-breaks-as-hard-breaks (matching `marked` + `breaks: true`), rendered by a custom `MarkdownView` Avalonia control that implements the typography spec in [04-markdown-rendering.md](../04-markdown-rendering.md) directly (headings, paragraphs, lists, inline/fenced code, blockquotes, GFM tables, selectable text, link click → OS browser).

Chosen over A (WebView) by the user: no web runtime, a genuinely native app, native text selection and theming. The trade-off accepted: substantial custom-renderer effort and a harder path for Mermaid.

## Consequences

- **`MarkdownView` control** (`App/Controls/`) is the M3 critical-path deliverable. Pipeline: tag-emoji decoration (fence-aware, in Core so it's testable) → Markdig AST → Avalonia visual tree. Streaming: LiveTracePanel appends deltas by re-parsing the accumulated text with throttling (spike the perf; incremental block-level reuse if needed).
- **Raw-HTML handling**: full HTML passthrough is off the table natively. Support the *known* raw-HTML shape only — the `search-warning` `<div>` (three variants) is recognized and rendered as a native styled banner; any other raw HTML falls back to visible literal text. This is an accepted, documented narrowing of §4.1 step 3 (DOMPurify becomes unnecessary; the emoji-decoration and mermaid passes remain).
- **Tables**: Markdig pipe-table AST → Avalonia `Grid` with the §4.2 border/padding/header styling, wrapped in a horizontal `ScrollViewer`.
- **Mermaid sub-spike (ADR-001b, open — first findings recorded below)** — candidates in order of preference:
  1. **Embedded JS engine** (ClearScript V8 or Jint) running mermaid with a minimal DOM/SVG shim → SVG string → `Avalonia.Svg.Skia`. Risk: mermaid's DOM dependence; may require pinning an older, less DOM-hungry mermaid major version. Time-box the spike.
  2. **Native layout of the flowchart subset** actually produced by the agents (graph/flowchart + subgraphs) via a .NET graph-layout library (e.g. MSAGL), styled dark/Inter. Approximate visuals — acceptable only if 1 fails, since diagram *content* parity matters more than mermaid's exact geometry.
  3. Last resort: show the normalized mermaid source as a code block at 80 % opacity (the original's own failure mode) — ship-blocker for parity, only as a temporary state.
  All five input normalizations from §4.4 are implemented in Core regardless of the renderer chosen.
- **Fonts/metrics**: root 16 px, Inter via `Avalonia.Fonts.Inter`; rem values from §4.2 become fixed px in control styles.
- Golden-corpus screenshot comparison vs the original stays the acceptance gate (M3); "close-but-not-identical" is acceptable only where §4.2 metrics are still met (control chrome, scrollbars), never for typography.

## ADR-001b spike findings (2026-08-26, first pass)

Harness: `tests/InterviewFlow.Tests/Spikes/MermaidJintSpikeTests.cs` + `tools/get-mermaid.ps1` (fetches the mermaid **11.4.0** UMD bundle — the exact version the original pins).

Result of running mermaid 11.4.0 under **Jint 4.16.1** with a hand-rolled DOM shim (~150 lines):

1. ✅ The UMD bundle **evaluates** and the `mermaid` global appears (needs: `console`, timers, `addEventListener`, `matchMedia`, `getComputedStyle` stubs). Eval time ≈ 700 ms — cache the engine, don't re-evaluate per diagram.
2. ✅ `mermaid.initialize({theme:'dark'})` works.
3. ✅ `mermaid.render()` executes the **full pipeline**: flowchart parsing, d3 DOM construction, and bundled DOMPurify all run. Two shim gotchas found: mermaid selects via `[id="…"]` attribute selectors, and DOMPurify silently degrades to a stub without `addHook` unless `document.nodeType === 9`.
4. ⛔ Current wall: mermaid round-trips SVG **through `innerHTML` strings** (DOMPurify sanitize → re-parse), so the shim DOM must actually *parse* markup, not just fabricate elements. Faked `getBBox`/`getComputedTextLength` also mean label geometry would be wrong until measurement is wired to real text metrics.

**Assessment (superseded — see second pass below):** no fundamental blocker found — candidate 1 is feasible, but the remaining work is a real minimal DOM (markup parsing on `innerHTML`, element tree, attribute/style serialization) plus text-measurement callbacks bridged to Avalonia's font stack for `getBBox`.

### Second pass (same day): candidate 1 WORKS — SVG produced

A real minimal DOM was implemented (`src/InterviewFlow.Core/Rendering/MermaidDom.js`, ~550 lines: prototype-based Node/Element/Document — DOMPurify reflects over `Element.prototype`, so accessors must live on prototypes — plus a markup parser for `innerHTML`/`DOMParser`, serializer, simple selector engine, and measurement-driven `getBBox`). Hosted by `MermaidRenderer` (Core, Jint 4.16.1, engine cached across renders) with text measurement injected by the App via Avalonia `FormattedText` (`MermaidHost`).

Results (spike harness `MermaidJintSpikeTests`):

- ✅ `graph LR\nA-->B` → **valid 8 KB SVG**, sane viewBox.
- ✅ The golden-corpus diagram (graph TD→LR rewrite, backtick label, unclosed subgraph — all five normalizations) → 15 KB SVG.
- ✅ Invalid input → clean error ("No diagram type detected…"), engine **recovers** and renders the next diagram.
- ✅ Warm render ≈ 420 ms; engine reuse works (rebuilds defensively after a failure).
- ✅ `MarkdownView` displays the SVG via `Svg.Controls.Skia.Avalonia` (12.0.0.16), falling back to 80 %-opacity source on any failure. App renders the corpus live.
- Geometry gotchas fixed along the way: `getBBox` must exclude `<style>/<script>` text (the injected stylesheet blew the viewBox to 29,000 px), and container bboxes must union children offset by `translate(x,y)`/`x`/`y`.

### Third pass: two invisible-diagram bugs fixed

1. **HTML labels**: mermaid's browser default puts node labels in `<foreignObject>` HTML, which Svg.Skia cannot draw — diagrams rendered as empty shapes. Fixed with `htmlLabels: false` everywhere (native `<text>` labels).
2. **Namespace poisoning**: the sanitize pass stamps `xmlns="…xhtml"` on inner elements; a strict XML parser then treats the diagram body as unrendered foreign content. Fixed in `MermaidRenderer.PostProcessSvg` (strips stray xhtml xmlns; also forces `fill`/`font-family` on text per §4.4 step 9, so labels don't depend on CSS support).
3. **`securityLevel: 'loose'`** (original uses `'antiscript'`): DOMPurify's final sweep dismantled entire diagrams under the shim DOM whenever a label contained `<br/>` (observed: it removed the root `<svg>` from `<body>`). Antiscript defends against script execution — a surface that does not exist in the port, where SVG goes to a static rasterizer, never a browser.
4. **Nested tspans**: Svg.Skia draws flat tspans but silently skips nested ones — mermaid wraps each label word in an inner tspan inside an outer line-tspan, so labels rasterized to zero pixels. `PostProcessSvg` flattens the nesting (hoisting position attributes onto the first inner tspan).
5. **Paint order**: d3's `insert(name, ':first-child')` resolves its reference node via `querySelector(':first-child')`; the shim didn't support that pseudo-selector, so node rects were appended *after* labels and painted over the text. Fixed in the shim's selector engine.

6. **Zero-size layout nodes**: `getBBox` called directly on a shape leaf (`rect`/`circle`/`polygon`) returned 0×0 (the shim only read size attributes when unioning children) — mermaid's `updateNodeBounds` therefore fed dagre zero-width nodes and boxes overlapped. Shapes now self-report bboxes from their geometry attributes.
7. **Label text fidelity trio**: void elements serialized as `<br></br>` (stray `</br>` showed as literal label text); Svg.Skia drops loose space text-nodes between per-word tspans (words ran together) → each line-tspan's content is merged to one string; and CSS `text-anchor: middle` isn't applied by Svg.Skia (text started at box center and overflowed) → set as an explicit attribute, cluster titles excluded.
8. **Line-aware text measurement**: `<text>` bboxes count direct tspans as lines (max line width × line count) instead of measuring concatenated text; and the App measures with **SkiaSharp's font resolution** (`MermaidHost`), not Avalonia's `FormattedText`, so layout metrics match what Svg.Skia draws — the mismatch showed as text spilling over box edges.

9. **Cluster titles**: mermaid's own title placement never runs under the shim — it emits two `g.cluster` twins per subgraph (one holding the unpositioned title text, one holding the sized rect plus an empty label group), leaving the title at the diagram origin, and even when positioned it paints *beneath* the cluster fill. `PositionClusterTitles` matches the twins by id, reparents the title after the rect, and centers it inside the cluster's top edge.

Verified by rendering PNGs and inspecting them: the two-node flowchart and the golden-corpus diagram (multi-line label, subgraph with centered title, curved edges) lay out correctly with no overlaps.

Regression-guarded by `MermaidSvgOutputTests`: strict XML parse, all-SVG namespace, no foreignObject, no nested tspans, text fills present, and a **pixel-level check** (rasterize via the same Svg.Skia the app uses; output with `<text>` must draw more light pixels than without).

**Remaining refinements (tracked in TODO.md):** label geometry is approximate where mermaid uses layout-dependent measurements (fine for spike; compare visually against the original); `<foreignObject>` HTML labels rely on the Svg.Skia renderer's support — verify visual output; per-diagram render cost (~0.4 s) argues for off-UI-thread rendering with a placeholder; the mermaid bundle ships as a file beside the app (packaging task), dev builds pick up `spikes/assets/`.

**ADR-001b decision: candidate 1 (Jint + minimal DOM) is adopted.** Candidate 2 (MSAGL) stays as the recorded fallback only if visual QA reveals unfixable geometry problems.

## Superseded options (for the record)

A (WebView content panes) and C (hybrid) were rejected with the decision above; revisit only if the mermaid sub-spike exhausts candidates 1–2 **and** diagram fidelity proves non-negotiable in practice.
