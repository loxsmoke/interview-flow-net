using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterviewFlow.Core.Providers;

/// <summary>Thrown for HTTP 429 so provider retry loops can react uniformly.</summary>
public sealed class RateLimitException(string message, double? suggestedWaitSeconds) : Exception(message)
{
    public double? SuggestedWaitSeconds { get; } = suggestedWaitSeconds;
}

/// <summary>
/// Shared plumbing for the raw-HttpClient providers: one long-lived client,
/// SSE/NDJSON line streaming, and the heartbeat-emitting rate-limit wait.
/// Line reading intentionally splits on \r/\n only (StreamReader semantics) —
/// U+2028/2029/0085 inside model text must never be treated as line breaks
/// (docs/07-queue-and-streaming.md §7.3).
/// </summary>
public static class ProviderHttp
{
    /// <summary>Default shared client; providers accept an override for tests.</summary>
    public static readonly HttpClient Default = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }

    /// <summary>
    /// SSE "data:" payloads as parsed JSON, until the stream ends or a
    /// "[DONE]" sentinel. Ignores event:/id:/comment lines — every payload the
    /// providers need carries its own type field.
    /// </summary>
    public static async IAsyncEnumerable<JsonNode> ReadSseJsonAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in ReadLinesAsync(response, ct))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line[5..].TrimStart();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not null)
                yield return node;
        }
    }

    /// <summary>
    /// Sleeps out a rate limit, yielding a countdown event every 5 s
    /// (streaming.py:33-43).
    /// </summary>
    public static async IAsyncEnumerable<Agents.AgentEvent> WaitWithHeartbeatsAsync(
        double seconds, [EnumeratorCancellation] CancellationToken ct)
    {
        var elapsed = 0.0;
        while (elapsed < seconds)
        {
            var chunk = Math.Min(5.0, seconds - elapsed);
            await Task.Delay(TimeSpan.FromSeconds(chunk), ct);
            elapsed += chunk;
            var remaining = Math.Max(0.0, seconds - elapsed);
            yield return new Agents.RateLimitRetryEvent((int)Math.Ceiling(remaining));
        }
    }

    /// <summary>Throws RateLimitException on 429, HttpRequestException otherwise.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 429)
        {
            var header = response.Headers.TryGetValues("retry-after", out var vals) ? vals.FirstOrDefault() : null;
            double? suggested = header is not null
                ? RetryParsing.ParseRetryAfterHeader(header)
                : RetryParsing.ParseOpenAiRetryAfter(body);
            throw new RateLimitException($"HTTP 429: {Truncate(body)}", suggested);
        }

        throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body)}");
    }

    private static string Truncate(string s) => s.Length > 600 ? s[..600] : s;
}
