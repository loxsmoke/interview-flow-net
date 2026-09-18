using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// OpenAI via the installed Codex CLI (`codex exec --json`), signed in as the
/// CLI is — no API key. Its JSONL carries whole items, not deltas, so each
/// agent message arrives as one receive event; web search (`web_search =
/// "live"`) surfaces as WebSearch tool events. Codex has no system-prompt
/// option: the section's system prompt leads the stdin prompt as an
/// instructions block. The sandbox is read-only and the working directory is
/// ours, so the model has nothing to touch. Temperature is ignored. Cost is
/// computed from the token counts at OpenAI list prices — what the run would
/// have cost by the token, whether or not a subscription covered it.
/// </summary>
public sealed class CodexCliProvider(string exePath, ICliRunner? runner = null)
{
    private readonly ICliRunner _runner = runner ?? ProcessCliRunner.Default;

    internal static CliCommand BuildCommand(string exePath, string prompt, string system, string model, bool useWeb)
    {
        var args = new List<string>
        {
            "exec",
            "--json",
            "--ephemeral",
            "--skip-git-repo-check",
            "--sandbox", "read-only",
            "--color", "never",
        };
        if (model.Length > 0)
            args.AddRange(["--model", model]);
        if (useWeb)
            args.AddRange(["-c", "web_search=\"live\""]);
        args.Add("-"); // prompt from stdin

        var stdin = system.Length > 0
            ? $"<instructions>\n{system}\n</instructions>\n\n{prompt}"
            : prompt;
        return new CliCommand(exePath, args, stdin, CliTools.WorkingDirectory(), new Dictionary<string, string?>());
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string prompt, string system, string model, bool useWeb,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var command = BuildCommand(exePath, prompt, system, model, useWeb);
        var modelName = model.Length > 0 ? model : CliTools.CodexDefaultModel();
        if (modelName.Length == 0)
            modelName = "codex";

        var fullText = new StringBuilder();
        var toolUses = new List<ToolUseEvent>();
        long inputTokens = 0, outputTokens = 0;
        string? errorMessage = null;
        var sw = Stopwatch.StartNew();

        await foreach (var line in CliLines.ReadAsync(_runner, command, CliTools.CodexProviderName, ct))
        {
            var node = CliLines.TryParse(line);
            if (node is null)
                continue;

            switch ((string?)node["type"])
            {
                case "item.completed":
                    var item = node["item"];
                    switch ((string?)item?["type"])
                    {
                        case "agent_message":
                            var text = (string?)item["text"] ?? "";
                            if (text.Length == 0)
                                break;
                            if (fullText.Length > 0)
                            {
                                fullText.Append("\n\n");
                                yield return new ReceiveEvent("\n\n");
                            }

                            fullText.Append(text);
                            yield return new ReceiveEvent(text);
                            break;

                        case "web_search":
                            var query = (string?)item["query"] ?? "";
                            if (query.Length > 0)
                            {
                                var entry = new ToolUseEvent("WebSearch", Query: query);
                                toolUses.Add(entry);
                                yield return entry;
                            }

                            break;
                    }

                    break;

                case "turn.completed":
                    var usage = node["usage"];
                    inputTokens = (long?)usage?["input_tokens"] ?? 0;
                    outputTokens = (long?)usage?["output_tokens"] ?? 0;
                    break;

                case "turn.failed":
                    errorMessage ??= (string?)node["error"]?["message"] ?? "Codex reported a failed turn.";
                    break;

                case "error":
                    errorMessage ??= (string?)node["message"] ?? "Codex reported an error.";
                    break;
            }
        }

        if (errorMessage is not null)
        {
            var message = CliLines.UnwrapApiError(errorMessage);
            throw new ProviderResponseException(message, CliTools.CodexProviderName, "cli_error", message);
        }

        yield return new CompleteEvent(
            fullText.ToString(),
            Pricing.OpenAiCost(modelName, inputTokens, outputTokens),
            modelName,
            sw.ElapsedMilliseconds,
            toolUses,
            inputTokens,
            outputTokens);
    }
}
