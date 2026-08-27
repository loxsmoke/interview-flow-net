using System.Runtime.CompilerServices;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Core.Providers;

/// <summary>What an agent asks of the provider layer (docs/05 §5.1).</summary>
public sealed record QueryOptions(
    string SystemPrompt = "",
    bool UseWebSearch = false)
{
    public TemperatureSetting Temperature { get; init; } = TemperatureSetting.FromSection;
}

/// <summary>
/// The iter_text_query equivalent: emits both send events, resolves the active
/// provider and temperature, and dispatches to the provider implementation.
/// Langfuse instrumentation is replaced by OpenTelemetry later (05 §5.8);
/// DiagnosticLog captures the per-query metadata regardless.
/// </summary>
public static class ProviderRouter
{
    /// <summary>get_active_provider port: explicit setting, else the fallback rule.</summary>
    public static string ResolveProvider(AppConfig config)
    {
        var explicitProvider = config.ActiveProvider.Trim().ToLowerInvariant();
        if (explicitProvider is "anthropic" or "openai" or "gemini" or "ollama")
            return explicitProvider;
        return config.OpenAiApiKey.Length > 0 ? "openai" : "anthropic";
    }

    public static async IAsyncEnumerable<AgentEvent> StreamQueryAsync(
        AppConfig config,
        string prompt,
        QueryOptions options,
        string traceName,
        [EnumeratorCancellation] CancellationToken ct = default,
        HttpClient? http = null)
    {
        var system = options.SystemPrompt;
        yield return new SendEvent("system", system);
        yield return new SendEvent("user", prompt);

        var provider = ResolveProvider(config);
        var temperature = options.Temperature.Resolve(traceName, provider);
        var model = provider switch
        {
            "ollama" => config.OllamaModel,
            "openai" => config.OpenAiModel,
            "gemini" => config.GeminiModel,
            _ => config.AnthropicModel,
        };

        // OTel span per query (docs/05 §5.8); no-op when nothing is listening.
        using var activity = Logging.Telemetry.StartQuery(traceName, provider, model, options.UseWebSearch);

        var source = provider switch
        {
            "ollama" => new OllamaProvider(config.OllamaBaseUrl, config.OllamaNumCtx, http)
                .StreamAsync(prompt, system, config.OllamaModel, temperature, options.UseWebSearch, ct),
            "openai" => new OpenAiProvider(config.OpenAiApiKey, http)
                .StreamAsync(prompt, system, config.OpenAiModel, temperature, options.UseWebSearch, ct),
            "gemini" => new GeminiProvider(config.GeminiApiKey, http)
                .StreamAsync(prompt, system, config.GeminiModel, temperature, options.UseWebSearch, ct),
            _ => new AnthropicProvider(config.AnthropicApiKey, http)
                .StreamAsync(prompt, system, config.AnthropicModel, temperature, options.UseWebSearch, ct),
        };

        await foreach (var evt in source)
        {
            switch (evt)
            {
                case CompleteEvent complete:
                    Logging.DiagnosticLog.Info("query",
                        $"{traceName} via {provider}/{complete.ModelName}: " +
                        $"${complete.CostUsd:0.####}, {complete.DurationMs} ms, " +
                        $"{complete.InputTokens}+{complete.OutputTokens} tok, {complete.ToolUses.Count} tool uses");
                    Logging.Telemetry.EndQuery(activity, complete.ModelName, complete.CostUsd,
                        complete.DurationMs, complete.InputTokens, complete.OutputTokens, complete.ToolUses.Count);
                    break;
                case ErrorEvent error:
                    Logging.Telemetry.FailQuery(activity, error.Message);
                    break;
            }

            yield return evt;
        }
    }
}
