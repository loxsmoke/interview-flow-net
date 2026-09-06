using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// OpenAI over raw SSE (port of _iter_openai_chat/_responses): Chat Completions
/// for plain queries; the Responses API with web_search_preview for web mode
/// (surfacing search calls and url_citation annotations as tool_use events;
/// the Responses path ignores temperature, like the original). Rate-limit
/// retries: hint parsed from the error message; pre-stream floor 15·2^attempt
/// capped at 60 s, mid-stream at least 60 s + reset event. Transient stream
/// errors back off 5·2^attempt capped at 60 s.
/// <para>
/// The GPT-5 line and the o-series are reasoning models: they reject any
/// <c>temperature</c> but the default with a 400 (<see cref="AcceptsTemperature"/>
/// gates the field, as the Anthropic provider does for Claude 4.7+), and their
/// hidden reasoning tokens count against <c>max_output_tokens</c>, so they get
/// twice the budget of the GPT-4 line. A Responses stream that ends with
/// <c>response.incomplete</c>, <c>response.failed</c> or <c>error</c> reports
/// that reason instead of a generic "stream ended".
/// </para>
/// </summary>
public sealed class OpenAiProvider(string apiKey, HttpClient? http = null)
{
    private const int MaxAttempts = 5;
    private const int Gpt4OutputTokens = 8000;
    private const int ReasoningOutputTokens = 16000;
    private readonly HttpClient _http = http ?? ProviderHttp.Default;

    /// <summary>
    /// True for the models that still take a sampling temperature: the GPT-4
    /// and GPT-3.5 lines. GPT-5.x and the o-series answer any value but the
    /// default with a 400, and an id we've never seen is assumed to be a newer
    /// reasoning model — omitting temperature costs a little determinism,
    /// sending it to a model that refuses it costs the whole run.
    /// </summary>
    internal static bool AcceptsTemperature(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        return id.StartsWith("gpt-4", StringComparison.Ordinal)
            || id.StartsWith("gpt-3.5", StringComparison.Ordinal)
            || id.StartsWith("chatgpt-4o", StringComparison.Ordinal);
    }

    /// <summary>
    /// Output budget for a web-mode run. Reasoning models spend part of it on
    /// reasoning the caller never sees, so a research answer that fits the
    /// GPT-4 budget can come back cut short on GPT-5.
    /// </summary>
    internal static int MaxOutputTokens(string model) =>
        AcceptsTemperature(model) ? Gpt4OutputTokens : ReasoningOutputTokens;

    public IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, double? temperature, bool useWeb,
        CancellationToken ct = default)
        => useWeb
            ? WithRetries(() => StreamResponsesOnceAsync(prompt, system, model, ct), transientRetries: true, ct)
            : WithRetries(() => StreamChatOnceAsync(prompt, system, model, temperature, ct), transientRetries: false, ct);

    private async IAsyncEnumerable<AgentEvent> WithRetries(
        Func<IAsyncEnumerable<AgentEvent>> source, bool transientRetries,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var receivedAny = false;
            double? wait = null;
            var inner = source();
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
                        var suggested = ex.SuggestedWaitSeconds;
                        if (receivedAny)
                        {
                            wait = Math.Max(suggested ?? 60.0, 60.0);
                        }
                        else
                        {
                            // Exponential floor 15s, 30s, 60s, 60s — lets the
                            // org-level token window clear (streaming.py:333).
                            var floor = Math.Min(15.0 * Math.Pow(2, attempt), 60.0);
                            wait = Math.Max(suggested ?? floor, floor);
                        }

                        break;
                    }
                    catch (HttpRequestException) when (transientRetries && attempt < MaxAttempts - 1)
                    {
                        wait = Math.Min(5.0 * Math.Pow(2, attempt), 60.0);
                        break;
                    }
                    catch (IOException) when (transientRetries && attempt < MaxAttempts - 1)
                    {
                        wait = Math.Min(5.0 * Math.Pow(2, attempt), 60.0);
                        break;
                    }

                    if (evt is ReceiveEvent or ToolUseEvent)
                        receivedAny = true;
                    yield return evt;
                    if (evt is CompleteEvent)
                        yield break;
                }
            }

            if (wait is null)
                yield break;

            await foreach (var hb in ProviderHttp.WaitWithHeartbeatsAsync(wait.Value, ct))
                yield return hb;
            if (receivedAny)
                yield return new RateLimitResetEvent();
        }
    }

    // ── Chat Completions (no web) ────────────────────────────────────────────

    private async IAsyncEnumerable<AgentEvent> StreamChatOnceAsync(
        string prompt, string system, string model, double? temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        if (system.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (temperature is not null && AcceptsTemperature(model))
            body["temperature"] = temperature.Value;

        using var response = await PostAsync("https://api.openai.com/v1/chat/completions", body, ct);

        var fullText = new StringBuilder();
        long promptTokens = 0, completionTokens = 0;
        var actualModel = model;
        var sw = Stopwatch.StartNew();

        await foreach (var node in ProviderHttp.ReadSseJsonAsync(response, ct))
        {
            var choices = node["choices"] as System.Text.Json.Nodes.JsonArray;
            var delta = choices is { Count: > 0 } ? (string?)choices[0]?["delta"]?["content"] : null;
            if (!string.IsNullOrEmpty(delta))
            {
                fullText.Append(delta);
                yield return new ReceiveEvent(delta);
            }

            if ((string?)node["model"] is { Length: > 0 } m)
                actualModel = m;
            if (node["usage"] is JsonObject usage)
            {
                promptTokens = (long?)usage["prompt_tokens"] ?? 0;
                completionTokens = (long?)usage["completion_tokens"] ?? 0;
            }
        }

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.OpenAiCost(actualModel, promptTokens, completionTokens),
            actualModel,
            sw.ElapsedMilliseconds,
            [],
            promptTokens,
            completionTokens);
    }

    // ── Responses API with web_search_preview (web mode) ─────────────────────

    private async IAsyncEnumerable<AgentEvent> StreamResponsesOnceAsync(
        string prompt, string system, string model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var input = new JsonArray();
        if (system.Length > 0)
            input.Add(new JsonObject { ["role"] = "developer", ["content"] = system });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        var body = new JsonObject
        {
            ["model"] = model,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search_preview" }),
            ["input"] = input,
            ["max_output_tokens"] = MaxOutputTokens(model),
            ["stream"] = true,
        };

        using var response = await PostAsync("https://api.openai.com/v1/responses", body, ct);

        var fullText = new StringBuilder();
        var toolUses = new List<ToolUseEvent>();
        long promptTokens = 0, completionTokens = 0;
        var actualModel = model;
        var sawCompleted = false;
        var cutShort = "";
        var sw = Stopwatch.StartNew();

        await foreach (var node in ProviderHttp.ReadSseJsonAsync(response, ct))
        {
            switch ((string?)node["type"])
            {
                // Terminal failures. The stream closes right after these, and
                // without handling them the only symptom was "stream ended
                // before response.completed" — retried as transient, reason lost.
                case "response.failed":
                    throw new ProviderResponseException(
                        "OpenAI response failed: " + Describe(node["response"]?["error"]));

                case "error":
                    throw new ProviderResponseException("OpenAI stream error: " + Describe(node));

                case "response.incomplete":
                    // Usually max_output_tokens on a reasoning model. Whatever
                    // text arrived is still the answer so far; keep it and say
                    // it was cut short rather than throwing the run away.
                    var reason = (string?)node["response"]?["incomplete_details"]?["reason"] ?? "unknown";
                    if (fullText.Length == 0)
                        throw new ProviderResponseException($"OpenAI response incomplete ({reason}) with no output");
                    Logging.DiagnosticLog.Warn("openai", $"response incomplete ({reason}); keeping partial text");
                    cutShort = reason;
                    sawCompleted = true;
                    ReadUsage(node["response"], ref actualModel, ref promptTokens, ref completionTokens);
                    break;

                case "response.output_item.added":
                    if ((string?)node["item"]?["type"] == "web_search_call")
                    {
                        // The query lives on item.query in the original SDK's view;
                        // the wire also nests it under item.action.query.
                        var q = (string?)node["item"]?["query"]
                            ?? (string?)node["item"]?["action"]?["query"] ?? "";
                        if (q.Length > 0)
                        {
                            var entry = new ToolUseEvent("WebSearch", Query: q);
                            toolUses.Add(entry);
                            yield return entry;
                        }
                    }

                    break;

                case "response.output_text.delta":
                    var delta = (string?)node["delta"] ?? "";
                    if (delta.Length > 0)
                    {
                        fullText.Append(delta);
                        yield return new ReceiveEvent(delta);
                    }

                    break;

                case "response.completed":
                    sawCompleted = true;
                    var final = node["response"];
                    ReadUsage(final, ref actualModel, ref promptTokens, ref completionTokens);

                    // Citation URLs from the completed response → WebFetch entries.
                    if (final?["output"] is JsonArray output)
                    {
                        foreach (var item in output)
                        {
                            if ((string?)item?["type"] != "message" || item?["content"] is not JsonArray content)
                                continue;
                            foreach (var block in content)
                            {
                                if (block?["annotations"] is not JsonArray annotations)
                                    continue;
                                foreach (var annotation in annotations)
                                {
                                    if ((string?)annotation?["type"] != "url_citation")
                                        continue;
                                    var url = (string?)annotation?["url"] ?? "";
                                    var title = (string?)annotation?["title"] ?? "";
                                    if (url.Length > 0 && !toolUses.Any(t => t.Url == url))
                                    {
                                        var entry = new ToolUseEvent("WebFetch", Url: url, Title: title);
                                        toolUses.Add(entry);
                                        yield return entry;
                                    }
                                }
                            }
                        }
                    }

                    break;
            }
        }

        if (!sawCompleted)
            throw new IOException("stream ended before response.completed");

        if (cutShort.Length > 0)
            fullText.Append("\n\n_Response cut short by the model's output limit (").Append(cutShort).Append(")._");

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.OpenAiCost(actualModel, promptTokens, completionTokens),
            actualModel,
            sw.ElapsedMilliseconds,
            toolUses,
            promptTokens,
            completionTokens);
    }

    private static void ReadUsage(JsonNode? response, ref string model, ref long promptTokens, ref long completionTokens)
    {
        model = (string?)response?["model"] ?? model;
        promptTokens = (long?)response?["usage"]?["input_tokens"] ?? 0;
        completionTokens = (long?)response?["usage"]?["output_tokens"] ?? 0;
    }

    /// <summary>"code: message" from an error object, or the raw node.</summary>
    private static string Describe(JsonNode? error)
    {
        if (error is null)
            return "no detail";
        var code = (string?)error["code"] ?? "";
        var message = (string?)error["message"] ?? "";
        if (message.Length == 0)
            return error.ToJsonString();
        return code.Length > 0 ? $"{code}: {message}" : message;
    }

    private async Task<HttpResponseMessage> PostAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        return response;
    }
}
