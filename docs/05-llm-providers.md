# 05 — LLM Provider Layer

Source: `app/agents/streaming.py` (provider abstraction), `app/agents/*.py` (per-section agents). The port reimplements this in `InterviewFlow.Core/Providers` + `Core/Agents`.

## 5.1 Abstraction

One entry point (original: `iter_text_query`) that all agents call:

```csharp
IAsyncEnumerable<AgentEvent> StreamQueryAsync(
    string systemPrompt, string userPrompt,
    QueryOptions opts,          // temperature, webSearch, maxTurns
    CancellationToken ct);
```

Provider selected by `ACTIVE_PROVIDER` config. Fallback rule (match original): if unset and only `OPENAI_API_KEY` is present → `openai`, else → `anthropic`.

| Provider | Non-web path | Web-search path |
|---|---|---|
| Anthropic | Messages API streaming | Anthropic server-side web-search tool |
| OpenAI | Chat Completions streaming | Responses API with `web_search` tool |
| Gemini | google-genai generateContent streaming | Google Search grounding |
| Ollama | `/api/chat` streaming | local tool loop: DuckDuckGo search + URL fetch |

A call runs in **web mode** when the section requests tools `WebSearch`/`WebFetch` (original: `allowed_tools ∩ {WebSearch, WebFetch}` non-empty). Web-search sections: `research`, `interview_intel`, `salary`.

**SDK choice per provider** — *TBD at implementation*: official .NET SDKs where mature (Anthropic, OpenAI, Google) vs raw `HttpClient` + SSE parsing (full control over retry/usage metadata; matches openlogi-net's no-dependency bias). The Ollama path is plain HTTP either way (`POST /api/chat`, `GET /api/tags`, `POST /api/show` for tool-capability probing). Ollama web mode needs a DuckDuckGo search implementation + page fetcher in .NET.

## 5.2 Event stream contract

Events emitted to the UI (retained verbatim from the original NDJSON vocabulary — see [07-queue-and-streaming.md](07-queue-and-streaming.md) for the queue events):

```
send            {channel: "system"|"user", text}      — prompts, emitted first
tool_use        {tool: "WebSearch"|"WebFetch", input: {query}|{url,title}}
receive         {text}                                 — streamed response delta
rate_limit_reset / rate_limit_retry {remaining_seconds}
complete        {result, cost_usd, model_name, duration_ms, query_ran_at}
error           {message, detail}
canceled
heartbeat
```

Story mining's `complete` carries `stories: [...]` (parsed story objects) instead of `result`.

## 5.3 Per-section temperatures

| Sections | Temp |
|---|---|
| resume-review, decode-jd, mine-stories | 0.3 |
| anticipate-concerns, company-research, interview-intel, salary-coach | 0.5 |
| build-pitches, mock-interview, resume-chat | 0.9 |
| default (custom actions with null temperature use API default) | 0.7 |

Clamp to ≤ 1.0 for Anthropic and Ollama.

## 5.4 Pricing tables (USD per million tokens, in/out)

Baked-in constants (`streaming.py:104-152`) — port verbatim, including defaults:

- `claude-opus-4-7` (15, 75) · `claude-sonnet-4-6` (3, 15) · `claude-haiku-4-5-20251001` (0.8, 4) · Anthropic default (3, 15)
- `gpt-5.5` (5, 30) · `gpt-4o` (2.5, 10) · OpenAI default (2.5, 10)
- Gemini default (1.25, 5)
- Ollama: cost 0

> *Scaffold task*: transcribe the **complete** table from `streaming.py:104-152` (only headline entries listed here).

Cost = `in_tokens * in_price/1e6 + out_tokens * out_price/1e6`, surfaced in the `complete` event and stored per section.

## 5.5 Rate-limit handling

- **OpenAI**: parse retry hints from error messages ("try again in Xs / Xms / XmYs").
- **Anthropic**: read `Retry-After` header; default 60 s when absent.
- During the wait: emit `rate_limit_retry {remaining_seconds}` every 5 s (drives the amber countdown row in LiveTracePanel), then `rate_limit_reset` and retry.

## 5.6 Agents (Core/Agents)

Each section = one agent class: load prompt template (embedded resource, from `app/prompts/*.md` — templates use 4-backtick fenced blocks extracted by the prompt loader), substitute state fields, call the provider, post-process, write results + `cost/model/duration/ran_at` metadata into `InterviewState`.

- **Research** additionally builds a "Sources" section and prepends the `search-warning` HTML block when search failed (see 04 §4.5).
- **Story miner** parses stories out of the response into `Story[]`.
- **Mock interview / Resume chat** are multi-turn sessions (conversation held in memory; mock sessions summarized into `MockSession` on completion; `END_OF_INTERVIEW` token ends the session).
- **Custom actions** substitute `{{tags}}` (see 02 §2.2) and store into `custom_action_results[name]`.

> *Scaffold*: per-agent LLDs (prompt file, state inputs/outputs, post-processing) to be written as `docs/lld/agents/*.md` during implementation, mirroring `c:\dev\interview-flow\docs\lld\agents\`.

## 5.7 Job-posting URL fetch (decided: structured-data first, LLM extraction last, no Playwright)

If the pasted job posting matches `^https?://\S+$`, `JobPostingFetcher.ResolveAsync`
walks four steps and stops at the first that yields **≥ 200 chars** (the original's
JS-rendered-page heuristic). Everything runs behind the **SSRF guard** (reject
private / loopback / reserved / link-local IPs — ported from the original).

1. **The board's own API**, when the host has one. Cleaner than the page every
   time: no site chrome, and the employer is named outright.
   - **Workday CXS JSON** (`WorkdayPosting`) — `*.myworkdayjobs.com` job pages are
     client-rendered shells, but every tenant serves the same requisition as JSON:
     `https://{tenant}.wdN.myworkdayjobs.com/{site}/job/{rest}` →
     `…/wday/cxs/{tenant}/{site}/job/{rest}`. Tenant is the first host label; a
     locale segment (`/en-US/`) ahead of the site id is dropped, and `/details/`
     maps to the same `/job/` route. Rendered from `jobPostingInfo` (title,
     location, timeType, jobReqId, postedOn) plus root-level `hiringOrganization`.
   - **Greenhouse board API** (`GreenhousePosting`) — `…/{board}/jobs/{id}` →
     `https://boards-api.greenhouse.io/v1/boards/{board}/jobs/{id}` (EU boards use
     `boards-api.eu.…`; the embedded `?for=&token=` form maps too). The page is
     server-rendered, so scraping it "works" — but it carries no JSON-LD, names
     the employer only in `<title>`, and drags in "Back to jobs"/"Apply". The API
     gives `title`, `company_name`, `location.name` and an entity-escaped HTML
     `content` body.
2. **Plain `HttpClient` fetch**, then a **block-aware strip** (`HtmlText.PageToText`):
   block tags become newlines and `<li>` becomes a bullet, so a posting keeps its
   headings and lists. The original's flat `_html_to_text` (kept as
   `JobPostingFetcher.HtmlToText`, the parity reference) collapses an entire
   posting onto one line — that is what it used to store.
3. **Structured data inside that same HTML** (`StructuredPosting`) — schema.org
   `JobPosting` JSON-LD first, then OpenGraph `og:title`/`og:description`. This
   costs no extra request and no provider call. It matters because the tag strip
   discards `<script>` blocks, which is exactly where a JS-rendered page keeps
   its posting: the captured Workday fixture strips to **zero** characters yet
   carries the full 4.8 k-char posting as JSON-LD.
4. **One LLM query**, low temperature. With the page in hand the model is asked to
   *extract* the posting from the raw HTML (capped at 160 k chars, web tools off,
   `NO_POSTING` sentinel for a page that has none) — **not** to fetch it. Asking a
   model to retrieve a named URL does not work: no provider here exposes a
   reliable fetch-this-URL tool, and OpenAI's `web_search_preview` answers such a
   request with zero tool calls. Only when the request itself failed (no HTML at
   all) does it fall back to asking for a server-side fetch.
5. If everything fails: "Couldn't extract the posting from this page — paste the
   text instead."

Company/Position for Setup come from whichever step resolved: the board API's own
fields, JSON-LD `title`/`hiringOrganization.name`, or — as the last resort —
`StructuredPosting.CompanyFromPage`, which reads `og:site_name` and then the
document title's "… at {Company}" tail (Greenhouse writes
"Job Application for Staff Software Engineer at CareDx, Inc."). Metadata is read
even when the page body scrapes fine, since that is often the only place the
employer is named.

Caveats to encode:
- **Ollama** skips step 4 entirely: its tool loop fetches locally over plain HTTP
  (the same thing that just failed) and a page of markup overruns a local model's
  context. Steps 1–3 are what cover JS-rendered pages for local setups.
- The step-4 query has a real (small) token cost — show it in the fetch progress
  UI; *whether it's recorded into any per-section cost field is TBD (leaning:
  display-only, not persisted — the data schema has no setup-section cost fields).*
- Provider blocks (e.g. LinkedIn refusing fetchers) still fail → behavior 5.

## 5.8 Observability (decided: OpenTelemetry)

The original's optional Langfuse tracing is replaced with **OpenTelemetry**: one activity/span per agent query (attributes: provider, model, section, temperature, web-search flag, token counts, cost, duration, outcome), exported via OTLP when an endpoint is configured, no-op otherwise. Config: standard `OTEL_EXPORTER_OTLP_ENDPOINT` env conventions — *exact config surface TBD in implementation; keep it out of the Configuration UI for v1*. Langfuse `.env` keys are preserved by the config codec but ignored. The diagnostic log records per-query metadata (model, duration, cost) regardless.
