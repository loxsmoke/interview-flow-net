using System.Text;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Core.Providers;

/// <summary>One turn in a chat transcript. Role: system | user | assistant.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Single-turn, non-streaming chat completion across all four providers —
/// what the original's chat sessions use (mock_interview.py / resume_chat.py
/// call messages.create / chat.completions.create rather than the streaming
/// paths). max_tokens 8192. OpenAI retries once on 429 after twice the
/// suggested wait, matching the original's "non-streaming, safe to retry".
/// </summary>
public static class ChatProvider
{
    public static Task<string> CompleteAsync(
        AppConfig config, IReadOnlyList<ChatMessage> messages, double? temperature,
        CancellationToken ct = default, HttpClient? http = null)
    {
        var provider = ProviderRouter.ResolveProvider(config);
        var resolved = temperature;
        if (resolved is not null && provider is "anthropic" or "ollama")
            resolved = Math.Min(1.0, resolved.Value);

        return provider switch
        {
            "openai" => OpenAiAsync(config, messages, resolved, ct, http),
            "gemini" => GeminiAsync(config, messages, resolved, ct, http),
            "ollama" => OllamaAsync(config, messages, resolved, ct, http),
            _ => AnthropicAsync(config, messages, resolved, ct, http),
        };
    }

    private static (string System, List<ChatMessage> Messages) SplitSystem(IReadOnlyList<ChatMessage> messages)
    {
        var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        return (system, messages.Where(m => m.Role != "system").ToList());
    }

    private static async Task<string> AnthropicAsync(
        AppConfig config, IReadOnlyList<ChatMessage> messages, double? temperature,
        CancellationToken ct, HttpClient? http)
    {
        var (system, rest) = SplitSystem(messages);
        var body = new JsonObject
        {
            ["model"] = config.AnthropicModel,
            ["max_tokens"] = 8192,
            ["messages"] = new JsonArray(rest
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };
        if (temperature is not null)
            body["temperature"] = temperature.Value;
        if (system.Length > 0)
            body["system"] = system;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", config.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await (http ?? ProviderHttp.Default).SendAsync(request, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return (string?)json?["content"]?[0]?["text"] ?? "";
    }

    private static async Task<string> OpenAiAsync(
        AppConfig config, IReadOnlyList<ChatMessage> messages, double? temperature,
        CancellationToken ct, HttpClient? http)
    {
        var body = new JsonObject
        {
            ["model"] = config.OpenAiModel,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };
        if (temperature is not null)
            body["temperature"] = temperature.Value;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {config.OpenAiApiKey}");
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await (http ?? ProviderHttp.Default).SendAsync(request, ct);
                await ProviderHttp.EnsureSuccessAsync(response, ct);
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                return (string?)json?["choices"]?[0]?["message"]?["content"] ?? "";
            }
            catch (RateLimitException ex) when (attempt == 0 && ex.SuggestedWaitSeconds is { } wait)
            {
                // Non-streaming call — safe to retry after twice the hint.
                await Task.Delay(TimeSpan.FromSeconds(wait * 2), ct);
            }
        }

        return "";
    }

    private static async Task<string> GeminiAsync(
        AppConfig config, IReadOnlyList<ChatMessage> messages, double? temperature,
        CancellationToken ct, HttpClient? http)
    {
        var (system, rest) = SplitSystem(messages);
        var contents = new JsonArray(rest
            .Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Content }),
            })
            .ToArray());

        var generationConfig = new JsonObject { ["maxOutputTokens"] = 8192 };
        if (temperature is not null)
            generationConfig["temperature"] = temperature.Value;
        var body = new JsonObject { ["contents"] = contents, ["generationConfig"] = generationConfig };
        if (system.Length > 0)
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", config.GeminiApiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await (http ?? ProviderHttp.Default).SendAsync(request, ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var parts = json?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        return parts is null ? "" : string.Concat(parts.Select(p => (string?)p?["text"] ?? ""));
    }

    private static async Task<string> OllamaAsync(
        AppConfig config, IReadOnlyList<ChatMessage> messages, double? temperature,
        CancellationToken ct, HttpClient? http)
    {
        var options = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.OllamaNumCtx) && int.TryParse(config.OllamaNumCtx, out var ctx))
            options["num_ctx"] = ctx;
        if (temperature is not null)
            options["temperature"] = temperature.Value;

        var body = new JsonObject
        {
            ["model"] = config.OllamaModel,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
            ["stream"] = false,
        };
        if (options.Count > 0)
            body["options"] = options;

        var url = $"{config.OllamaBaseUrl.TrimEnd('/')}/api/chat";
        using var response = await (http ?? ProviderHttp.Default).PostAsync(
            url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return (string?)json?["message"]?["content"] ?? "";
    }
}
