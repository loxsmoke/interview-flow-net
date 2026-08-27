# 07 — Queue & Streaming

Source: `app/queue_manager.py`, NDJSON emission in `app/main.py`.

## 7.1 Queue semantics (port exactly)

- **Single slot**: at most one AI section runs at a time, across all workflows. Others wait.
- **Fixed section order** (from `queue_manager.py:15-31`): the 8 queueable sections run in the canonical order `Research, Interview Intel, Job Decoder, Resume Tailor, Story Bank, Pitch, Concerns, Salary` regardless of enqueue order. **Custom actions ARE queueable** (verified from source): section key `custom:{action_id}`, sort order 1000 — they run after all built-in sections, ordered among themselves by key. Unknown built-in keys sort at 999. Chats (mock interview, resume coach) never touch the queue.
- **In-memory only**: queue state is lost on exit — do not persist.
- Cancel: a running item transitions `running → canceling → canceled`; queued items can be dequeued (the Run button's `Don't Run AI` state).

### Queue item shape

```
{ id, state_id, section_key, title, status, queued_at, running_at, completed_at, error, error_detail }
status ∈ queued | running | canceling | canceled | failed | completed
```

## 7.2 Event flow (port architecture)

Original: FastAPI endpoints stream NDJSON; the SPA subscribes per-run and to a global queue event stream. Port (in-process): the queue manager exposes

- `IAsyncEnumerable<AgentEvent>` (or event/callback) per running job, feeding the LiveTracePanel;
- a queue-changed event carrying the `{running, queued[], failed[]}` snapshot, feeding sidebar badges and Run-button states.

Event vocabulary (identical to original NDJSON objects, minus transport):

```
send {channel, text} · tool_use {tool, input} · receive {text}
rate_limit_reset · rate_limit_retry {remaining_seconds}
complete {result|stories, cost_usd, model_name, duration_ms, query_ran_at}
error {message, detail} · canceled · heartbeat
queue {queue: {running, queued[], failed[]}}
```

## 7.3 Newline caution (from the original — replicate)

The original deliberately splits streamed text on `"\n"` only, **not** `splitlines()`, because U+2028 / U+2029 / U+0085 appear unescaped in model output and must **not** be treated as line breaks. In .NET terms: never use `string.Split` with the full Unicode newline set or `StringReader.ReadLine` on model output where line semantics matter — only `'\n'`.

## 7.4 Threading model

- Agents run on background tasks; all UI mutation marshalled via `Dispatcher.UIThread`.
- Streaming Response pane: append deltas, auto-scroll pinned to bottom (unpin on user scroll-up — match original's pin-to-bottom behavior).
- Cancellation via `CancellationTokenSource` per job, cooperative in the provider layer (emit `canceled` after the stream stops).
- Heartbeats: the original emits periodic `heartbeat` to keep HTTP alive — unnecessary in-process; keep only if it drives any UI (it doesn't — drop, but keep the enum member for contract completeness).

## 7.5 Status → UI mapping

| Signal | Sidebar tile | Run button |
|---|---|---|
| this section running | spinner | `Stop AI` / `Stopping AI...` |
| other section running | — | `Run AI Later` |
| this section queued | amber ⌛ badge | `Don't Run AI` (click dequeues) |
| failed | red `!` tile | `Run AI` + error block in view |
| completed | green ✓ | `Run AI` (re-run allowed) |
