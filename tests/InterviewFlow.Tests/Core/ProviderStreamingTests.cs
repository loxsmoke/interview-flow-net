using System.Net;
using System.Text;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Tests.Core;

/// <summary>Queues canned HTTP responses and records outgoing requests.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<(string Url, string Body)> Requests { get; } = [];

    public void Enqueue(HttpStatusCode status, string body, string contentType = "text/event-stream",
        (string Name, string Value)? header = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (header is { } h)
            response.Headers.Add(h.Name, h.Value);
        _responses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));
        return _responses.Dequeue();
    }
}

public sealed class AnthropicStreamingTests
{
    private const string HappySse =
        """
        event: message_start
        data: {"type":"message_start","message":{"model":"claude-sonnet-4-6","usage":{"input_tokens":1000000}}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello "}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}

        event: message_delta
        data: {"type":"message_delta","usage":{"output_tokens":1000000}}

        """;

    [Fact]
    public async Task Streams_deltas_and_computes_cost_from_usage()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, HappySse);
        var provider = new AnthropicProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("hi", "sys", "claude-sonnet-4-6", 0.5, useWeb: false))
            events.Add(e);

        Assert.Equal(["Hello ", "world"], events.OfType<ReceiveEvent>().Select(e => e.Text));
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("Hello world", complete.Text);
        Assert.Equal("claude-sonnet-4-6", complete.ModelName);
        Assert.Equal(3.0 + 15.0, complete.CostUsd, 6); // 1M in + 1M out
        Assert.Contains("\"temperature\":0.5", handler.Requests[0].Body);
        Assert.Contains("\"system\":\"sys\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Web_mode_adds_the_tool_and_surfaces_search_queries()
    {
        const string webSse =
            """
            data: {"type":"content_block_start","content_block":{"type":"server_tool_use","name":"web_search"}}

            data: {"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"query\":\"acme"}}

            data: {"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":" reviews\"}"}}

            data: {"type":"content_block_stop"}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Report."}}

            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, webSse);
        var provider = new AnthropicProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("hi", "", "m", null, useWeb: true))
            events.Add(e);

        var tool = Assert.Single(events.OfType<ToolUseEvent>());
        Assert.Equal("WebSearch", tool.Tool);
        Assert.Equal("acme reviews", tool.Query);
        Assert.Contains("web_search_20250305", handler.Requests[0].Body);
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Single(complete.ToolUses);
    }

    [Fact]
    public async Task Pre_stream_429_waits_retry_after_then_succeeds()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "{\"error\":\"rate\"}", "application/json",
            ("retry-after", "1"));
        handler.Enqueue(HttpStatusCode.OK, HappySse);
        var provider = new AnthropicProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("hi", "", "m", null, useWeb: false))
            events.Add(e);

        Assert.Contains(events, e => e is RateLimitRetryEvent);          // countdown emitted
        Assert.DoesNotContain(events, e => e is RateLimitResetEvent);    // pre-stream: no reset
        Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal(2, handler.Requests.Count);
    }
}

public sealed class OpenAiStreamingTests
{
    [Fact]
    public async Task Chat_streams_deltas_and_reads_usage_chunk()
    {
        const string sse =
            """
            data: {"choices":[{"delta":{"content":"Hi"}}],"model":"gpt-4o-2024"}

            data: {"choices":[{"delta":{"content":"!"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":1000000,"completion_tokens":1000000}}

            data: [DONE]

            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, sse);
        var provider = new OpenAiProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("q", "s", "gpt-4o", 0.7, useWeb: false))
            events.Add(e);

        Assert.Equal(["Hi", "!"], events.OfType<ReceiveEvent>().Select(e => e.Text));
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("gpt-4o-2024", complete.ModelName);
        Assert.Equal(2.50 + 10.0, complete.CostUsd, 6); // default pricing for unknown snapshot
    }

    [Fact]
    public async Task Responses_mode_surfaces_search_and_citations()
    {
        const string sse =
            """
            data: {"type":"response.output_item.added","item":{"type":"web_search_call","action":{"query":"acme"}}}

            data: {"type":"response.output_text.delta","delta":"Answer"}

            data: {"type":"response.completed","response":{"model":"gpt-4o","usage":{"input_tokens":10,"output_tokens":20},"output":[{"type":"message","content":[{"annotations":[{"type":"url_citation","url":"https://x.example","title":"X"}]}]}]}}

            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, sse);
        var provider = new OpenAiProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("q", "s", "gpt-4o", null, useWeb: true))
            events.Add(e);

        var tools = events.OfType<ToolUseEvent>().ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("acme", tools[0].Query);
        Assert.Equal("https://x.example", tools[1].Url);
        Assert.Contains("web_search_preview", handler.Requests[0].Body);
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("Answer", complete.Text);
        Assert.Equal(2, complete.ToolUses.Count);
    }
}

public sealed class OllamaStreamingTests
{
    [Fact]
    public async Task Chat_streams_ndjson_content_at_zero_cost()
    {
        const string ndjson =
            """
            {"message":{"content":"He"}}
            {"message":{"content":"y"}}
            {"done":true}
            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, ndjson, "application/x-ndjson");
        var provider = new OllamaProvider("http://localhost:11434", numCtx: "8192", http: new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("q", "sys", "llama3.2", 0.7, useWeb: false))
            events.Add(e);

        Assert.Equal(["He", "y"], events.OfType<ReceiveEvent>().Select(e => e.Text));
        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal("Hey", complete.Text);
        Assert.Equal(0.0, complete.CostUsd);
        Assert.Contains("\"num_ctx\":8192", handler.Requests[0].Body);
        Assert.Contains("\"temperature\":0.7", handler.Requests[0].Body);
    }
}

public sealed class RouterTests
{
    private static AppConfig Config(string content, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".env");
        File.WriteAllText(path, content);
        return new AppConfig(EnvFile.Load(path));
    }

    [Fact]
    public async Task Emits_send_events_first_then_dispatches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-router-" + Guid.NewGuid().ToString("N")[..8]);
        var config = Config("ACTIVE_PROVIDER=ollama\nOLLAMA_MODEL=llama3.2\n", dir);
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"message\":{\"content\":\"ok\"}}", "application/x-ndjson");

        var events = new List<AgentEvent>();
        await foreach (var e in ProviderRouter.StreamQueryAsync(
            config, "user prompt", new QueryOptions("system prompt"), "company-research",
            http: new HttpClient(handler)))
        {
            events.Add(e);
        }

        var send0 = Assert.IsType<SendEvent>(events[0]);
        Assert.Equal("system", send0.Channel);
        Assert.Equal("system prompt", send0.Text);
        var send1 = Assert.IsType<SendEvent>(events[1]);
        Assert.Equal("user", send1.Channel);
        Assert.IsType<CompleteEvent>(events[^1]);
        // company-research section temperature (0.5) flows through to the request.
        Assert.Contains("\"temperature\":0.5", handler.Requests[0].Body);
        Directory.Delete(dir, recursive: true);
    }
}
