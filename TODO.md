# TODO

Task list for the port, grouped by topic in rough implementation order. Details live in `docs/` (referenced per section). Check items off as they land; items already done during scaffolding are pre-checked.

## 1. Foundations (M0) — `docs/01-architecture.md`

- [x] Solution scaffold: `InterviewFlow.slnx`, App/Core/Tests projects, `Directory.Build.props`, `.editorconfig`, `.gitignore`
- [x] Theme.axaml with the slate palette; Inter font wired (`WithInterFont`)
- [x] CI on windows-latest + macos-latest (build + test)
- [x] `run.cmd` dev launcher
- [ ] Verify build/run/tests on an actual macOS machine (CI covers build/test; do one manual app launch)
- [x] `DiagnosticLog` port: static no-throw facade, area-tagged, lazily created file, suppressible for tests (+ `[ModuleInitializer]` test setup)
- [x] `Paths` helper: per-user config/data/log locations for Windows + macOS

## 2. Markdown rendering (M0 → M3) — `docs/04-markdown-rendering.md`, ADR-001

- [x] Core pipeline pieces: `TagEmojiDecorator`, `MermaidNormalizer` (5 normalizations), `SearchWarning.Extract` — unit-tested
- [x] `MarkdownView` first pass: headings, breaks:true, GFM tables, code blocks, blockquotes, lists, search-warning banner, hit-tested links (http/https → OS browser)
- [x] Golden-corpus spike harness (side-by-side source/render in `MainWindow`)
- [ ] Screenshot-compare golden corpus vs the original app at identical sizes; fix deviations (§4.2 metrics are the contract) — *needs the original running; side-by-side QA*
- [x] Link styling verified from the original's source: Tailwind CDN preflight resets anchors to inherit/no-underline; port matches (04 §4.3). Final eyeball at side-by-side QA
- [x] Inline-code chips: `InlineUIContainer` + `Border` (0.15em/0.4em padding, 4px radius, slate bg) — verified via in-app screenshot. Trade-off: chip text excluded from block selection (documented in 04)
- [x] Streaming-append performance: 120 ms debounced re-render in `MarkdownView` + mermaid SVG memo cache in `MermaidHost` (editor keystrokes and streamed deltas no longer trigger full-cost re-renders); incremental block reuse only if profiling later demands
- [x] Golden corpus expanded with the structural patterns of real stored reports (bold-key metadata lines relying on breaks:true, `---` rules, nested lists with inline confidence tags, inline code in bullets) — synthesized content, no personal data copied
- [ ] Text-selection: per-block selection works; cross-block selection + code-chip selection are accepted deviations (04 §4.3 note). Revisit in M3 (likely mitigation: per-pane Copy button)
- [ ] **Mermaid (ADR-001b — candidate 1 adopted, works end-to-end; see ADR-001 findings)**:
  - [x] Jint spike: bundle evaluates, initialize + render pipeline runs
  - [x] Real minimal DOM (`MermaidDom.js`): prototype-based tree, `innerHTML` markup parser, serializer, selector engine — SVG produced for real diagrams
  - [x] `getBBox`/`getComputedTextLength` bridged to Avalonia `FormattedText` measurement (`MermaidHost`)
  - [x] SVG displayed in `MarkdownView` via `Svg.Controls.Skia.Avalonia`, with 80 %-opacity source fallback
  - [x] JS engine cached across renders; recovers after a failed render
  - [x] Invisible-diagram fixes: `htmlLabels:false` (foreignObject → native `<text>`), xhtml-xmlns strip + text fill/font post-processing, `securityLevel:'loose'`, nested-tspan flattening, `:first-child` selector for paint order (rationale in ADR-001); guarded by `MermaidSvgOutputTests` incl. a pixel-level rasterization check
  - [x] Layout fixes: shape-leaf getBBox (dagre got 0-width nodes → overlap), line-aware text measurement, SkiaSharp-based measurement in `MermaidHost` (metrics match Svg.Skia's drawing font), void-element serialization, line-tspan merging, explicit `text-anchor="middle"` — verified by PNG inspection
  - [x] Cluster/subgraph title: mermaid never positions it under the shim (and it paints beneath the cluster fill) — `PositionClusterTitles` matches the twin cluster groups by id, reparents the title after the rect, and centers it inside the top edge; verified by PNG inspection
  - [ ] Visual QA of diagram output vs the original app side-by-side (incl. disconnected-component ordering — expected to match, same dagre engine)
  - [x] Diagram rendering moved off the UI thread: dimmed-source placeholder swaps to the SVG when the worker finishes; cache hits render synchronously
  - [x] Mermaid debug harness: the spike `MainWindow` (live source editor + rendered preview) covers `mermaid-debug.html`'s role — keep it reachable as a dev window when the real shell lands in M2
  - [ ] Packaging: ship `mermaid.min.js` beside the app (`Assets/`); dev builds use `spikes/assets/` (add to §11 release tasks)

## 3. Data layer (M1) — `docs/02-data-formats.md` ✅ COMPLETE

- [x] Complete model set transcribed from `app/models.py` (`Models/InterviewModels.cs`) — exact JSON names via `JsonPropertyName`, same declaration order (drives property order), same defaults. Noted deviation: Pydantic-required `Story.title`/`InterviewQuestion.question` default to `""` (port is permissive where the original skips)
- [x] `StateStore`: atomic write, UTF-8 no BOM, indent 2, LF newlines, corrupt-entry skip on load, read-merge-write behind a lock, `updated_at` stamping, summaries + resume-library dedupe ports
- [x] Non-ASCII parity: relaxed encoder + surrogate-pair unescape pass (`StateJson.UnescapeNonBmp`) so emoji stay literal like Python's `ensure_ascii=False`
- [x] ID generation (`ModelDefaults.NewId`, 12-hex) + validation guard
- [x] `CustomActionStore`: load/save + corrupt-entry skip + `NameExists` uniqueness check
- [x] Env-file codec (`EnvFile`): exact `_update_env_file` port — replace-in-place, comments/unknown lines verbatim, append missing, OS newlines + trailing newline (matching Python text-mode write); dotenv-style reads (last wins, quotes stripped)
- [x] `AppConfig` typed accessor: process-env-wins precedence (dotenv `override=False`), typed keys with `.env.example`/`main.py` defaults, env-path resolution (beside-exe portable → per-user)
- [x] Round-trip tests vs the real data file (`RealDataCompatTests`, read-only, skip when absent): all 108 states load with zero skips, dump→reload→dump byte-stable, per-state payload spot checks
- [x] Newer-schema-version guard: `version > 1` → `DataFileVersionException`, refuses load (original ignores version entirely; the guard protects against a future original writing v2)
- [x] Fixture corpus: synthetic full-coverage `sample-data.json`/`sample-custom-actions.json` (incl. deliberate corrupt entries; no personal data) + `Parse-Test-Resume.docx`, `parsed-resume.txt`, `Resume-Template.docx`, `section-headings.md` copied for §8
- [x] **Exit gate PASSED: the original app's own Pydantic loader (`app.state._load_all` in the original's venv) loaded the port-written file — 108 states + 3 custom actions, zero loss, byte-identical `raw_report` spot check**

## 4. App shell & navigation (M2) — `docs/03-ui-spec.md` §3.0–§3.2, §3.10 ✅ COMPLETE

- [x] `MainWindow` shell: 240 px `SidebarView` + `TransitioningContentControl` (0.25 s cross-fade ≈ fade-in), window title binding; F12 opens the markdown/mermaid dev harness (`DevMarkdownWindow`)
- [x] Sidebar: header (ⓘ/wordmark/⚙, opacity-pulse amber cog when no provider — scale pulse dropped, transform keyframes not style-animatable), step rows with tile states (idle/active gradient/done ✓/failed !; running spinner + queued ⌛ properties wired, driven by the queue in M5), 🌐/📄 badges, `(tech)` marker (exact keyword list from index.html:1218), step locking, custom-actions section + "+ Add new", progress footer (N/12 + gradient bar)
- [x] Page switching via typed DataTemplates; page VMs under `ViewModels/Pages` (Setup, About, Config stub, Placeholder for M5–M8 screens)
- [x] Setup screen: new/select/clone/delete with confirm dialogs, company/position inputs + `|`-comment hint, job-posting textarea, provider chip (green configured / amber not). Clone naming is an exact port of main.py:1869 (suffix-strip + max-N per position), regression-tested. URL fetch lands in M8 as planned
- [x] About screen: feature list, source link, built-with — content mirrors the original's AboutStep
- [x] `ConfirmWindow`: `ShowDialog<bool>` + `Close(value)`, chrome-less dark panel, requested via VM event so views own modals
- [x] Reusable pieces: `CostBadge` control (chip + tooltip; bound by M5 screens), `Button.primary/.subtle/.danger/.icon` + `TextBox.field` styles in `Styles/AppStyles.axaml`
- [x] Shell-logic tests (`ShellLogicTests`, headless): clone naming/deep-copy, step locking/unlock, tech badge, current_step persistence on navigation, delete-resets-to-setup — caught one real bug (stale lock flag blocked post-select navigation)
- [ ] Typing-dots and book-scroll animations — belong to the M5 screens that use them (chat, Run AI)

## 5. Provider layer (M4) — `docs/05-llm-providers.md` ✅ COMPLETE (live-validated)

- [x] `AgentEvent` record union mirroring the NDJSON vocabulary (`Core/Agents/AgentEvent.cs`); heartbeat kept for contract completeness, never emitted
- [x] `ProviderRouter.StreamQueryAsync` = `iter_text_query` port: send events first, `ResolveProvider` (explicit → OpenAI-key fallback → anthropic), per-query DiagnosticLog line (OTel span comes with M8's observability task)
- [x] Anthropic (raw SSE): max_tokens 16000, usage→cost, web_search_20250305 tool with streamed query assembly, 5-attempt 429 retry (Retry-After header, pre-stream as-is / mid-stream ≥60 s + reset, 5 s countdown heartbeats)
- [x] OpenAI (raw SSE): Chat Completions (+usage chunk) and Responses API web mode (web_search_preview, url_citation → WebFetch, max_output_tokens 8000); message-parsing retry hints, 15·2^n pre-stream floor, transient-stream backoff 5·2^n
- [x] Gemini (raw SSE): generateContent streaming + Google Search grounding + live model listing (stable-first sort)
- [x] Ollama: NDJSON chat streaming, 20-turn tool loop (non-streaming turns, synthesis fallback), num_ctx, `/api/tags` + `/api/show` tool probing; DuckDuckGo html-endpoint search + fetch_url with the original's load-bearing result prefixes; search-status classifier
- [x] Pricing tables verbatim (all three providers + defaults); per-section temperatures + ≤1.0 clamp; three-valued `TemperatureSetting` (section/api-default/explicit)
- [x] All 11 prompt templates embedded + 4-backtick fence loader port (CRLF-normalized)
- [x] Research agent end-to-end: prompt build (`{job_posting}`/`{resume_section}`), web streaming, Sources section (dedupe, exact format), search-warning injection (strings verbatim, round-trips through the renderer's extractor), `SaveResult` state write
- [x] `\n`-only line safety: StreamReader.ReadLine semantics (never splits U+2028/2029/0085)
- [x] 51 unit tests (pricing, temperatures, retry parsing, fence extraction, sources, warnings, fake-HTTP SSE/NDJSON streams incl. 429-retry path) + **live smoke against real APIs**: OpenAI ✓ (exact cost math), Gemini ✓, Ollama ✓ (incl. tool probing); Anthropic path structurally validated (key had no credits; auth + request accepted, billing error surfaced cleanly)
- [ ] Note: Google deprecated `gemini-2.5-flash` (the original's default) for new API keys — the M8 config screen's live model fetch is the fix; until then set GEMINI_MODEL to a current id (e.g. `gemini-3.6-flash`)

## 6. Queue & streaming UI (M5) — `docs/07-queue-and-streaming.md` ✅ COMPLETE

- [x] `QueueManager` (Core/Queue): exact port — single slot, fixed section order + `custom:{id}` at 1000, active-pair dedupe, failure records cleared on retry, cancel (running → canceling + token; runner acknowledges) vs unqueue (waiting only), cleanup for deleted workflows/actions, per-item event history with replay-on-subscribe, queue-changed snapshots (callbacks outside the lock)
- [x] Verified from source: custom actions ARE queueable (`custom:{action_id}`, sort order 1000) — 07 §7.1 updated
- [x] `QueueWorker`: `_run_queue_item` port — publishes every event, settles on error/complete/cancellation, stream-ended-early → failed, factory exceptions fail cleanly
- [x] `LiveTracePanel` control + `LiveTraceViewModel` (headless event consumer): collapsed system/user prompts (user copyable), streaming response with pin-to-bottom auto-scroll (scroll-up unpins, return re-pins), web-activity 🔍/🌐 rows, amber rate-limit countdown; reset event clears accumulated text
- [x] `RunButtonLogic` (Core): the full label state machine incl. disabled "Stopping AI...", target queue-item ids for unqueue/stop
- [x] Sidebar badges wired: queue snapshots → step IsRunning/IsQueued/IsFailed on the UI thread (`MainViewModel.Queue.cs`)
- [x] Cancellation plumbing: per-item `CancellationTokenSource` flows worker → section stream → provider HTTP; cooperative OCE → canceled
- [x] Section-stream factory in the shell: research runs live end-to-end (persists via `ResearchAgent.SaveResult` + refreshes the shell); unported sections fail with a clear message
- [x] 23 new tests (queue mechanics, worker settlement incl. mid-stream cancel, run-button states, trace VM) — 128 total green
- [ ] Queue dropdown UI (checkbox list of the 8 sections) — part of the §7 agent-screen header where it lives

## 7. Agent screens (M5) — `docs/03-ui-spec.md` §3.4, §3.5, §3.8, §3.9 ✅ COMPLETE

- [x] All remaining agents in Core (`SectionAgents`, port of story_miner.py): interview intel (with the technical-section injection), jd decode, resume review, pitches, concerns, salary (web + Sources section), story mining (fence-strip + JSON parse with the original's error messages, "Untitled" default)
- [x] `SectionRunner` (port of `_queued_section_stream` + `_stream_saved_*`): per-section persistence with verbatim precondition messages ("Resume required for …"), `RanAt` stamping, the faithful quirk that only research/intel move `current_step`, custom-action results keyed by NAME + `custom_{id}` completed step
- [x] Generic agent screen (`AgentPageView`) for the 6 markdown sections: header + 🌐 badge + CostBadge tooltip, split Run button + queue dropdown flyout (checkbox list, running locked), Continue →, LiveTracePanel while running (with replay on re-open), error block with collapsible detail, markdown result pane. Shared `AgentRunViewModel` component owns the state machine + queue/item subscriptions (disposed on page swap)
- [x] Story Bank: collapsible STAR cards (tag chips, indigo labels, purple earned-secret panel, fit chips green/blue/yellow/red) — rendered output verified via headless frame capture
- [x] Debrief: total cost + query count with per-section tooltip breakdown, notes editor (continues latest note; save appends + ProgressEntry, matching the route), insert timestamp, ✓ Saved! flash
- [x] Custom actions: full CRUD (unique-name check, temperature 0–2 validation + guidance line, tag-insert dropdown, unknown-tag confirm dialog), view mode with prompt-template expander + markdown result, run through the queue (`custom:{id}`), delete cleans the queue
- [x] `{{tag}}` substitution port (`CustomActionAgent`): `<user_provided_*>` wrapping, `(not provided)`, pitch-variant joining, unknown tags left literal + detected
- [x] Book-scroll run animation approximated as an indeterminate gradient progress bar (recorded deviation)
- [x] Tests: 17 new Core tests (agents/substitution/runner incl. fake-provider persistence) + **headless Avalonia view smoke tests** (every page instantiates + lays out; test project migrated to xunit v3 for Avalonia.Headless 12) — 147 total green. Research + Story Bank pages verified pixel-level via headless frame capture

## 8. Resume pipeline (M6) — `docs/06-resume-pipeline.md` ✅ COMPLETE (byte-parity verified)

- [x] File intake (`ResumeIntake`): extension allow-list, 10 MB cap, magic-byte checks, verbatim error messages
- [x] DOCX → clean markdown (`DocxExtractor.ExtractMarkdown`, Open XML SDK) — **byte-identical** to the Python original on the fixture corpus
- [x] DOCX → diagnostic raw dump — **byte-identical** (style/indent/align/spacing/numPr/tabs + run annotations)
- [x] PDF → markdown (PdfPig): modal-body-size heuristic (≥1.5/1.3/1.15), bullet chars, bold/italic spans; synthetic-PDF test. Segmentation differs from PyMuPDF by construction — tolerance recorded in docs/06 §6.2
- [x] TXT/MD/RTF passthrough (UTF-8 decode + trim, matching the original); `.doc` handled as DOCX like the original
- [x] Section map (`SectionMap`): builtin table + first-matching-table parser, lowercase keying, **hot-reload on every parse**, ALL-CAPS ≤65-char rule
- [x] Tagging heuristic (`ResumeTagger`) — full port incl. header skipping, deferred job-title resolution, contextual tagging; **byte-identical** to Python output
- [x] Resume screen: file picker + drag-drop (Avalonia 12 `IDataTransfer` API), Edit/Raw/Preview tabs, insert-tag dropdown, saved-resume library (select/delete, description prompt dialog), Save & Continue
- [x] `TaggedResumePreview` control with the exact §4.6 metrics + contact-header toggle — verified by headless frame capture against the real fixture resume
- [x] Styled .docx export (`DocxExporter`): template body-strip keeping `w:sectPr`, style-name→id mapping, `[Skill]` colon bolding, `[Section Heading]Summary` skip, plain fallback, filename builder. **Cross-validated: python-docx opens the port-built file with correct styles, bold runs, and preserved sectPr**
- [x] Export flow: `StorageProvider` save dialog, sky path bar, 📂 reveal-in-file-manager (per-platform)
- [x] `LineDiff` (LCS) for the Resume Tailor comparison tab — ready for §9
- [x] 3 parity fixtures generated by running the ORIGINAL's own extractors (`parsed-resume-generated/-raw/-tagged.txt`); 22 new tests — 173 total green

**Parity notes discovered while porting** (all recorded in code comments):
- The original's hyperlink-URL resolution in `_extract_markdown_from_docx` is dead code (calls a python-docx API that doesn't exist, swallows the error), so hyperlink runs render as plain text — matched deliberately.
- python-docx reports alignment enum names (`justify`) and translates builtin style names (`heading 1` → `Heading 1`); both replicated for byte parity.

## 9. Resume Tailor & chats (M7) — `docs/03-ui-spec.md` §3.6, §3.7 ✅ COMPLETE

- [x] `ChatProvider` (Core): single-turn **non-streaming** completion for all four providers — what the original's chat sessions use (max_tokens 8192, OpenAI's one-retry-at-2×-hint rule, per-provider message shaping)
- [x] `ChatSessionBase` + `MockInterviewSession` + `ResumeChatSession`: exact ports incl. the five format instruction blocks, opening messages, review-section injection, `END_OF_INTERVIEW` detection, `MockSession` record building
- [x] `TailoredResume`: "Use AI Resume" extraction — exact regex port (section-6 heading match + trailing "a note" strip), `HasDraft` gating, verbatim no-draft message, `StripTags`
- [x] Splitter layout with `GridSplitter`; **vertical stacking under 640 px** (re-lays the grid and flips splitter direction)
- [x] LCS diff Comparison tab: red `−` deletions / green `+` insertions / slate unchanged, monospace 12 px — verified by headless frame capture
- [x] Auto-save on page swap (Dispose) + Save with `✓ Saved!` flash; dirty tracking drives the diff baseline
- [x] Resume Coach chat: collapsible cyan panel, session started lazily on first open
- [x] Mock Interview: 5 format tiles with selection state, dynamic "Start {Format} Interview", chat view, New Interview reset, completion persists a `MockSession` + `mock_interview` step
- [x] Shared `ChatPanel` control + `ChatViewModel`: bubbles through `MarkdownView` (user right `#1E293B` radius 12/12/4/12, assistant left `#0F172A` bordered radius 12/12/12/4), "You"/"Interviewer"/"Coach" captions, **Enter sends / Shift+Enter newline**, auto-scroll to newest, typing-dots indicator, composer hidden on completion, end-token stripped from display
- [x] 19 new tests (extraction variants, session transcripts, format metadata, temperature clamping, error paths) — 186 total green; both screens verified by headless frame capture

## 10. Configuration & migration (M8) — `docs/08-configuration.md` ✅ COMPLETE

- [x] Configuration screen: four provider cards with radio selection + active highlight, green Configured / slate Not-set pills, masked key fields with 👁 toggles, "Get API key ↗" links (all three URLs verbatim), model dropdowns with the original's exact model lists and notes — verified by headless frame capture
- [x] Gemini live model fetch (falls back to a free-text field when unfetched); Ollama model fetch with `· tools ✓` annotation + amber no-tools warning; `num_ctx` slider with the 8 stops (Default/4k…256k) and live label
- [x] Resume Info (name, contact) + Data Storage (path, Default/Browse…/Save, file list with sizes and sessions/custom-actions notes)
- [x] Apply-on-change persistence to the shared `.env` (ADR-002) — sets the process env too so changes take effect immediately, and refreshes the sidebar cog / provider chip
- [x] Env-file + data-folder location follow the original's working-directory convention: `./.env` and `./data` (the repo root under `run.cmd`) win over beside-exe and per-user locations; `run.cmd` pins the working directory; `.env.example` documents every key
- [x] "Import .env…" in Configuration: file picker → confirm → replace the active settings file (previous kept as `.bak`), re-sync the process environment so imported values aren't shadowed by in-session edits, reload the page fields, and re-point the data stores if the import carries a different `INTERVIEW_DATA_DIR`. Refuses files with no settings and self-imports
- [x] **Data-loss guard**: choosing a data folder that already contains workflows now *adopts* it (save location + re-point stores, nothing copied/moved/deleted) instead of migrating into it — the old path copied the current folder over it with `overwrite: true`, which would have destroyed the target's data. Empty targets still run the move wizard. Empty-data-folder hint added to the UI; 5 tests cover the branches
- [x] Null-safety fix in Configuration: dropdowns bind `SelectedItem` to nullable options rather than `SelectedValue` to the model strings, so an empty list can't null out a configured model name (that null previously hit `value.Trim()` and was swallowed, silently dropping saves). Global unhandled-exception logging added in `Program.cs`
- [x] Migration wizard (`DataMigration`): confirm → copy → **byte-for-byte verify** → save config → delete originals. Ordered so any failure before the config save leaves data untouched; the shell re-points its stores with no restart (port of apply-location)
- [x] Job-posting URL fetch (`JobPostingFetcher`): SSRF guard (private/loopback/link-local/metadata addresses refused **before** any request), HTTP fetch + `_html_to_text` port, `< 200 chars` heuristic → **LLM web-fetch fallback** per the ADR decision, Ollama skipped (its tool loop fetches locally), verbatim "paste the text instead" message. Wired into Setup's Save & Continue with inline status
- [x] OpenTelemetry (`Telemetry`): one span per agent query with gen_ai.* + cost/duration/tool attributes, OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, zero-cost no-op otherwise; initialized/flushed in the composition root
- [x] 23 new tests (migration phases incl. corrupted-copy detection, SSRF rejection table, html-to-text, fetch fallback paths) — 209 total green

**Parity note:** entities are decoded *after* whitespace collapse (the original's order), so `&nbsp;` survives as U+00A0 — matched deliberately and asserted in tests.

## 11. Packaging & release (M9) — `docs/09-porting-plan.md`

- [ ] App icon + version stamping
- [ ] Windows: self-contained publish, installer (Inno Setup), portable zip
- [ ] Bundle `mermaid.min.js` (11.4.0) into the app's `Assets/` in publish output (dev builds read `spikes/assets/`)
- [ ] macOS: `.app` bundle (osx-arm64 at minimum), ad-hoc signing, first-launch note
- [ ] Release workflow (CI job), smoke tests on both OSes

## 12. Parity acceptance pass — checklist in `docs/09-porting-plan.md`

- [ ] Data compatibility suite green (shared folder, env round-trip, tagged-output parity, docx export parity)
- [ ] Rendering fidelity checklist green (screenshot gates on both OSes)
- [ ] Behavior checklist green (queue, badges, costs, providers, chats, migration)
- [ ] Rotate the API keys exposed in the original repo's `.env`; confirm it was never committed here
