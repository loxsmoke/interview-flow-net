# 02 — Data Formats (compatibility contract)

The port must read and write these formats **byte-compatibly enough that the original app and the port can share a data folder**. Where exact byte equality is impractical (e.g. JSON key ordering), the rule is: *the original app must load what the port writes, and vice versa, with no data loss.*

Sources: `app/state.py`, `app/models.py`, `app/main.py` in `c:\dev\interview-flow`.

## 2.1 Session store — `<DATA_DIR>/interview-flow-data.json`

Single JSON file holding **all** workflows.

- **Write discipline**: atomic — write to temp file, then rename over (`os.replace`). Port: `File.Replace`/move-overwrite, same as openlogi-net's `SaveAtomic`.
- **Encoding**: UTF-8, `indent=2`, non-ASCII kept literal (`ensure_ascii=False`). Port: `JsonSerializerOptions { WriteIndented = true, Encoder = UnsafeRelaxedJsonEscaping }` — verify emoji and non-Latin text round-trip unescaped.
- **Envelope**: `{ "version": 1, "states": { "<12-hex-id>": { …InterviewState… } } }`
- **IDs**: `uuid4().hex[:12]`, validated `^[a-f0-9]{12}$` (also a path-traversal guard in the original — keep the validation).
- **Timestamps**: naive local ISO-8601 with microseconds, e.g. `2026-05-08T15:27:44.718213`. Port: `DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")`. Do **not** switch to UTC or add offsets — the original parses these back.

### InterviewState shape (per-workflow)

Top-level scalar/collection fields:

```
id, created_at, updated_at,
job_posting, resume, resume_raw, resumes[], custom_action_results{},
company_name, position,
research{}, interview_intel{}, jd_analysis{},
stories[], stories_cost_usd, stories_model_name, stories_duration_ms, stories_ran_at,
mock_sessions[],
comp_data{}, pitch{}, progress[],
resume_review, resume_review_cost_usd, resume_review_model_name,
resume_review_duration_ms, resume_review_ran_at,
tailored_resume, resume_tagged,
concerns_analysis, concerns_cost_usd, concerns_model_name,
concerns_duration_ms, concerns_ran_at,
interviewer_concerns[], thank_you_drafts[], debrief_notes[],
completed_steps[], current_step
```

Nested types (full field lists in `app/models.py`; port to `Core/Models/` as records/classes with exact JSON names):

- `research`: `company_name, summary, culture, reputation, tech_stack[], products[], challenges[], green_flags[], red_flags[], fit_score, raw_report, query_cost_usd, query_model_name, query_duration_ms, query_ran_at, researched_at`
- `interview_intel`: `raw_report` + the four `query_*` metadata fields
- `jd_analysis`: `raw_jd, requirements[], nice_to_haves[], hidden_signals[], cultural_cues[], missing_signals[], confidence_tags{}, raw_analysis, …`
- `comp_data`: `range_low, range_high, equity_notes, negotiation_scripts[], fallback_language[], raw_analysis, …`
- `pitch`: `elevator_10s, networking_30s, recruiter_60s, interview_90s, value_proposition, …, talking_points[], thirty_sixty_ninety`
- `Resume`: `id, created_at, description, text`
- `Story`: `id, title, situation, task, action, result, tags[], earned_secret, fit_scores{}, times_used, created_at`
- `MockSession`: `id, format, questions[], overall_scores{}, bottleneck, root_cause, summary, created_at`
- `InterviewQuestion`: `question, answer, scores{}, feedback, interviewer_thoughts`
- `ProgressEntry`: `date, event_type, notes, scores{}, self_assessment{}`
- `CustomActionResult`: `result, cost_usd, model_name, duration_ms, ran_at` — stored in `custom_action_results` **keyed by action name**

> **Done (M1):** complete field lists transcribed into `src/InterviewFlow.Core/Models/InterviewModels.cs` (exact names, order, defaults). Round-trip verified two ways: `RealDataCompatTests` (all 108 real states load with zero skips and re-serialize stably) and a live cross-check — the original's Pydantic loader validated the port-written file with zero loss.

### Compatibility rules

- Unknown fields in the file: preserve on round-trip if feasible, or at minimum tolerate (`JsonUnmappedMemberHandling` decision — *TBD*). Pydantic default drops unknowns; matching that is acceptable.
- Missing fields: fill with defaults (Pydantic default behavior).
- Numbers: `cost_usd` is a float (`0.0`), `duration_ms` int, `fit_score` int. Don't serialize `0.0` as `0` — verify the original accepts both (Pydantic does; keep it simple).

## 2.2 Custom actions — `<DATA_DIR>/custom-actions.json`

Global (not per-workflow).

```json
{
  "version": 1,
  "actions": [
    {
      "id": "36161ba18915",
      "name": "What question to ask",
      "description": "Questions to ask during interview",
      "prompt_template": "…\nCompany: {{company_name}}\nPosition: {{position}}\n…",
      "temperature": null,
      "created_at": "2026-06-24T13:52:06.973452"
    }
  ]
}
```

- `temperature: null` = use API default; otherwise 0–2.
- Names must be unique (original returns 409 on conflict; port: validation error dialog).

### Template tags

`{{resume}} {{job_posting}} {{company_name}} {{position}} {{research}} {{jd_analysis}} {{stories}} {{pitch}} {{concerns}} {{interview_intel}} {{comp_data}}`

Substitution wraps non-empty values as `<user_provided_{tag}>\n{value}\n</user_provided_{tag}>`; empty values substitute the literal string `(not provided)`. Unknown tags at save time trigger a confirm dialog (amber "Unknown tag(s)"), not an error.

## 2.3 Config — `.env` file

See [08-configuration.md](08-configuration.md) and ADR-002. Keys:

```
ACTIVE_PROVIDER=anthropic          # anthropic|openai|gemini|ollama
ANTHROPIC_API_KEY=  ANTHROPIC_MODEL=claude-sonnet-4-6
OPENAI_API_KEY=     OPENAI_MODEL=gpt-4o
GEMINI_API_KEY=     GEMINI_MODEL=gemini-2.5-flash
OLLAMA_BASE_URL=http://localhost:11434  OLLAMA_MODEL=llama3.2  OLLAMA_NUM_CTX=
RESUME_NAME=        RESUME_CONTACT=
INTERVIEW_DATA_DIR=
LANGFUSE_PUBLIC_KEY=  LANGFUSE_SECRET_KEY=  LANGFUSE_BASEURL=http://localhost:3000
```

The original **rewrites the `.env` in place**, preserving comments and unrelated lines: update matching `KEY=` lines, append new keys at the end. The port's config codec must replicate this (it is the compatibility surface if the user runs both apps).

## 2.4 Resume tag DSL (`resume_tagged` field)

Line-prefix format; each line is `[Tag]content`. Tags (case-insensitive on consume):

```
[Summary] [Section Heading] [Job title] [Job summary] [Job bullet] [Skill] [Additional info]
```

Produced by the tagging heuristic ([06-resume-pipeline.md](06-resume-pipeline.md)), hand-editable in the Resume screen's Edit tab, consumed by the tagged HTML preview and the .docx exporter. Untagged lines are legal (rendered as plain paragraphs; exported with default style).

## 2.5 Section-heading map — `app/section-headings.md` → shipped resource

Markdown file containing a table mapping resume heading text → section type. Parsing rules (`_parse_section_map_md`):

- Find the **first** table whose first header cell is exactly `Section type`.
- Use only columns 1–2; key = column 2 lowercased; value = column 1.
- Section types: `summary | experience | skills | additional`.
- Re-read **on every parse** (hot-reload, no restart needed).
- Independent rule: ALL-CAPS lines (≤ 65 chars) are always treated as headings regardless of the table.

Port: ship the same file as a user-editable file (location *TBD* — beside the app or in the data dir; original keeps it in the app folder), hot-read on each parse.

## 2.6 Word template — `<DATA_DIR>/resume-template.docx`

(README refers to lowercase name; the repo ships `Resume-Template.docx` — match case-insensitively.)

Required paragraph style names: `Name`, `Contact line`, `Section Heading`, `Summary`, `Job title`, `Job summary`, `Job bullet`, `Skill`, `Additional info`.

Export algorithm (must match `main.py:_build_resume_doc_styled`):

1. Copy the template; strip every body child **except** `w:sectPr` (preserves page setup).
2. Write Name + Contact line paragraphs from `RESUME_NAME` / `RESUME_CONTACT` config.
3. One paragraph per tagged line, paragraph style = tag name (case-insensitive style lookup).
4. Special cases:
   - `[Section Heading]Summary` is **skipped** entirely.
   - `[Skill]` splits at the first `:`; the prefix **including the colon** is a bold run, then `" " + rest` as a plain run.
5. No template present → fresh document: `#`/`##`/`###` → Heading 1–3, `- `/`* ` → `List Bullet` style.

Export filename: `FirstName_LastName_Resume_YYYYMMDD_Company.docx` — each part takes text before the first `|`, strips `[^\w\s-]`, collapses whitespace to `_`; empty parts are omitted.

## 2.7 Uploaded resume files (input)

Accepted: `.pdf .docx .doc .txt .md .rtf`; max 10 MB; magic-byte check (`%PDF`, `PK\x03\x04`). Parsing detail in [06-resume-pipeline.md](06-resume-pipeline.md). Produces `{text (clean markdown), raw (diagnostic dump), tagged, filename, chars}`.

## 2.8 Internal streaming events (not a file format)

The NDJSON event vocabulary is retained as the internal agent→UI contract; documented in [07-queue-and-streaming.md](07-queue-and-streaming.md).

## 2.9 Export CLI output (`exports/*.txt`) — *dropped*

`python -m app.export_responses` text format; **not ported** (decided — see 00-overview). Because data formats are shared, the original Python CLI can still be run against the same data folder if the export is ever needed.

## Test fixtures to copy from the original repo

- `tests/app/Parse-Test-Resume.docx`, `tests/app/parsed-resume.txt` (parser parity corpus)
- A real `interview-flow-data.json` with populated sections (round-trip test)
- `data/custom-actions.json`, `data/Resume-Template.docx`, `app/section-headings.md`
