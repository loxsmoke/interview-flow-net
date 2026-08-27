using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Local Ollama over /api/chat (port of _iter_ollama_chat/_web): NDJSON
/// streaming for plain queries; a 20-turn tool loop with DuckDuckGo search +
/// URL fetch for web mode, non-streaming per turn so tool_calls arrive whole,
/// with a streamed synthesis fallback when the model searched but wrote
/// nothing. Always $0; search outcomes are classified into search_status for
/// the search-warning banner. Also hosts /api/tags + /api/show model probing.
/// </summary>
public sealed class OllamaProvider(string baseUrl, string? numCtx = null, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? ProviderHttp.Default;
    private readonly string _chatUrl = $"{baseUrl.TrimEnd('/')}/api/chat";

    private JsonObject? BuildOptions(double? temperature)
    {
        var options = new JsonObject();
        if (!string.IsNullOrWhiteSpace(numCtx) && int.TryParse(numCtx, out var ctx))
            options["num_ctx"] = ctx;
        if (temperature is not null)
            options["temperature"] = temperature.Value;
        return options.Count > 0 ? options : null;
    }

    public IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, double? temperature, bool useWeb,
        CancellationToken ct = default)
        => useWeb
            ? StreamWebAsync(prompt, system, model, temperature, ct)
            : StreamChatAsync(prompt, system, model, temperature, ct);

    // ── Plain streaming chat ─────────────────────────────────────────────────

    private async IAsyncEnumerable<AgentEvent> StreamChatAsync(
        string prompt, string system, string model, double? temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = BuildMessages(prompt, system);
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = true };
        if (BuildOptions(temperature) is { } opts)
            body["options"] = opts;

        var fullText = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var response = await PostStreamingAsync(body, ct);
        await foreach (var text in ReadNdjsonContentAsync(response, ct))
        {
            fullText.Append(text);
            yield return new ReceiveEvent(text);
        }

        yield return new CompleteEvent(fullText.ToString(), 0.0, model, sw.ElapsedMilliseconds, []);
    }

    // ── Tool-calling web loop ────────────────────────────────────────────────

    private static readonly JsonArray WebToolsTemplate = (JsonArray)JsonNode.Parse(
        """
        [
          { "type": "function", "function": { "name": "web_search",
            "description": "Search the web for current information such as company details, salary data, interview experiences, and news.",
            "parameters": { "type": "object", "properties": { "query": { "type": "string", "description": "The search query" } }, "required": ["query"] } } },
          { "type": "function", "function": { "name": "fetch_url",
            "description": "Fetch and read the text content of a webpage URL.",
            "parameters": { "type": "object", "properties": { "url": { "type": "string", "description": "The full URL to fetch" } }, "required": ["url"] } } }
        ]
        """)!;

    private async IAsyncEnumerable<AgentEvent> StreamWebAsync(
        string prompt, string system, string model, double? temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = BuildMessages(prompt, system);
        var toolUses = new List<ToolUseEvent>();
        var fullText = new StringBuilder();
        int searchesDone = 0, searchesFailed = 0, searchesEmpty = 0;
        var opts = BuildOptions(temperature);
        var sw = Stopwatch.StartNew();

        for (var turn = 0; turn < 20; turn++)
        {
            // Non-streaming so tool_calls arrive as one complete object.
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = WebToolsTemplate.DeepClone(),
                ["stream"] = false,
            };
            if (opts is not null)
                body["options"] = opts.DeepClone();

            using var response = await PostStreamingAsync(body, ct);
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var msg = data?["message"] as JsonObject ?? [];
            var content = (string?)msg["content"] ?? "";
            var toolCalls = msg["tool_calls"] as JsonArray;

            if (content.Length > 0)
            {
                fullText.Append(content);
                yield return new ReceiveEvent(content);
            }

            if (toolCalls is null || toolCalls.Count == 0)
                break;

            messages.Add(msg.DeepClone());

            foreach (var tc in toolCalls)
            {
                var fn = tc?["function"];
                var fnName = (string?)fn?["name"] ?? "";
                var argsNode = fn?["arguments"];
                var args = argsNode switch
                {
                    JsonObject o => o,
                    JsonValue v when v.TryGetValue<string>(out var s) => TryParseObject(s),
                    _ => [],
                };

                string result;
                if (fnName == "web_search")
                {
                    var query = (string?)args["query"] ?? "";
                    var entry = new ToolUseEvent("WebSearch", Query: query);
                    toolUses.Add(entry);
                    yield return entry;
                    result = await WebTools.SearchDuckDuckGoAsync(query, _http, ct);
                    searchesDone++;
                    if (result.StartsWith("Web search failed", StringComparison.Ordinal))
                        searchesFailed++;
                    else if (result.StartsWith("No results found", StringComparison.Ordinal))
                        searchesEmpty++;
                }
                else if (fnName == "fetch_url")
                {
                    var fetchUrl = (string?)args["url"] ?? "";
                    var entry = new ToolUseEvent("WebFetch", Url: fetchUrl);
                    toolUses.Add(entry);
                    yield return entry;
                    result = await WebTools.FetchUrlAsync(fetchUrl, _http, ct);
                }
                else
                {
                    result = $"Unknown tool: {fnName}";
                }

                messages.Add(new JsonObject { ["role"] = "tool", ["content"] = result });
            }
        }

        // Synthesis fallback: the model searched but never wrote anything.
        if (fullText.Length == 0 && toolUses.Count > 0)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Based on your research above, please write your complete analysis and findings now.",
            });
            var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = true };
            if (opts is not null)
                body["options"] = opts.DeepClone();

            using var response = await PostStreamingAsync(body, ct);
            await foreach (var text in ReadNdjsonContentAsync(response, ct))
            {
                fullText.Append(text);
                yield return new ReceiveEvent(text);
            }
        }

        yield return new CompleteEvent(
            fullText.ToString(), 0.0, model, sw.ElapsedMilliseconds, toolUses,
            SearchStatus: ClassifySearchStatus(searchesDone, searchesFailed, searchesEmpty));
    }

    /// <summary>Outcome classifier for the search-warning banner (streaming.py:473-482).</summary>
    public static string ClassifySearchStatus(int done, int failed, int empty)
    {
        if (done == 0)
            return "not_searched";
        if (done - failed - empty > 0)
            return "ok";
        return failed > 0 ? "connection_error" : "no_results";
    }

    // ── Model probing (config screen) ────────────────────────────────────────

    public async Task<List<(string Name, bool SupportsTools)>> ListModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new List<(string, bool)>();
        if (root?["models"] is not JsonArray models)
            return result;
        foreach (var m in models)
        {
            var name = (string?)m?["name"] ?? "";
            if (name.Length > 0)
                result.Add((name, await SupportsToolsAsync(name, ct)));
        }

        return result;
    }

    private async Task<bool> SupportsToolsAsync(string name, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject { ["name"] = name };
            using var response = await _http.PostAsync(
                $"{baseUrl.TrimEnd('/')}/api/show",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var d = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (d?["capabilities"] is JsonArray caps && caps.Count > 0)
                return caps.Any(c => (string?)c == "tools");
            // Older Ollama: check the template for tool handling as a fallback.
            return ((string?)d?["template"] ?? "").Contains("tool", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static JsonArray BuildMessages(string prompt, string system)
    {
        var messages = new JsonArray();
        if (system.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
        return messages;
    }

    private async Task<HttpResponseMessage> PostStreamingAsync(JsonObject body, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            _chatUrl,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        await ProviderHttp.EnsureSuccessAsync(response, ct);
        return response;
    }

    private static async IAsyncEnumerable<string> ReadNdjsonContentAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in ProviderHttp.ReadLinesAsync(response, ct))
        {
            if (line.Length == 0)
                continue;
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var content = (string?)chunk?["message"]?["content"] ?? "";
            if (content.Length > 0)
                yield return content;
        }
    }

    private static JsonObject TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
