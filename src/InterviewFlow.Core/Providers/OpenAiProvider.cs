using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// OpenAI over raw SSE (port of _iter_openai_chat/_responses): Chat Completions
/// for plain queries; the Responses API with web_search_preview for web mode
/// (surfacing search calls and url_citation annotations as tool_use events,
/// max_output_tokens 8000; the Responses path ignores temperature, like the
/// original). Rate-limit retries: hint parsed from the error message; pre-stream
/// floor 15·2^attempt capped at 60 s, mid-stream at least 60 s + reset event.
/// Transient stream errors back off 5·2^attempt capped at 60 s.
/// </summary>
public sealed class OpenAiProvider(string apiKey, HttpClient? http = null)
{
    private const int MaxAttempts = 5;
    private readonly HttpClient _http = http ?? ProviderHttp.Default;

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
        if (temperature is not null)
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
            ["max_output_tokens"] = 8000,
            ["stream"] = true,
        };

        using var response = await PostAsync("https://api.openai.com/v1/responses", body, ct);

        var fullText = new StringBuilder();
        var toolUses = new List<ToolUseEvent>();
        long promptTokens = 0, completionTokens = 0;
        var actualModel = model;
        var sawCompleted = false;
        var sw = Stopwatch.StartNew();

        await foreach (var node in ProviderHttp.ReadSseJsonAsync(response, ct))
        {
            switch ((string?)node["type"])
            {
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
                    actualModel = (string?)final?["model"] ?? model;
                    promptTokens = (long?)final?["usage"]?["input_tokens"] ?? 0;
                    completionTokens = (long?)final?["usage"]?["output_tokens"] ?? 0;

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

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.OpenAiCost(actualModel, promptTokens, completionTokens),
            actualModel,
            sw.ElapsedMilliseconds,
            toolUses,
            promptTokens,
            completionTokens);
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
