using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Providers;
using InterviewFlow.Core.Queue;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// What a failed run tells the user (docs/05 §5.8). The exhausted-credit case
/// is the real one: OpenAI's stream ended with an "error" event and the queue
/// showed "Queued agent encountered an error. Please try again." over a
/// 30-line stack trace.
/// </summary>
public sealed class ProviderErrorsTests
{
    private const string OpenAiCreditEvent =
        """{"type":"error","error":{"type":"insufficient_quota","code":"credit_balance_exhausted","message":"You have no credits remaining. Add credits to continue using the API at https://platform.openai.com/settings/organization/billing/.","param":null},"sequence_number":2}""";

    private const string OpenAiQuota429 =
        """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","param":null,"code":"insufficient_quota"}}""";

    private const string AnthropicCredit400 =
        """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}""";

    private const string GeminiQuota429 =
        """{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}""";

    [Theory]
    [InlineData(OpenAiCreditEvent, "credit_balance_exhausted", "You have no credits remaining.")]
    [InlineData(OpenAiQuota429, "insufficient_quota", "You exceeded your current quota")]
    [InlineData(AnthropicCredit400, "invalid_request_error", "Your credit balance is too low")]
    [InlineData(GeminiQuota429, "RESOURCE_EXHAUSTED", "Resource has been exhausted")]
    // Gemini sometimes wraps the envelope in an array; text before the JSON is skipped.
    [InlineData("HTTP 429: [" + GeminiQuota429 + "]", "RESOURCE_EXHAUSTED", "Resource has been exhausted")]
    public void Reads_every_providers_error_shape(string body, string code, string messageStart)
    {
        var error = ProviderErrors.Parse(body);
        Assert.Equal(code, error.Code);
        Assert.StartsWith(messageStart, error.Message);
        Assert.False(error.IsEmpty);
    }

    [Theory]
    // Out of money: waiting never clears these, so they must not be retried.
    [InlineData(OpenAiCreditEvent, true)]
    [InlineData(OpenAiQuota429, true)]
    [InlineData(AnthropicCredit400, true)]
    // Gemini reuses RESOURCE_EXHAUSTED for its per-minute rate limit, which a
    // wait does clear — so it stays a rate limit.
    [InlineData(GeminiQuota429, false)]
    public void Only_money_problems_count_as_quota_exhaustion(string body, bool exhausted) =>
        Assert.Equal(exhausted, ProviderErrors.Parse(body).IsQuotaExhausted);

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"choices":[]}""")]
    public void Anything_else_is_empty(string body) =>
        Assert.True(ProviderErrors.Parse(body).IsEmpty);

    [Fact]
    public void A_plain_rate_limit_is_not_quota_exhaustion()
    {
        var error = ProviderErrors.Parse(
            """{"error":{"message":"Rate limit reached for gpt-4o. Please try again in 20s.","type":"requests","code":"rate_limit_exceeded"}}""");
        Assert.False(error.IsQuotaExhausted);
    }

    [Fact]
    public void Credit_exhaustion_shows_the_providers_sentence_without_a_stack_trace()
    {
        var ex = new ProviderResponseException(
            "OpenAI stream error: credit_balance_exhausted: You have no credits remaining.",
            "OpenAI", "credit_balance_exhausted",
            "You have no credits remaining. Add credits to continue using the API at https://platform.openai.com/settings/organization/billing/.");

        var shown = ProviderErrors.Describe(ex);

        Assert.Equal(
            "OpenAI: You have no credits remaining. Add credits to continue using the API at https://platform.openai.com/settings/organization/billing/.",
            shown.Message);
        Assert.Equal("ProviderResponseException: " + ex.Message, shown.Detail);
        Assert.DoesNotContain("   at ", shown.Detail);
    }

    [Fact]
    public void Rate_limit_and_network_failures_read_as_such()
    {
        Assert.Equal("Anthropic: Rate limited. Slow down.",
            ProviderErrors.Describe(new RateLimitException("HTTP 429", 5, "Anthropic", "Slow down.")).Message);
        Assert.Equal("Rate limited. Wait a moment and try again.",
            ProviderErrors.Describe(new RateLimitException("HTTP 429", null)).Message);

        var offline = new HttpRequestException("No such host is known.", new System.Net.Sockets.SocketException(11001));
        var shown = ProviderErrors.Describe(offline);
        Assert.StartsWith("Could not reach the AI provider:", shown.Message);
        Assert.Contains("SocketException", shown.Detail);

        Assert.Equal("The AI provider did not answer in time. Try again.",
            ProviderErrors.Describe(new TaskCanceledException("timed out")).Message);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/responses", "OpenAI")]
    [InlineData("https://api.anthropic.com/v1/messages", "Anthropic")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models", "Gemini")]
    [InlineData("http://localhost:11434/api/chat", "Ollama")]
    [InlineData("https://proxy.example.net/v1", "proxy.example.net")]
    public void Names_the_provider_from_the_request_host(string url, string expected) =>
        Assert.Equal(expected, ProviderErrors.ProviderFor(new Uri(url)));

    [Fact]
    public async Task Out_of_credit_429_is_rejected_at_once_not_retried_as_a_rate_limit()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, OpenAiQuota429, "application/json");
        var provider = new OpenAiProvider("sk-test", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync("q", "s", "gpt-4o", null, useWeb: false,
                TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal("OpenAI", ex.Provider);
        Assert.Equal("insufficient_quota", ex.Code);
        Assert.StartsWith("You exceeded your current quota", ex.ApiMessage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Anthropic_credit_400_carries_its_message()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, AnthropicCredit400, "application/json");
        var provider = new AnthropicProvider("sk-test", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderResponseException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync("q", "s", "claude-sonnet-4-6", null, useWeb: false,
                TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal("Anthropic: Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.",
            ProviderErrors.Describe(ex).Message);
    }

    [Fact]
    public async Task A_5xx_in_web_mode_is_still_retried_as_transient()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html");
        handler.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-4o\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n");
        var provider = new OpenAiProvider("sk-test", new HttpClient(handler));

        var events = new List<AgentEvent>();
        await foreach (var e in provider.StreamAsync("q", "s", "gpt-4o", null, useWeb: true,
            TestContext.Current.CancellationToken))
            events.Add(e);

        Assert.IsType<CompleteEvent>(events[^1]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_stream_error_event_reaches_the_queue_as_the_providers_sentence()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "data: " + OpenAiCreditEvent + "\n\n");
        var provider = new OpenAiProvider("sk-test", new HttpClient(handler));

        var queue = new QueueManager();
        var worker = new QueueWorker(queue, (_, ct) =>
            provider.StreamAsync("q", "s", "gpt-5.6-terra", null, useWeb: true, ct));
        var item = queue.Enqueue("s1", "research", "R");
        worker.EnsureRunning();
        await worker.CurrentLoop!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal(
            "OpenAI: You have no credits remaining. Add credits to continue using the API at https://platform.openai.com/settings/organization/billing/.",
            item.Error);
        Assert.StartsWith("ProviderResponseException: OpenAI stream error: credit_balance_exhausted:", item.ErrorDetail);
        Assert.DoesNotContain("   at ", item.ErrorDetail);
    }
}
