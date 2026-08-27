# 00 — Overview

## What Interview Flow is

A local, single-user AI interview-prep coach. The user pastes a job posting, uploads a resume, and works through a 12-step workflow where each step calls an LLM agent and stores a markdown report. All data stays on disk locally; only LLM API calls leave the machine.

### The 12 steps

| Step id | Label | Icon | Description | Flags |
|---|---|---|---|---|
| `setup` | Setup | 📋 | Upload job posting | |
| `resume` | Resume | 📄 | Upload or select resume | resumeBadge |
| `research` | Research | 🔍 | Deep-dive company analysis | webSearch |
| `interview_intel` | Interview Intel | 🕵️ | Real questions & interview patterns | webSearch |
| `jd_decode` | Job Decoder | 🔬 | Six-lens deep read of the job posting | |
| `resume_tailor` | Resume Tailor | ✏️ | Tailor resume to the JD | needsResume |
| `stories` | Story Bank | 📖 | Mine & manage your stories | needsResume |
| `pitch` | Pitch | 🎯 | Build your positioning | needsResume |
| `concerns` | Concerns | 🛡️ | Anticipate objections | needsResume |
| `mock_interview` | Mock Interview | 🎙️ | Practice with AI interviewer | needsResume |
| `salary` | Salary | 💰 | Comp negotiation coaching | webSearch |
| `debrief` | Debrief | 📝 | Post-interview reflection | |

Plus: **Configuration** page, **About** page, and unlimited user-defined **Custom actions** (each becomes its own sidebar entry + view).

### Cross-cutting behaviors

- Multiple independent "applications" (workflows) tracked simultaneously; the Setup screen lists, selects, clones (`Company | copy N`), and deletes them.
- A single-slot **background queue**: only one AI section runs at a time; others wait in a fixed order. Queue state is in-memory and lost on exit.
- Live streaming trace of each AI call (system prompt / user prompt / streamed response / web-search activity), plus per-query cost, model name, duration, timestamp.
- Resume library shared across all applications (deduped by description).
- Four LLM providers: Anthropic, OpenAI, Google Gemini, local Ollama (with DuckDuckGo web-search tool loop).

## Port scope

**In scope**

- Full functional parity with interview-flow v1.5.0: all 12 steps, custom actions, queue, configuration, resume pipeline, .docx export, mock-interview and resume-coach chats, data-folder migration wizard.
- Byte-compatible data files (see [02-data-formats.md](02-data-formats.md)) — the same data folder must work under both apps.
- Visual parity on the "major things": markdown text styles, font sizes (Inter, root 16 px), hyperlink behavior (open in OS browser), mermaid diagrams, dark theme layout.
- Windows **and macOS**.

**Out of scope / acceptable differences**

- Native control chrome (button/textbox frames, focus rings, scrollbar visuals) may differ.
- Langfuse tracing — **replaced by OpenTelemetry** (decided; see 05 §5.8). Langfuse `.env` keys are preserved on round-trip but unused.
- The Python export CLI (`app/export_responses.py`) — **dropped** (decided, not needed at this time); the original CLI still works against a shared data folder.
- The FastAPI HTTP surface itself: the port is a native app, not a server (see ADR-003, accepted).

## Fidelity rules (what "must match" means)

1. **Markdown rendering** — heading sizes/weights, paragraph spacing/line-height, list indentation, inline/fenced code styling, blockquotes, GFM tables, `breaks: true` semantics (single newline → line break), tag-emoji decoration, and the raw-HTML `search-warning` block. Exact spec in [04-markdown-rendering.md](04-markdown-rendering.md).
2. **Fonts** — Inter (weights 300–700), root size 16 px, same relative sizes for prose elements.
3. **HTML links** — clicking any `http:`/`https:` link opens the OS default browser; other schemes are ignored; nothing navigates in place.
4. **Mermaid diagrams** — rendered with the same theme and the same input-normalization quirks.
5. **Colors of the content area** — dark slate palette (`bg-slate-950` page, `bg-slate-900` panels, `text-slate-300` prose body); minor deviations in control chrome are fine.

## Non-goals

- No web/server deployment mode.
- No multi-user support.
- No redesign of the workflow, prompts, or data model — this is a port, not a v2.

## Key risks (detail in [09-porting-plan.md](09-porting-plan.md))

1. Markdown + Mermaid + raw-HTML fidelity in Avalonia (ADR-001) — no off-the-shelf Avalonia markdown control matches `marked` + `breaks:true` + Mermaid + raw HTML passthrough.
2. PDF parsing parity — the original uses PyMuPDF font-size heuristics; the .NET equivalent (e.g. PdfPig) will need tuning against the same test corpus.
3. Streaming provider SDKs across four providers with rate-limit retry parity.
4. ⚠️ The original repo's `.env` at `c:\dev\interview-flow\.env` contains **live API keys in plaintext**. Never copy it into this repo; those keys should be rotated.
