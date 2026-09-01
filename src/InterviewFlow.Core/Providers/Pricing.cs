namespace InterviewFlow.Core.Providers;

/// <summary>
/// Baked-in cost tables — (input, output) USD per million tokens, with
/// per-provider defaults for unknown models. Ollama is always $0. This is what
/// the "$X query cost" badges are computed from: the provider APIs report token
/// counts, not dollars, so an entry missing here silently falls back to the
/// provider default and the badge goes wrong rather than blank.
///
/// Rates verified 2026-08-31 against the providers' own pricing pages; the
/// original's streaming.py:104-155 table is the ancestor, not the source.
/// Cached-input, batch, and context-tier rates are deliberately not modelled —
/// the app only ever makes single uncached calls.
/// </summary>
public static class Pricing
{
    private static readonly Dictionary<string, (double In, double Out)> OpenAi = new()
    {
        ["gpt-5.6-sol"] = (4.00, 20.00),
        ["gpt-5.6"] = (4.00, 20.00),           // alias for -sol
        ["gpt-5.6-terra"] = (2.00, 12.00),
        ["gpt-5.6-luna"] = (0.20, 1.20),
        ["gpt-5.5"] = (5.00, 30.00),
        ["gpt-5.5-pro"] = (30.00, 180.00),
        ["gpt-5.4"] = (2.50, 15.00),
        ["gpt-5.4-mini"] = (0.75, 4.50),
        ["gpt-5.4-nano"] = (0.20, 1.25),
        ["gpt-5"] = (1.25, 10.00),
        ["gpt-5-mini"] = (0.25, 2.00),
        ["gpt-5-nano"] = (0.05, 0.40),
        ["gpt-4.1"] = (2.00, 8.00),
        ["gpt-4.1-mini"] = (0.40, 1.60),
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-4o-mini"] = (0.15, 0.60),
    };

    private static readonly (double In, double Out) OpenAiDefault = (2.50, 10.00);

    private static readonly Dictionary<string, (double In, double Out)> Anthropic = new()
    {
        ["claude-fable-5"] = (10.00, 50.00),
        ["claude-opus-5"] = (5.00, 25.00),
        ["claude-opus-4-8"] = (5.00, 25.00),
        ["claude-opus-4-7"] = (5.00, 25.00),
        ["claude-opus-4-6"] = (5.00, 25.00),
        ["claude-sonnet-5"] = (2.00, 10.00),
        ["claude-sonnet-4-6"] = (3.00, 15.00),
        ["claude-haiku-4-5"] = (1.00, 5.00),
        ["claude-haiku-4-5-20251001"] = (1.00, 5.00),  // the dated id, still valid
    };

    private static readonly (double In, double Out) AnthropicDefault = (3.00, 15.00);

    private static readonly Dictionary<string, (double In, double Out)> Gemini = new()
    {
        // The 3.7/3.6 Flash rates are promotional through 2026-12-31, after
        // which both go to (1.50, 7.50).
        ["gemini-3.7-flash"] = (0.75, 3.75),
        ["gemini-3.6-flash"] = (0.75, 3.75),
        ["gemini-3.5-flash"] = (1.50, 9.00),
        ["gemini-3.5-flash-lite"] = (0.30, 2.50),
        // Pro steps to (4.00, 18.00) above a 200k-token prompt; the flat table
        // can't express that, so long research prompts under-report slightly.
        ["gemini-3.1-pro-preview"] = (2.00, 12.00),
        ["gemini-2.5-pro"] = (1.25, 10.00),
        ["gemini-2.5-flash"] = (0.30, 2.50),
        ["gemini-2.0-flash"] = (0.10, 0.40),
        ["gemini-1.5-pro"] = (1.25, 5.00),
        ["gemini-1.5-flash"] = (0.075, 0.30),
    };

    private static readonly (double In, double Out) GeminiDefault = (1.25, 5.00);

    public static double AnthropicCost(string model, long inputTokens, long outputTokens) =>
        Cost(Anthropic.GetValueOrDefault(model, AnthropicDefault), inputTokens, outputTokens);

    public static double OpenAiCost(string model, long promptTokens, long completionTokens) =>
        Cost(OpenAi.GetValueOrDefault(model, OpenAiDefault), promptTokens, completionTokens);

    public static double GeminiCost(string model, long inputTokens, long outputTokens) =>
        Cost(Gemini.GetValueOrDefault(model, GeminiDefault), inputTokens, outputTokens);

    private static double Cost((double In, double Out) price, long input, long output) =>
        (input * price.In + output * price.Out) / 1_000_000;
}
