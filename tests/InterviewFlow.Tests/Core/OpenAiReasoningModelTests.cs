using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// OpenAI's reasoning models (docs/05 §5.8): the GPT-5 line 400s on any
/// temperature but the default, spends part of max_output_tokens on hidden
/// reasoning, and ends a Responses stream with a terminal event the provider
/// used to drop — which is how a company-research run on GPT-5.6 Terra turned
/// into "stream ended before response.completed", retried five times.
/// </summary>
public sealed class OpenAiReasoningModelTests
{
    private const string ChatSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: [DONE]\n\n";

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-2024-08-06", true)]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("gpt-3.5-turbo", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("gpt-5.6-terra", false)]
    [InlineData("gpt-5", false)]
    [InlineData("gpt-5.4-mini", false)]
    [InlineData("o3", false)]
    [InlineData("o4-mini", false)]
    // An id we've never seen is assumed to follow the newer surface.
    [InlineData("gpt-7-nova", false)]
    public void Only_the_gpt4_line_accepts_temperature(string model, bool accepts) =>
        Assert.Equal(accepts, OpenAiProvider.AcceptsTemperature(model));

    [Fact]
    public async Task Temperature_is_omitted_for_reasoning_models()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, ChatSse);

        await foreach (var _ in new OpenAiProvider("sk-test", new HttpClient(handler))
            .StreamAsync("prompt", "system", "gpt-5.6-terra", 0.3, useWeb: false,
                TestContext.Current.CancellationToken))
        {
        }

        Assert.DoesNotContain("temperature", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Temperature_is_still_sent_to_models_that_take_it()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, ChatSse);

        await foreach (var _ in new OpenAiProvider("sk-test", new HttpClient(handler))
            .StreamAsync("prompt", "system", "gpt-4o", 0.3, useWeb: false,
                TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains("\"temperature\":0.3", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("gpt-4o", 8000)]
    [InlineData("gpt-5.6-terra", 16000)]
    public async Task Reasoning_models_get_a_larger_output_budget_in_web_mode(string model, int expected)
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n");

        await foreach (var _ in new OpenAiProvider("sk-test", new HttpClient(handler))
            .StreamAsync("prompt", "system", model, null, useWeb: true, TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains($"\"max_output_tokens\":{expected}", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Incomplete_response_keeps_the_partial_text_and_says_so()
    {
        const string sse =
            """
            data: {"type":"response.output_text.delta","delta":"Partial answer"}

            data: {"type":"response.incomplete","response":{"model":"gpt-5.6-terra","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":30000,"output_tokens":16000}}}

            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, sse);

        var events = new List<AgentEvent>();
        await foreach (var e in new OpenAiProvider("sk-test", new HttpClient(handler))
            .StreamAsync("q", "s", "gpt-5.6-terra", null, useWeb: true, TestContext.Current.CancellationToken))
            events.Add(e);

        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.StartsWith("Partial answer", complete.Text);
        Assert.Contains("cut short", complete.Text);
        Assert.Contains("max_output_tokens", complete.Text);
        Assert.Equal(30000, complete.InputTokens);
        Assert.Equal(16000, complete.OutputTokens);
        // Not retried: the partial result is the result.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Incomplete_response_with_no_text_reports_the_reason()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n");

        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in new OpenAiProvider("sk-test", new HttpClient(handler))
                .StreamAsync("q", "s", "gpt-5.6-terra", null, useWeb: true, TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Contains("max_output_tokens", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"type":"response.failed","response":{"error":{"code":"server_error","message":"The model produced invalid output"}}}""",
        "server_error: The model produced invalid output")]
    [InlineData("""{"type":"error","code":"rate_limit_exceeded","message":"Too many tokens"}""",
        "rate_limit_exceeded: Too many tokens")]
    public async Task Terminal_failure_events_surface_the_reason_and_are_not_retried(string payload, string expected)
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "data: " + payload + "\n\n");

        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in new OpenAiProvider("sk-test", new HttpClient(handler))
                .StreamAsync("q", "s", "gpt-5.6-terra", null, useWeb: true, TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Contains(expected, ex.Message);
        // Only transient failures earn a retry; a rejected request would fail again.
        Assert.Single(handler.Requests);
    }
}
