using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Claude via the installed Claude Code CLI (`claude -p`), authenticated by
/// whatever the CLI is signed in as — no API key. Output is read as
/// <c>stream-json</c> with partial messages, whose <c>stream_event</c> lines
/// wrap the very same Messages-API SSE events the HTTP provider parses, so
/// text deltas and WebSearch tool calls map onto the same agent events. The
/// closing <c>result</c> line carries the CLI's own cost and token accounting.
/// Temperature is not a CLI option and is ignored.
/// </summary>
public sealed class ClaudeCliProvider(string exePath, ICliRunner? runner = null)
{
    /// <summary>
    /// A system prompt longer than this rides inside the stdin prompt instead
    /// of `--system-prompt`: Windows caps the whole command line at 32 K.
    /// </summary>
    internal const int MaxSystemPromptArgLength = 24_000;

    private readonly ICliRunner _runner = runner ?? ProcessCliRunner.Default;

    internal static CliCommand BuildCommand(string exePath, string prompt, string system, string model, bool useWeb)
    {
        if (system.Length > MaxSystemPromptArgLength)
        {
            prompt = $"<system>\n{system}\n</system>\n\n{prompt}";
            system = "";
        }

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
            "--no-session-persistence",
            "--disable-slash-commands",
        };
        if (model.Length > 0)
            args.AddRange(["--model", model]);
        if (system.Length > 0)
            args.AddRange(["--system-prompt", system]);
        if (useWeb)
            args.AddRange(["--tools", "WebSearch", "--allowedTools", "WebSearch"]);
        else
            args.AddRange(["--tools", ""]);

        // Inside a Claude Code session these mark the child as nested and it
        // refuses to start; the app may well have been launched from one.
        var env = new Dictionary<string, string?>
        {
            ["CLAUDECODE"] = null,
            ["CLAUDE_CODE_ENTRYPOINT"] = null,
        };
        return new CliCommand(exePath, args, prompt, CliTools.WorkingDirectory(), env);
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, bool useWeb,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var command = BuildCommand(exePath, prompt, system, model, useWeb);
        var fullText = new StringBuilder();
        var toolUses = new List<ToolUseEvent>();
        var actualModel = model.Length > 0 ? model : "claude";
        var currentToolName = "";
        var currentToolParts = new StringBuilder();
        double cost = 0;
        long inputTokens = 0, outputTokens = 0;
        string? resultText = null, errorMessage = null, errorCode = null;
        var sw = Stopwatch.StartNew();

        await foreach (var line in CliLines.ReadAsync(_runner, command, CliTools.ClaudeProviderName, ct))
        {
            var node = CliLines.TryParse(line);
            if (node is null)
                continue;

            switch ((string?)node["type"])
            {
                case "stream_event":
                    var evt = node["event"];
                    switch ((string?)evt?["type"])
                    {
                        case "message_start":
                            actualModel = (string?)evt["message"]?["model"] ?? actualModel;
                            break;

                        case "content_block_start":
                            if ((string?)evt["content_block"]?["type"] is "tool_use" or "server_tool_use")
                            {
                                currentToolName = (string?)evt["content_block"]?["name"] ?? "";
                                currentToolParts.Clear();
                            }

                            break;

                        case "content_block_delta":
                            var deltaType = (string?)evt["delta"]?["type"];
                            if (deltaType == "text_delta")
                            {
                                var text = (string?)evt["delta"]?["text"] ?? "";
                                if (text.Length > 0)
                                {
                                    fullText.Append(text);
                                    yield return new ReceiveEvent(text);
                                }
                            }
                            else if (deltaType == "input_json_delta")
                            {
                                currentToolParts.Append((string?)evt["delta"]?["partial_json"] ?? "");
                            }

                            break;

                        case "content_block_stop":
                            if (currentToolName is "WebSearch" or "web_search" && currentToolParts.Length > 0)
                            {
                                var query = CliLines.ReadString(currentToolParts.ToString(), "query");
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
                    }

                    break;

                case "result":
                    resultText = (string?)node["result"];
                    cost = (double?)node["total_cost_usd"] ?? 0;
                    var usage = node["usage"];
                    inputTokens = ((long?)usage?["input_tokens"] ?? 0)
                        + ((long?)usage?["cache_creation_input_tokens"] ?? 0)
                        + ((long?)usage?["cache_read_input_tokens"] ?? 0);
                    outputTokens = (long?)usage?["output_tokens"] ?? 0;
                    if (node["modelUsage"] is JsonObject models && models.Count > 0)
                        actualModel = models.First().Key;
                    if ((bool?)node["is_error"] == true)
                    {
                        errorCode = (string?)node["subtype"] ?? "error";
                        errorMessage = resultText is { Length: > 0 } ? resultText : $"Claude Code reported {errorCode}.";
                    }

                    break;
            }
        }

        if (errorMessage is not null)
            throw new ProviderResponseException(errorMessage, CliTools.ClaudeProviderName, errorCode ?? "error", errorMessage);

        // A run that only produced a final message (no deltas) still has its text.
        if (fullText.Length == 0 && resultText is { Length: > 0 })
        {
            fullText.Append(resultText);
            yield return new ReceiveEvent(resultText);
        }

        yield return new CompleteEvent(
            fullText.ToString(), cost, actualModel, sw.ElapsedMilliseconds, toolUses, inputTokens, outputTokens);
    }
}

/// <summary>Shared line plumbing for the CLI providers.</summary>
internal static class CliLines
{
    /// <summary>
    /// The runner's stdout lines, with a process failure re-thrown as the
    /// provider error the queue and chat screens already know how to show.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadAsync(
        ICliRunner runner, CliCommand command, string provider,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = runner.RunAsync(command, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            string line;
            try
            {
                if (!await e.MoveNextAsync())
                    yield break;
                line = e.Current;
            }
            catch (CliProcessException ex)
            {
                var detail = LastMeaningfulLine(ex.StdErr);
                var message = detail.Length > 0 ? $"{ex.Message}: {detail}" : ex.Message;
                throw new ProviderResponseException(message, provider, "cli_failed", message);
            }

            yield return line;
        }
    }

    public static JsonNode? TryParse(string line)
    {
        if (line.Length == 0 || line[0] != '{')
            return null; // log noise the CLIs print alongside their JSON
        // Not JsonNode.Parse: Codex repeats "id" inside web_search items, and a
        // lazily-built JsonObject throws on the duplicate at first access rather
        // than at parse time. JsonDocument accepts it; rebuild keeping the first.
        try
        {
            using var doc = JsonDocument.Parse(line);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    obj.TryAdd(property.Name, FromElement(property.Value));
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    arr.Add(FromElement(item));
                return arr;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    public static string ReadString(string json, string property)
    {
        try
        {
            return (string?)JsonNode.Parse(json)?[property] ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// Codex wraps API failures as a JSON string inside its error message
    /// (`{"type":"error","status":400,"error":{"message":"…"}}`); the sentence
    /// inside is what the user should read. Anything else is returned as-is.
    /// </summary>
    public static string UnwrapApiError(string message)
    {
        try
        {
            var inner = (string?)JsonNode.Parse(message)?["error"]?["message"];
            return inner is { Length: > 0 } ? inner : message;
        }
        catch (JsonException)
        {
            return message;
        }
    }

    private static string LastMeaningfulLine(string stdErr)
    {
        var lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Prefer a plain sentence over the CLIs' timestamped log chatter.
        var plain = lines.LastOrDefault(l => !(l.StartsWith("20", StringComparison.Ordinal) && l.Contains(" ERROR ")));
        var line = plain ?? lines.LastOrDefault() ?? "";
        return line.Length > 300 ? line[..300] : line;
    }
}
