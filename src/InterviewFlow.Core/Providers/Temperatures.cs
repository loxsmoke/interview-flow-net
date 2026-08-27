namespace InterviewFlow.Core.Providers;

/// <summary>
/// How a query's temperature is decided (mirrors streaming.py's three-valued
/// scheme): from the per-section map (default), the API default (custom actions
/// with null temperature), or an explicit value.
/// </summary>
public readonly record struct TemperatureSetting
{
    private enum Kind { FromSection, ApiDefault, Explicit }

    private readonly Kind _kind;
    private readonly double _value;

    private TemperatureSetting(Kind kind, double value) => (_kind, _value) = (kind, value);

    public static TemperatureSetting FromSection => new(Kind.FromSection, 0);
    public static TemperatureSetting ApiDefault => new(Kind.ApiDefault, 0);
    public static TemperatureSetting Explicit(double value) => new(Kind.Explicit, value);

    /// <summary>
    /// Resolves to a concrete temperature or null (= omit from the request).
    /// Anthropic and Ollama cap at 1.0; clamp silently rather than letting the
    /// API reject it (streaming.py:1052).
    /// </summary>
    public double? Resolve(string sectionName, string provider)
    {
        double? value = _kind switch
        {
            Kind.ApiDefault => null,
            Kind.Explicit => _value,
            _ => Temperatures.ForSection(sectionName),
        };
        if (value is not null && provider is "anthropic" or "ollama")
            value = Math.Min(1.0, value.Value);
        return value;
    }
}

/// <summary>Per-section temperature map, verbatim from streaming.py:63-78.</summary>
public static class Temperatures
{
    public const double Default = 0.7;

    private static readonly Dictionary<string, double> BySection = new()
    {
        // Analytical / structured output — low temperature for precision
        ["resume-review"] = 0.3,
        ["decode-jd"] = 0.3,
        ["mine-stories"] = 0.3,
        // Research / synthesis — mid temperature for balanced output
        ["anticipate-concerns"] = 0.5,
        ["company-research"] = 0.5,
        ["interview-intel"] = 0.5,
        ["salary-coach"] = 0.5,
        // Creative / conversational — higher temperature for variety
        ["build-pitches"] = 0.9,
        ["mock-interview"] = 0.9,
        ["resume-chat"] = 0.9,
    };

    public static double ForSection(string section) =>
        BySection.GetValueOrDefault(section, Default);
}
