using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Anthropic Messages API over raw SSE (port of _iter_anthropic_chat/_web):
/// max_tokens 16000; web mode adds the server-side web_search_20250305 tool and
/// surfaces its queries as WebSearch tool_use events. 429s retry up to 5 times:
/// pre-stream waits the Retry-After suggestion as-is, mid-stream waits at least
/// 60 s and emits rate_limit_reset so the UI clears accumulated text.
/// </summary>
public sealed class AnthropicProvider(string apiKey, HttpClient? http = null)
{
    private const int MaxAttempts = 5;
    private readonly HttpClient _http = http ?? ProviderHttp.Default;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, double? temperature, bool useWeb,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var receivedAny = false;
            RateLimitException? rateLimited = null;

            var inner = StreamOnceAsync(prompt, system, model, temperature, useWeb, ct);
            await using (var e = inner.GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    AgentEvent? evt;
                    try
                    {
                        if (!await e.MoveNextAsync())
                            break;
                        evt = e.Current;
                    }
                    catch (RateLimitException ex)
                    {
                        if (attempt == MaxAttempts - 1)
                            throw;
                        rateLimited = ex;
                        break;
                    }

                    if (evt is ReceiveEvent or ToolUseEvent)
                        receivedAny = true;
                    yield return evt;
                    if (evt is CompleteEvent)
                        yield break;
                }
            }

            if (rateLimited is null)
                yield break; // stream ended without complete — surface as-is

            var suggested = rateLimited.SuggestedWaitSeconds ?? 60.0;
            var wait = receivedAny ? Math.Max(suggested, 60.0) : suggested;
            await foreach (var hb in ProviderHttp.WaitWithHeartbeatsAsync(wait, ct))
                yield return hb;
            if (receivedAny)
                yield return new RateLimitResetEvent();
        }
    }


    /// <summary>
    /// Models that still accept `temperature`. Opus 4.7+, Opus 4.8, Opus 5,
    /// Sonnet 5, and Fable 5 removed the sampling parameters and reject the
    /// field outright; Opus 4.6, Sonnet 4.6, Haiku 4.5 and older accept it.
    /// Unknown ids are assumed new — omitting temperature costs a little
    /// determinism, sending it to a model that refuses it costs the whole run.
    /// </summary>
    internal static bool AcceptsTemperature(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        return id.StartsWith("claude-sonnet-4-", StringComparison.Ordinal)
            || id.StartsWith("claude-haiku-", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-4-6", StringComparison.Ordinal)
            || id.StartsWith("claude-opus-4-5", StringComparison.Ordinal)
            || id.StartsWith("claude-3", StringComparison.Ordinal);
    }

    private async IAsyncEnumerable<AgentEvent> StreamOnceAsync(
        string prompt, string system, string model, double? temperature, bool useWeb,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 16000,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = prompt,
            }),
        };
        // Claude 4.7 and later removed the sampling parameters: sending
        // temperature to them is a 400, not a no-op (docs/05 §5.9).
        if (temperature is not null && AcceptsTemperature(model))
            body["temperature"] = temperature.Value;
        if (system.Length > 0)
            body["system"] = system;
        if (useWeb)
            body["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_20250305",
                ["name"] = "web_search",
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);

        var fullText = new StringBuilder();
        var toolUses = new List<ToolUseEvent>();
        long inputTokens = 0, outputTokens = 0;
        var actualModel = model;
        var currentToolName = "";
        var currentToolParts = new StringBuilder();
        var sw = Stopwatch.StartNew();

        await foreach (var node in ProviderHttp.ReadSseJsonAsync(response, ct))
        {
            switch ((string?)node["type"])
            {
                case "message_start":
                    actualModel = (string?)node["message"]?["model"] ?? model;
                    inputTokens = (long?)node["message"]?["usage"]?["input_tokens"] ?? 0;
                    break;

                case "content_block_start":
                    if ((string?)node["content_block"]?["type"] is "tool_use" or "server_tool_use")
                    {
                        currentToolName = (string?)node["content_block"]?["name"] ?? "";
                        currentToolParts.Clear();
                    }

                    break;

                case "content_block_delta":
                    var deltaType = (string?)node["delta"]?["type"];
                    if (deltaType == "text_delta")
                    {
                        var text = (string?)node["delta"]?["text"] ?? "";
                        if (text.Length > 0)
                        {
                            fullText.Append(text);
                            yield return new ReceiveEvent(text);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        currentToolParts.Append((string?)node["delta"]?["partial_json"] ?? "");
                    }

                    break;

                case "content_block_stop":
                    if (currentToolName == "web_search" && currentToolParts.Length > 0)
                    {
                        var query = TryReadQuery(currentToolParts.ToString());
                        if (query.Length > 0)
                        {
                            var entry = new ToolUseEvent("WebSearch", Query: query);
                            toolUses.Add(entry);
                            yield return entry;
                        }
                    }

                    currentToolName = "";
                    currentToolParts.Clear();
                    break;

                case "message_delta":
                    outputTokens = (long?)node["usage"]?["output_tokens"] ?? outputTokens;
                    break;
            }
        }

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.AnthropicCost(actualModel, inputTokens, outputTokens),
            actualModel,
            sw.ElapsedMilliseconds,
            toolUses,
            inputTokens,
            outputTokens);
    }

    private static string TryReadQuery(string json)
    {
        try
        {
            return (string?)JsonNode.Parse(json)?["query"] ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
