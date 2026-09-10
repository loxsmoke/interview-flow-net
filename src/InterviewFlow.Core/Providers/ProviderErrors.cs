using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterviewFlow.Core.Providers;

/// <summary>What the user is shown for a failed run: a headline and, under a "Details" fold, the exception chain without stack frames.</summary>
public sealed record UserFacingError(string Message, string Detail);

/// <summary>
/// Turns provider failures into something a person can act on (docs/05 §5.8).
/// Every API answers a rejected request with a JSON body that names the
/// problem — "You have no credits remaining. Add credits to continue…",
/// "Incorrect API key provided", "Your credit balance is too low" — and the
/// queue used to bury that under "Queued agent encountered an error. Please
/// try again." with the exception's stack trace as the detail. The stack
/// trace still goes to the diagnostic log; the user gets the API's sentence.
/// </summary>
public static class ProviderErrors
{
    /// <summary>The API's own account of a failure: a machine code and a sentence.</summary>
    public readonly record struct ApiError(string Code, string Message)
    {
        public static readonly ApiError None = new("", "");

        public bool IsEmpty => string.IsNullOrEmpty(Code) && string.IsNullOrEmpty(Message);

        /// <summary>
        /// Out of money rather than out of rate: OpenAI answers 429
        /// insufficient_quota, or a stream "error" event with
        /// credit_balance_exhausted; Anthropic answers 400 "Your credit balance
        /// is too low". None of these clear by waiting. Gemini's
        /// RESOURCE_EXHAUSTED is deliberately not here — it is also its
        /// per-minute rate limit, which does.
        /// </summary>
        public bool IsQuotaExhausted =>
            Has(Code, "quota") || Has(Code, "credit") || Has(Code, "billing")
            || Has(Message, "credit balance") || Has(Message, "no credits")
            || Has(Message, "exceeded your current quota");

        private static bool Has(string? text, string needle) =>
            text is not null && text.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the error out of a response body or stream event. Handles the
    /// three shapes in use — OpenAI <c>{"error":{"code","type","message"}}</c>,
    /// Anthropic <c>{"type":"error","error":{"type","message"}}</c>, Gemini
    /// <c>{"error":{"code":429,"status","message"}}</c> (sometimes wrapped in
    /// an array) — and a bare error object. Anything else is empty.
    /// </summary>
    public static ApiError Parse(string body)
    {
        var start = body.IndexOfAny(['{', '[']);
        if (start < 0)
            return ApiError.None;
        try
        {
            return Parse(JsonNode.Parse(body[start..]));
        }
        catch (JsonException)
        {
            return ApiError.None;
        }
    }

    public static ApiError Parse(JsonNode? node)
    {
        if (node is JsonArray array)
            node = array.FirstOrDefault();
        if (node is not JsonObject obj)
            return ApiError.None;
        if (obj["error"] is JsonObject inner)
            obj = inner;

        var message = Str(obj["message"]);
        // OpenAI puts the stable id in "code" and the family in "type"; Gemini
        // uses "status" for the readable one and "code" for the HTTP number.
        var code = Str(obj["code"]) is { Length: > 0 } c && !c.All(char.IsAsciiDigit)
            ? c
            : Str(obj["status"]) is { Length: > 0 } s ? s : Str(obj["type"]);
        return new ApiError(code, message);

        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    }

    /// <summary>
    /// The provider a request went to, by host, for the headline — or "" when
    /// there is no request to read it from, which drops the prefix rather than
    /// guessing at one.
    /// </summary>
    public static string ProviderFor(Uri? uri)
    {
        var host = uri?.Host ?? "";
        if (host.Length == 0)
            return "";
        if (host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase))
            return "OpenAI";
        if (host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase))
            return "Anthropic";
        if (host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase))
            return "Gemini";
        if (host is "localhost" or "127.0.0.1" or "::1")
            return "Ollama";
        return host;
    }

    /// <summary>
    /// The headline and detail for a failed run. The headline is the API's
    /// sentence when there is one, prefixed with the provider; a network
    /// failure says the provider could not be reached; anything else is the
    /// exception's message. The detail is the exception chain, one line each,
    /// never a stack trace.
    /// </summary>
    public static UserFacingError Describe(Exception ex)
    {
        var message = ex switch
        {
            ProviderResponseException p => Headline(p.Provider, p.ApiMessage),
            RateLimitException r => Headline(r.Provider,
                r.ApiMessage.Length > 0
                    ? $"Rate limited. {r.ApiMessage}"
                    : "Rate limited. Wait a moment and try again."),
            HttpRequestException { StatusCode: null } h =>
                $"Could not reach the AI provider: {Root(h).Message}",
            HttpRequestException h => h.Message,
            TaskCanceledException or TimeoutException =>
                "The AI provider did not answer in time. Try again.",
            _ => ex.Message.Length > 0 ? ex.Message : ex.GetType().Name,
        };
        return new UserFacingError(message, Chain(ex));
    }

    private static string Headline(string provider, string message) =>
        provider.Length > 0 ? $"{provider}: {message}" : message;

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is { } inner)
            ex = inner;
        return ex;
    }

    /// <summary>"Type: message" per exception in the chain, outermost first.</summary>
    private static string Chain(Exception ex)
    {
        var lines = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            lines.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join("\n", lines);
    }
}
