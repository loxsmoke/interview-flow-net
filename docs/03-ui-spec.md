# 03 — UI Specification

Source of truth: `c:\dev\interview-flow\app\static\index.html` (React SPA, ~4,560 lines). This doc specifies each screen for the Avalonia port. Colors are Tailwind slate/indigo values written as hex so they can go straight into `Theme.axaml`.

## 3.0 Global shell & theme

- Layout: fixed **240 px sidebar** + main content filling the rest (content padding ≈ 32 px top/left/right).
- Window: 1400×900 default, min 900×600. Title: `"Interview Flow — v{version} — {Company}"` when a workflow is selected, else `"Interview Flow — v{version}"` — the original is a browser page titled just `"Interview Flow"`.
- Dark theme only:
  - Page background `#020617` (slate-950), text `#f1f5f9` (slate-100)
  - Panels `#0f172a` (slate-900) with `#1e293b` (slate-800) borders, `rounded-xl` (12 px radius)
  - Primary buttons: linear gradient indigo-600 `#4f46e5` → purple-600 `#9333ea`, 12 px radius
  - Prose body text `#cbd5e1` (slate-300)
- Font: **Inter** (via `Avalonia.Fonts.Inter`), root size 16 px, weights 300–700.
- Scrollbars: 8 px, thumb `#334155`, track `#0f172a` (style ScrollBar template; minor deviation acceptable).
- Animations to reproduce (acceptable to approximate): `.fade-in` (0.3 s, translateY 8→0), `.typing-dot` pulse (1.4 s, staggered 0.2/0.4 s), `.bra-scroll` book animation (6 s, translateX 0→−480 px), `.cog-pulse` amber pulse (1.8 s, `#f59e0b`↔`#fbbf24`, scale 1→1.25).

## 3.1 Sidebar

Top → bottom:

1. **Header row**: ⓘ About button · gradient wordmark "Interview Flow" (indigo→purple text gradient) · ⚙ Config button (pulses amber when no provider is configured). Subtitle "AI interview prep". When a company is selected: company name + "Currently selected company".
2. **Step list** — 12 rows. Each: 32×32 rounded icon tile + label + one-line description.
   - Tile states: idle `#1e293b`; active gradient `135deg #6366f1→#8b5cf6`; done `✓` in `#22c55e`; running → the glyph is replaced by a spinning arc (`Path.spinner`, one turn/second, the original's `animate-spin`); failed `bg red-900/80` showing `!`. The tile's tooltip names the state (Running/Queued/Failed) like the original's title/aria-label.
   - Queued: 12 px amber `⌛` badge, top-right of tile.
   - Active row: background slate-800 + 2 px right border `#6366f1`.
   - All non-`setup` rows locked at 40 % opacity until a workflow exists.
   - Inline badges after label: 🌐 globe `#38bdf8` 11 px (webSearch steps), 📄 doc `#facc15` 11 px (needsResume steps), `(tech)` in sky-400 on Interview Intel when position matches technical keywords.
3. **Custom actions** divider; one ⚡ row per action; dashed-border `+ Add new` row.
4. **Footer** (when a workflow is loaded): `Progress: N/12 steps` + 6 px indigo→purple progress bar.

Avalonia notes: `ItemsControl` of `StepNavItem` control; badges as adorner-style overlays in the item template.

## 3.2 Setup screen

- Header: "Setup" + provider label chip (e.g. green `Anthropic - claude-sonnet-4-6`) + subtitle; right-aligned **Save & Continue →**.
- **New application** button.
- Collapsible "Previous applications (N)" (Expander): each row — Select · `Company — Position` + "N steps done" + updated date · Clone · Delete. Clone and Delete get confirm dialogs; Clone names the copy `Company | copy N`.
- Two-column inputs: Company Name, Position — with hint that text after `|` is a stripped comment.
- Job Posting label with a **⤓ Fetch from URL** button to its right, above a full-height textarea. The button is enabled only when the textarea holds a bare `http(s)://` URL, shows "Fetching…" while it runs, and **stays on Setup** — it neither saves nor navigates, so the user can check the text and the filled-in names first. **Save & Continue** resolves a still-unfetched URL the same way before moving on, and stays put if that fails.
- Fetching runs the four-step resolution in 05 §5.7 (Workday CXS JSON · plain HTTP behind the SSRF guard · JSON-LD/OpenGraph in that HTML · one LLM extraction). Progress and fallback cost show inline; on failure, the "paste the text instead" message and the URL is left untouched.
- When the source names the role and employer (Workday CXS, schema.org JSON-LD, `og:site_name`/`og:title`), **Company** and **Position** are filled from it — but only when empty. A value the user typed is never overwritten; the status line says which fields were filled and which were left as typed.

## 3.3 Resume screen

- Header + **Save & Continue →**.
- Collapsible "Saved resumes (N)": Select · description + created date · Delete.
- Drag-and-drop upload zone ("PDF, DOCX, TXT, MD — max 10 MB").
- Editor card with three tabs:
  - **Edit** — monospace textarea of the tagged text.
  - **Raw format** — read-only monospace view of the diagnostic dump (see 06).
  - **Preview** — rendered tagged HTML (see 04 §tagged preview) + "Show contact info" checkbox.
- Footer: "Insert tag…" dropdown (lists all 7 tags with hints, e.g. `[Job title] — Role | Company | Location | Dates`), green **Save to library** (opens description dialog; library dedupes by description).

## 3.4 Generic agent screen (Research, Job Decoder, Pitch, Concerns, Salary, Interview Intel)

- Header: title (+ 🌐 badge on web-search steps) + **cost badge** (`$0.42 query cost` / `No query cost`; tooltip: model, duration, local run time) + description.
- Buttons: split **Run AI** + queue dropdown caret · **Continue →**.
  - Run label cycles: `Run AI` → `Run AI Later` (something else running) → `Don't Run AI` (this one queued) → `Stop AI` / `Stopping AI...` (running).
  - Queue dropdown: checkbox list of the 8 queueable sections; currently-running one locked (amber `running` tag). Ticks are a *pending* selection seeded from the live queue on open — nothing runs until **Apply**, which enqueues newly ticked sections and unqueues cleared ones and closes the dropdown. Left button flips **Select all** / **Clear all**.
- While running: book-scroll animation (320×80) + **LiveTracePanel**.
- Otherwise: error block if failed (message + collapsible detail + Copy button), and the markdown report in a slate-900 rounded scroll pane (24 px padding).

### LiveTracePanel

Three collapsible sections: **System Prompt** (collapsed), **User Prompt** (collapsed, copyable), **Streaming Response** (expanded, auto-scroll pinned to bottom, max height ~16 rem). Optional **Web Activity (N)** list — 🔍 per search query, 🌐 per fetched URL. Optional amber rate-limit countdown row (driven by `rate_limit_retry` events).

## 3.5 Story Bank

Header + cost badge + Run/Continue as above. List of collapsible story cards:

- Collapsed: 📖 + title + up to 4 tag chips.
- Expanded: SITUATION / TASK / ACTION / RESULT with indigo uppercase labels; "Earned Secret" panel (purple-900/20 bg, purple-800 border); Fit Score chips colored by value: `Strong Fit` green, `Workable` blue, `Stretch` yellow, else red.

## 3.6 Mock Interview

Two states:

- **Setup**: 5 format tiles in a grid — Behavioral 💬, System Design 🏗️, Case Study 📊, Panel 👥, Bar Raiser ⚡ — + full-width "Start {Format} Interview" button.
- **Chat**: header `Mock Interview — {Format}`, New Interview / Continue buttons. Message bubbles max-width 85 %: user right-aligned (`#1e293b`, radius `12 12 4 12`), assistant left (`#0f172a`, 1 px `#1e293b` border, radius `12 12 12 4`), captions "You"/"Interviewer". Bubble bodies render through the same Markdown component. 3-row input; Enter sends, Shift+Enter newline. Reply containing the literal token `END_OF_INTERVIEW` ends the session and hides the composer.

## 3.7 Resume Tailor (most complex view)

- Header + cost badge. Buttons: split **Run AI** + queue caret · **Use AI Resume** (violet) · **Chat with Coach** (cyan) · **Continue →**.
- Optional collapsible cyan **Resume Coach Chat** panel (max height 50 % of viewport; captions "You"/"Coach") — same chat mechanics as Mock Interview.
- While the review runs: indeterminate progress bar under the header and the **live trace** in place of the split — the same running indication as the §3.4 agent screens (the original hides the split behind `isRunning && liveTrace`). A failed run shows the error block there too.
- Main area: **draggable splitter** — left pane "AI ANALYSIS & SUGGESTIONS" (markdown report), right pane "Your Resume" with tabs **Edit / Preview / Comparison**.
  - Splitter clamps 20–80 %; container under 640 px wide flips to vertical stacking (Avalonia: `GridSplitter` + width-triggered layout swap).
  - **Comparison** tab: LCS line diff of saved vs edited tagged text — deleted rows red (`bg red-950/70, text red-300`, `−` prefix), added rows green (`bg green-950/70, text green-300`, `+` prefix), unchanged slate-400; monospace, 12 px, line-height 20 px.
- **Use AI Resume**: extracts the draft from the analysis with regex `^#{1,6}\s*6[^#\n]*tailored resume draft[^#\n]*\n([\s\S]*)$` (case-insensitive, multiline), then strips a trailing `#… a note …` block. Port this extraction exactly.
- Footer: Insert-tag dropdown · **Export .docx** (sky/blue) · **Save** (green, flashes `✓ Saved!`). After export: sky info bar with the path + "📂 Open folder" button. Unsaved edits auto-save when leaving the view.

## 3.8 Debrief

- Title + **total** cost badge (`Cost: $X · Queries: N`; tooltip enumerates every section with model, plus total duration and local time range).
- Full-height notes textarea, **Insert timestamp**, **Save Debrief**.

## 3.9 Custom Action view

- ⚡ + name + cost badge; **Edit** / **Delete** / **Run AI**.
- Edit mode: temperature number input (0–2, step 0.1, empty = API default, with a 4-cell guidance grid), monospace prompt-template textarea, "Insert tag" dropdown (11 tags). Saving with unknown `{{tags}}` → amber confirm dialog.
- View mode: collapsible prompt preview + markdown result.

## 3.10 About

Version, 11-row icon/label/description feature list, GitHub link (styled `text-indigo-300`, underline, offset 4), "Built with" list. Links open externally.

## 3.11 Configuration

- Four provider radio cards (Anthropic / OpenAI / Google Gemini / Local Ollama), each with a Configured/Not-set pill.
- Per provider: API-key field with eye toggle + "Get API key ↗" link (`text-xs`, indigo-400) + model dropdown.
  - Anthropic models: Claude Sonnet 4.6 (Balanced, recommended), Claude Opus 4.7 (Most capable), Claude Haiku 4.5.
  - OpenAI: gpt-5.5, gpt-5.4, gpt-5.4-mini, gpt-5, gpt-4.1, gpt-4o, gpt-4o-mini, gpt-4.1-mini.
  - Gemini: model list fetched live from the API.
  - Ollama: base URL field · "Fetch locally available models" dropdown annotating tool support (`· tools ✓`) + yellow warning when the chosen model lacks tools · `num_ctx` slider with 8 stops (`Default, 4k, 8k, 16k, 32k, 64k, 128k, 256k` → `'', 4096 … 262144`).
- **Resume Info**: Full name, Contact info.
- **Data Storage**: current path + **📂 Open folder** / Default / Browse… / Save; file list with sizes. Open folder opens the *active* data directory (not the pending edit in the textbox) in Explorer / Finder / `xdg-open`, and is disabled when that directory doesn't exist. The list covers the JSON stores **and** `resume-template.docx`, which is data too — it also moves with the folder on a migration. Changing the folder opens the **MigrationModal** — 5-phase wizard: confirm → copy → verify (byte-for-byte) → save config → delete originals, with distinct error states and retry paths (see 08).

## 3.12 Dialog inventory

Confirm dialogs (delete application, delete resume, delete custom action, unknown-tags warning, migration confirm), description prompt (save to library), and the migration wizard. Port as modal `Window.ShowDialog<T>` per openlogi-net's convention (`Close(value)` result).
