# ADR-003 — In-Process Services vs. Retained HTTP Layer

**Status:** Accepted — **Option A (fully in-process)**, decided by the user 2026-08-26.
**Drives:** [01-architecture.md](../01-architecture.md), [07-queue-and-streaming.md](../07-queue-and-streaming.md).

## Context

The original is a localhost FastAPI server + browser SPA in a pywebview shell; all UI↔logic traffic is HTTP + NDJSON streams. The port is a native Avalonia app, which does not need a network boundary between UI and logic. However, the original's E2E tests (Playwright + mock server) and its event vocabulary are built around that boundary.

## Options

### A. Fully in-process (recommended)

ViewModels call Core services directly; streaming = `IAsyncEnumerable<AgentEvent>`; the NDJSON event **vocabulary** survives as the typed internal contract, minus transport (heartbeats become unnecessary).

- **+** Simpler, no port management, no CORS/SSRF-on-localhost concerns, natural cancellation via `CancellationToken`, testable with plain xunit.
- **−** Original E2E harness not reusable; any future remote/web frontend would need the boundary rebuilt.

### B. Keep a localhost HTTP layer inside the .NET app

Kestrel serving the same routes; Avalonia UI as a client.

- **+** Route-level parity testing against the original; conceivable UI reuse.
- **−** Two serialization hops, port/firewall issues, much more code, no user-visible benefit.

## Decision

**Option A.** The HTTP surface was an implementation detail of the Python/browser split, not a product feature. Parity is asserted at the data-format and rendering level instead (M1/M3 acceptance tests), and the `AgentEvent`/queue-event contracts (07) preserve the semantics the UI depends on.

## Consequences

- `AgentEvent` union type in Core mirrors the NDJSON vocabulary 1:1 (documented in 05/07), keeping the original docs and this port's UI logic in obvious correspondence.
- The `heartbeat` event exists in the enum for contract completeness but is never emitted.
- E2E-style tests are rewritten as ViewModel-level tests plus Avalonia headless UI tests (*TBD: `Avalonia.Headless.XUnit`*).
