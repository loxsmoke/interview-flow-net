namespace InterviewFlow.Core.Agents;

// The original's NDJSON event vocabulary, kept as the typed internal contract
// (ADR-003, docs/07-queue-and-streaming.md §7.2). Heartbeat exists for contract
// completeness but is never emitted in-process.

public abstract record AgentEvent;

/// <summary>Prompt echo, emitted first. Channel: "system" | "user".</summary>
public sealed record SendEvent(string Channel, string Text) : AgentEvent;

/// <summary>Web activity. Tool: "WebSearch" (Query) or "WebFetch" (Url, Title).</summary>
public sealed record ToolUseEvent(string Tool, string Query = "", string Url = "", string Title = "") : AgentEvent;

/// <summary>Streamed response delta.</summary>
public sealed record ReceiveEvent(string Text) : AgentEvent;

/// <summary>Countdown tick (every 5 s) while waiting out a rate limit.</summary>
public sealed record RateLimitRetryEvent(int RemainingSeconds) : AgentEvent;

/// <summary>The retried stream is starting over — clear accumulated text.</summary>
public sealed record RateLimitResetEvent : AgentEvent;

/// <summary>
/// Terminal success. SearchStatus mirrors the original's Ollama-web outcome
/// classifier ("ok" | "not_searched" | "connection_error" | "no_results") and
/// drives search-warning injection; non-Ollama paths always report "ok".
/// </summary>
public sealed record CompleteEvent(
    string Text,
    double CostUsd,
    string ModelName,
    long DurationMs,
    IReadOnlyList<ToolUseEvent> ToolUses,
    long InputTokens = 0,
    long OutputTokens = 0,
    string SearchStatus = "ok") : AgentEvent
{
    /// <summary>query_ran_at — stamped by SectionRunner when the result persists.</summary>
    public string RanAt { get; init; } = "";
}

public sealed record ErrorEvent(string Message, string Detail = "") : AgentEvent;

public sealed record CanceledEvent : AgentEvent;

public sealed record HeartbeatEvent : AgentEvent;
