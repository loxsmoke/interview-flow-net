using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InterviewFlow.Core.Logging;

/// <summary>
/// OpenTelemetry instrumentation for agent queries (docs/05 §5.8 — replaces the
/// original's Langfuse tracing). One span per query with provider/model/cost/
/// token attributes. Exports over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is
/// set; otherwise the ActivitySource has no listener and spans cost nothing.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "InterviewFlow";

    private static readonly ActivitySource Source = new(SourceName);
    private static TracerProvider? _provider;

    /// <summary>True when an OTLP endpoint is configured and export is active.</summary>
    public static bool IsExporting { get; private set; }

    /// <summary>
    /// Starts OTLP export if configured. Safe to call once at startup; failures
    /// are swallowed — telemetry must never break the app.
    /// </summary>
    public static void Initialize(string? appVersion = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        try
        {
            _provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(SourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService("interview-flow", serviceVersion: appVersion ?? "0.0.0"))
                .AddOtlpExporter()
                .Build();
            IsExporting = true;
            DiagnosticLog.Info("telemetry", $"OTLP export enabled → {endpoint}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("telemetry", $"OTLP export unavailable: {ex.Message}");
        }
    }

    /// <summary>Opens a span for one agent query. Null when nothing is listening.</summary>
    public static Activity? StartQuery(string traceName, string provider, string model, bool webSearch)
    {
        var activity = Source.StartActivity($"agent.{traceName}", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", provider);
        activity?.SetTag("gen_ai.request.model", model);
        activity?.SetTag("interview_flow.section", traceName);
        activity?.SetTag("interview_flow.web_search", webSearch);
        return activity;
    }

    /// <summary>Records the completion metadata onto an open query span.</summary>
    public static void EndQuery(
        Activity? activity, string model, double costUsd, long durationMs,
        long inputTokens, long outputTokens, int toolUses)
    {
        if (activity is null)
            return;
        activity.SetTag("gen_ai.response.model", model);
        activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
        activity.SetTag("interview_flow.cost_usd", costUsd);
        activity.SetTag("interview_flow.duration_ms", durationMs);
        activity.SetTag("interview_flow.tool_uses", toolUses);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    public static void FailQuery(Activity? activity, string message)
    {
        activity?.SetStatus(ActivityStatusCode.Error, message);
    }

    public static void Shutdown()
    {
        try
        {
            _provider?.ForceFlush(2000);
            _provider?.Dispose();
        }
        catch
        {
            // Never let telemetry teardown break shutdown.
        }
        finally
        {
            _provider = null;
            IsExporting = false;
        }
    }
}
