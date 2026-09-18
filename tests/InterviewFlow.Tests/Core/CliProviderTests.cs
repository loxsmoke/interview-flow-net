using System.Runtime.CompilerServices;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Tests.Core;

/// <summary>Plays canned stdout lines and records what it was asked to run.</summary>
internal sealed class FakeCliRunner(params string[] lines) : ICliRunner
{
    public List<CliCommand> Commands { get; } = [];

    /// <summary>Thrown after the lines, as a failing process would.</summary>
    public CliProcessException? FailWith { get; init; }

    public async IAsyncEnumerable<string> RunAsync(
        CliCommand command, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Commands.Add(command);
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }

        if (FailWith is not null)
            throw FailWith;
    }
}

internal static class CliCommandAssertions
{
    /// <summary>The value that follows a flag, or null when the flag is absent.</summary>
    public static string? Arg(this CliCommand cmd, string flag)
    {
        var index = cmd.Arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= cmd.Arguments.Count ? null : cmd.Arguments[index + 1];
    }
}

public sealed class ClaudeCliProviderTests
{
    private static readonly string[] HappyRun =
    [
        """{"type":"system","subtype":"init","session_id":"s"}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-sonnet-5-20260401"}}}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"WebSearch"}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"Acme"}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":" culture\"}"}}}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""",
        "not json at all",
        """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello "}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"world"}}}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"Hello world","total_cost_usd":0.0123,"usage":{"input_tokens":10,"cache_creation_input_tokens":500,"cache_read_input_tokens":400,"output_tokens":46},"modelUsage":{"claude-sonnet-5-20260401":{"costUSD":0.0123}}}""",
    ];

    [Fact]
    public async Task Streams_deltas_tool_use_and_the_cli_reported_cost()
    {
        var runner = new FakeCliRunner(HappyRun);
        var events = new List<AgentEvent>();
        await foreach (var e in new ClaudeCliProvider("claude", runner)
            .StreamAsync("prompt", "system", "sonnet", useWeb: true, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        var tool = Assert.Single(events.OfType<ToolUseEvent>());
        Assert.Equal("WebSearch", tool.Tool);
        Assert.Equal("Acme culture", tool.Query);
        Assert.Equal(["Hello ", "world"], events.OfType<ReceiveEvent>().Select(r => r.Text));

        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("Hello world", complete.Text);
        Assert.Equal(0.0123, complete.CostUsd);
        Assert.Equal("claude-sonnet-5-20260401", complete.ModelName);
        Assert.Equal(910, complete.InputTokens); // fresh + cache writes + cache reads
        Assert.Equal(46, complete.OutputTokens);
        Assert.Single(complete.ToolUses);
    }

    [Fact]
    public async Task Builds_a_headless_command_with_the_prompt_on_stdin()
    {
        var runner = new FakeCliRunner(HappyRun);
        await foreach (var _ in new ClaudeCliProvider(@"C:\tools\claude.exe", runner)
            .StreamAsync("the prompt", "be terse", "opus", useWeb: false, TestContext.Current.CancellationToken))
        {
        }

        var cmd = Assert.Single(runner.Commands);
        Assert.Equal(@"C:\tools\claude.exe", cmd.FileName);
        Assert.Equal("the prompt", cmd.StdIn);
        Assert.Contains("-p", cmd.Arguments);
        Assert.Equal("stream-json", cmd.Arg("--output-format"));
        Assert.Equal("opus", cmd.Arg("--model"));
        Assert.Equal("be terse", cmd.Arg("--system-prompt"));
        Assert.Contains("--no-session-persistence", cmd.Arguments);
        // Non-web runs disable every agent tool; web runs pre-approve WebSearch only.
        Assert.Equal("", cmd.Arg("--tools"));
        Assert.DoesNotContain("--allowedTools", cmd.Arguments);
        // A nested-session marker would make the CLI refuse to start.
        Assert.True(cmd.Environment.ContainsKey("CLAUDECODE"));
        Assert.Null(cmd.Environment["CLAUDECODE"]);
        Assert.True(Directory.Exists(cmd.WorkingDirectory));
    }

    [Fact]
    public void Web_mode_allows_only_the_search_tool()
    {
        var cmd = ClaudeCliProvider.BuildCommand("claude", "p", "s", "sonnet", useWeb: true);
        Assert.Equal("WebSearch", cmd.Arg("--tools"));
        Assert.Equal("WebSearch", cmd.Arg("--allowedTools"));
    }

    [Fact]
    public void An_oversized_system_prompt_moves_into_stdin()
    {
        var system = new string('x', ClaudeCliProvider.MaxSystemPromptArgLength + 1);
        var cmd = ClaudeCliProvider.BuildCommand("claude", "the prompt", system, "", useWeb: false);
        Assert.DoesNotContain("--system-prompt", cmd.Arguments);
        Assert.DoesNotContain("--model", cmd.Arguments);
        Assert.StartsWith("<system>\n" + system, cmd.StdIn);
        Assert.EndsWith("the prompt", cmd.StdIn);
    }

    [Fact]
    public async Task A_result_flagged_as_error_becomes_a_provider_error()
    {
        var runner = new FakeCliRunner(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in. Run claude login.","total_cost_usd":0}""");
        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in new ClaudeCliProvider("claude", runner)
                .StreamAsync("p", "s", "sonnet", false, TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Equal("Claude Code", ex.Provider);
        Assert.Equal("error_during_execution", ex.Code);
        Assert.Equal("Not logged in. Run claude login.", ex.ApiMessage);
        Assert.StartsWith("Claude Code: Not logged in", ProviderErrors.Describe(ex).Message);
    }

    [Fact]
    public async Task A_crashed_process_reports_its_stderr()
    {
        var runner = new FakeCliRunner
        {
            FailWith = new CliProcessException("claude.exe exited with code 1", 1,
                "2026-09-17T20:13:42Z ERROR some::module noisy log line\nInvalid API key · Please run /login"),
        };
        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in new ClaudeCliProvider("claude", runner)
                .StreamAsync("p", "s", "sonnet", false, TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Equal("Claude Code", ex.Provider);
        Assert.Equal("claude.exe exited with code 1: Invalid API key · Please run /login", ex.ApiMessage);
    }

    [Fact]
    public async Task Falls_back_to_the_result_text_when_no_deltas_arrived()
    {
        var runner = new FakeCliRunner(
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","total_cost_usd":0.001,"usage":{"input_tokens":5,"output_tokens":1}}""");
        var events = new List<AgentEvent>();
        await foreach (var e in new ClaudeCliProvider("claude", runner)
            .StreamAsync("p", "", "", false, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Assert.Equal("OK", Assert.Single(events.OfType<ReceiveEvent>()).Text);
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("OK", complete.Text);
        Assert.Equal("claude", complete.ModelName); // no model given, none reported
    }
}

public sealed class CodexCliProviderTests
{
    private static readonly string[] HappyRun =
    [
        "2026-09-17T20:13:42Z ERROR codex_models_manager::cache: failed to load models cache",
        """{"type":"thread.started","thread_id":"t"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I’ll check current web sources."}}""",
        // Verbatim shape from codex 0.154: note the duplicated "id" key inside the item.
        """{"type":"item.started","item":{"id":"item_1","type":"web_search","id":"exec-d86a02c4","query":"","action":{"type":"other"}}}""",
        """{"type":"item.completed","item":{"id":"item_1","type":"web_search","id":"exec-d86a02c4","query":"Acme culture","action":{"type":"search","query":"Acme culture"}}}""",
        """{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Acme is fine."}}""",
        """{"type":"turn.completed","usage":{"input_tokens":1000000,"cached_input_tokens":28800,"output_tokens":1000000,"reasoning_output_tokens":0}}""",
    ];

    [Fact]
    public async Task Whole_messages_become_receive_events_and_cost_uses_list_prices()
    {
        var runner = new FakeCliRunner(HappyRun);
        var events = new List<AgentEvent>();
        await foreach (var e in new CodexCliProvider("codex", runner)
            .StreamAsync("prompt", "system", "gpt-5.5", useWeb: true, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        var tool = Assert.Single(events.OfType<ToolUseEvent>());
        Assert.Equal("Acme culture", tool.Query); // the started item's empty query is skipped
        Assert.Equal(["I’ll check current web sources.", "\n\n", "Acme is fine."],
            events.OfType<ReceiveEvent>().Select(r => r.Text));

        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("I’ll check current web sources.\n\nAcme is fine.", complete.Text);
        Assert.Equal("gpt-5.5", complete.ModelName);
        Assert.Equal(5.0 + 30.0, complete.CostUsd);
        Assert.Equal(1000000, complete.InputTokens);
    }

    [Fact]
    public async Task Builds_a_sandboxed_exec_command_with_instructions_on_stdin()
    {
        var runner = new FakeCliRunner(HappyRun);
        await foreach (var _ in new CodexCliProvider("/usr/local/bin/codex", runner)
            .StreamAsync("the prompt", "be terse", "", useWeb: false, TestContext.Current.CancellationToken))
        {
        }

        var cmd = Assert.Single(runner.Commands);
        Assert.Equal("exec", cmd.Arguments[0]);
        Assert.Contains("--json", cmd.Arguments);
        Assert.Contains("--ephemeral", cmd.Arguments);
        Assert.Equal("read-only", cmd.Arg("--sandbox"));
        Assert.Equal("-", cmd.Arguments[^1]);
        Assert.DoesNotContain("--model", cmd.Arguments); // empty = the CLI's own default
        Assert.DoesNotContain("-c", cmd.Arguments);
        Assert.Equal("<instructions>\nbe terse\n</instructions>\n\nthe prompt", cmd.StdIn);
    }

    [Fact]
    public void Web_mode_turns_on_live_search_and_a_model_is_passed_through()
    {
        var cmd = CodexCliProvider.BuildCommand("codex", "p", "", "gpt-5.5", useWeb: true);
        Assert.Equal("web_search=\"live\"", cmd.Arg("-c"));
        Assert.Equal("gpt-5.5", cmd.Arg("--model"));
        Assert.Equal("p", cmd.StdIn);
    }

    [Fact]
    public async Task A_failed_turn_unwraps_the_api_sentence()
    {
        var runner = new FakeCliRunner(
            """{"type":"thread.started","thread_id":"t"}""",
            """{"type":"error","message":"{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-9' model requires a newer version of Codex.\"}}"}""",
            """{"type":"turn.failed","error":{"message":"same again"}}""");
        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in new CodexCliProvider("codex", runner)
                .StreamAsync("p", "", "gpt-9", false, TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Equal("Codex", ex.Provider);
        Assert.Equal("The 'gpt-9' model requires a newer version of Codex.", ex.ApiMessage);
    }
}

public sealed class CliToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public CliToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void A_configured_path_is_taken_literally()
    {
        var exe = Touch("bin", "claude.exe");
        Assert.Equal(exe, CliTools.FindClaude(exe));
        Assert.Equal(exe, CliTools.FindClaude($"  \"{exe}\" ")); // pasted with quotes
        Assert.Null(CliTools.FindClaude(Path.Combine(_dir, "missing", "claude.exe")));
    }

    [Fact]
    public void A_missing_configured_path_names_the_fix()
    {
        var bogus = Path.Combine(_dir, "nope", "codex.exe");
        var ex = Assert.Throws<ProviderResponseException>(() => CliTools.RequireCodex(bogus));
        Assert.Equal("Codex", ex.Provider);
        Assert.Equal("cli_not_found", ex.Code);
        Assert.Contains(bogus, ex.ApiMessage);
    }

    [Fact]
    public void Path_lookup_walks_entries_and_windows_extensions()
    {
        var exe = Touch("tools", OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        var path = string.Join(Path.PathSeparator, [Path.Combine(_dir, "empty"), Path.Combine(_dir, "tools")]);
        Assert.Equal(exe, CliTools.OnPath("codex", path));
        Assert.Null(CliTools.OnPath("nothing-here", path));
    }

    [Fact]
    public void Bundled_codex_binaries_come_newest_first()
    {
        var root = Path.Combine(_dir, "OpenAI", "Codex", "bin");
        var old = Touch("OpenAI", "Codex", "bin", "codex.exe");
        var current = Touch("OpenAI", "Codex", "bin", "12219cbfbcbddde7", "codex.exe");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMonths(-4));
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow);

        Assert.Equal([current, old], CliTools.BundledCodexBinaries(root));
        Assert.Empty(CliTools.BundledCodexBinaries(Path.Combine(_dir, "absent")));
    }

    [Fact]
    public void Reads_only_the_top_level_model_from_codex_config()
    {
        var home = Path.Combine(_dir, "codex-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"),
            "# default config\n\nmodel = \"gpt-6-astra\" # pinned\nmodel_reasoning_effort = \"low\"\n\n[profiles.fast]\nmodel = \"gpt-5.4-mini\"\n");
        Assert.Equal("gpt-6-astra", CliTools.CodexDefaultModel(home));

        File.WriteAllText(Path.Combine(home, "config.toml"), "[profiles.fast]\nmodel = \"gpt-5.4-mini\"\n");
        Assert.Equal("", CliTools.CodexDefaultModel(home));
        Assert.Equal("", CliTools.CodexDefaultModel(Path.Combine(_dir, "no-such-home")));
    }
}

public sealed class CliRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-clirt-" + Guid.NewGuid().ToString("N")[..8]);

    public CliRoutingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AppConfig Config(string content)
    {
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, content);
        return new AppConfig(EnvFile.Load(path));
    }

    private string FakeExe(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void The_cli_providers_are_explicit_choices_only()
    {
        Assert.Equal("claude-cli", ProviderRouter.ResolveProvider(Config("ACTIVE_PROVIDER=claude-cli\n")));
        Assert.Equal("codex-cli", ProviderRouter.ResolveProvider(Config("ACTIVE_PROVIDER=Codex-CLI\n")));
        // The fallback rule never picks a CLI on its own.
        Assert.Equal("anthropic", ProviderRouter.ResolveProvider(Config("CLAUDE_CLI_PATH=/x/claude\n")));
        Assert.True(ProviderRouter.IsCli("claude-cli"));
        Assert.False(ProviderRouter.IsCli("ollama"));
    }

    [Fact]
    public async Task Router_dispatches_to_claude_code_with_the_configured_path_and_model()
    {
        var exe = FakeExe("claude.exe");
        var config = Config($"ACTIVE_PROVIDER=claude-cli\nCLAUDE_CLI_PATH={exe}\nCLAUDE_CLI_MODEL=haiku\n");
        var runner = new FakeCliRunner(
            """{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.002,"usage":{"input_tokens":1,"output_tokens":1}}""");

        var events = new List<AgentEvent>();
        await foreach (var e in ProviderRouter.StreamQueryAsync(
            config, "user prompt", new QueryOptions("system prompt", UseWebSearch: true), "company-research",
            TestContext.Current.CancellationToken, cli: runner))
        {
            events.Add(e);
        }

        Assert.IsType<SendEvent>(events[0]);
        Assert.Equal("done", Assert.IsType<CompleteEvent>(events[^1]).Text);
        var cmd = Assert.Single(runner.Commands);
        Assert.Equal(exe, cmd.FileName);
        Assert.Equal("haiku", cmd.Arg("--model"));
        Assert.Equal("system prompt", cmd.Arg("--system-prompt"));
        Assert.Contains("--allowedTools", cmd.Arguments);
        Assert.Equal("user prompt", cmd.StdIn);
    }

    [Fact]
    public async Task Router_reports_a_missing_cli_as_a_provider_error()
    {
        var config = Config($"ACTIVE_PROVIDER=codex-cli\nCODEX_CLI_PATH={Path.Combine(_dir, "gone", "codex.exe")}\n");
        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in ProviderRouter.StreamQueryAsync(
                config, "p", new QueryOptions("s"), "decode-jd", TestContext.Current.CancellationToken, cli: new FakeCliRunner()))
            {
            }
        });
        Assert.Equal("cli_not_found", ex.Code);
    }

    [Fact]
    public async Task Chat_turns_ride_as_one_transcript_prompt()
    {
        var exe = FakeExe("codex.exe");
        var config = Config($"ACTIVE_PROVIDER=codex-cli\nCODEX_CLI_PATH={exe}\nCODEX_CLI_MODEL=gpt-5.5\n");
        var runner = new FakeCliRunner(
            """{"type":"item.completed","item":{"type":"agent_message","text":"Next question?"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""");

        var reply = await ChatProvider.CompleteAsync(config,
        [
            new ChatMessage("system", "You are the interviewer."),
            new ChatMessage("user", "Let's begin."),
            new ChatMessage("assistant", "Tell me about yourself."),
            new ChatMessage("user", "I build things."),
        ], 0.9, TestContext.Current.CancellationToken, cli: runner);

        Assert.Equal("Next question?", reply);
        var cmd = Assert.Single(runner.Commands);
        Assert.StartsWith("<instructions>\nYou are the interviewer.\n</instructions>\n\n", cmd.StdIn);
        Assert.Contains("### User\nLet's begin.\n\n### Assistant\nTell me about yourself.\n\n### User\nI build things.\n\n### Assistant\n", cmd.StdIn);
        Assert.DoesNotContain("-c", cmd.Arguments); // chat never searches the web
    }

    [Fact]
    public void An_opening_message_is_not_wrapped_as_a_transcript()
    {
        Assert.Equal("Hi", ChatProvider.RenderTranscript([new ChatMessage("user", "Hi")]));
        Assert.EndsWith("### Assistant\n", ChatProvider.RenderTranscript(
            [new ChatMessage("user", "Hi"), new ChatMessage("assistant", "Hello"), new ChatMessage("user", "Go")]));
    }
}
