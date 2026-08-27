using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Gemini generateContent over SSE (port of _iter_gemini_chat/_web):
/// max_output_tokens 16000; web mode adds Google Search grounding. Like the
/// original, no retry loop and no tool_use surfacing (grounding is server-side
/// and opaque). Also hosts the live model listing used by the config screen.
/// </summary>
public sealed class GeminiProvider(string apiKey, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? ProviderHttp.Default;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, double? temperature, bool useWeb,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var generationConfig = new JsonObject { ["maxOutputTokens"] = 16000 };
        if (temperature is not null)
            generationConfig["temperature"] = temperature.Value;

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["generationConfig"] = generationConfig,
        };
        if (system.Length > 0)
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            };
        if (useWeb)
            body["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);

        var fullText = new StringBuilder();
        long inputTokens = 0, outputTokens = 0;
        var sw = Stopwatch.StartNew();

        await foreach (var node in ProviderHttp.ReadSseJsonAsync(response, ct))
        {
            var candidates = node["candidates"] as JsonArray;
            var parts = candidates is { Count: > 0 }
                ? candidates[0]?["content"]?["parts"] as JsonArray
                : null;
            if (parts is not null)
            {
                foreach (var part in parts)
                {
                    var text = (string?)part?["text"] ?? "";
                    if (text.Length > 0)
                    {
                        fullText.Append(text);
                        yield return new ReceiveEvent(text);
                    }
                }
            }

            if (node["usageMetadata"] is JsonObject um)
            {
                inputTokens = (long?)um["promptTokenCount"] ?? inputTokens;
                outputTokens = (long?)um["candidatesTokenCount"] ?? outputTokens;
            }
        }

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.GeminiCost(model, inputTokens, outputTokens),
            model,
            sw.ElapsedMilliseconds,
            [],
            inputTokens,
            outputTokens);
    }

    /// <summary>
    /// generateContent-capable gemini-* models, stable ids first (port of
    /// main.py get_gemini_models). Returns (id, displayName) rows.
    /// </summary>
    public async Task<List<(string Id, string DisplayName)>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
        request.Headers.Add("x-goog-api-key", apiKey);
        using var response = await _http.SendAsync(request, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var models = new List<(string Id, string DisplayName)>();
        if (root?["models"] is not JsonArray list)
            return models;

        foreach (var m in list)
        {
            var name = (string?)m?["name"] ?? "";
            var id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;
            if (!id.StartsWith("gemini-", StringComparison.Ordinal))
                continue;
            var actions = m?["supportedGenerationMethods"] as JsonArray;
            if (actions is null || !actions.Any(a => (string?)a == "generateContent"))
                continue;
            var display = (string?)m?["displayName"] ?? id;
            models.Add((id, display));
        }

        return models
            .OrderBy(m => m.Id.Contains("preview") || m.Id.Contains("exp") || m.Id.Contains("latest"))
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }
}
