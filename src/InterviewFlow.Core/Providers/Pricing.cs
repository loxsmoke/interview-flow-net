namespace InterviewFlow.Core.Providers;

/// <summary>
/// Baked-in cost tables, verbatim from streaming.py:104-155 —
/// (input, output) USD per million tokens, with per-provider defaults for
/// unknown models. Ollama is always $0.
/// </summary>
public static class Pricing
{
    private static readonly Dictionary<string, (double In, double Out)> OpenAi = new()
    {
        ["gpt-5.5"] = (5.00, 30.00),
        ["gpt-5.5-pro"] = (30.00, 180.00),
        ["gpt-5.4"] = (5.00, 20.00),
        ["gpt-5.4-mini"] = (0.75, 3.00),
        ["gpt-5"] = (5.00, 20.00),
        ["gpt-5-mini"] = (0.75, 3.00),
        ["gpt-4.1"] = (2.00, 8.00),
        ["gpt-4.1-mini"] = (0.40, 1.60),
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-4o-mini"] = (0.15, 0.60),
    };

    private static readonly (double In, double Out) OpenAiDefault = (2.50, 10.00);

    private static readonly Dictionary<string, (double In, double Out)> Anthropic = new()
    {
        ["claude-opus-4-7"] = (15.00, 75.00),
        ["claude-sonnet-4-6"] = (3.00, 15.00),
        ["claude-haiku-4-5-20251001"] = (0.80, 4.00),
    };

    private static readonly (double In, double Out) AnthropicDefault = (3.00, 15.00);

    private static readonly Dictionary<string, (double In, double Out)> Gemini = new()
    {
        ["gemini-2.5-pro"] = (1.25, 10.00),
        ["gemini-2.5-flash"] = (0.15, 0.60),
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
