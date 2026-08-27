using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Providers;

/// <summary>Rate-limit wait-time extraction, ported from streaming.py:16-59.</summary>
public static partial class RetryParsing
{
    [GeneratedRegex(@"try again in (\d+(?:\.\d+)?)(ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleUnits();

    [GeneratedRegex(@"try again in (?:(\d+)m)?(?:(\d+(?:\.\d+)?)s)?", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesSeconds();

    /// <summary>
    /// Extracts the suggested wait (seconds) from an OpenAI rate-limit error
    /// message: "try again in 1.5s", "800ms", "1m30s". Null = don't retry hint.
    /// </summary>
    public static double? ParseOpenAiRetryAfter(string message)
    {
        var m = SimpleUnits().Match(message);
        if (m.Success)
        {
            var val = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return m.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? val / 1000 : val;
        }

        m = MinutesSeconds().Match(message);
        if (m.Success && (m.Groups[1].Success || m.Groups[2].Success))
        {
            var minutes = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            var seconds = m.Groups[2].Success
                ? double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            return minutes * 60 + seconds;
        }

        return null;
    }

    /// <summary>
    /// Retry-After header seconds, falling back to 60 s — one full token-bucket
    /// window for Anthropic per-minute limits (streaming.py:46-59).
    /// </summary>
    public static double ParseRetryAfterHeader(string? headerValue)
    {
        if (double.TryParse(headerValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        return 60.0;
    }
}
