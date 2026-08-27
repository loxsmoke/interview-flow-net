namespace InterviewFlow.Core.Agents;

/// <summary>
/// The raw-HTML warning block prepended to reports when web search failed —
/// strings verbatim from main.py:394-418. Stored inline in the report for data
/// compatibility; the native renderer recognizes the shape (04 §4.5).
/// </summary>
public static class SearchWarnings
{
    private static readonly Dictionary<string, string> Warnings = new()
    {
        ["connection_error"] =
            "⚠️ <strong>Web search failed — connection error.</strong> " +
            "All search queries failed before returning any data. " +
            "Check your internet connection and try again. " +
            "The report below is based solely on the AI model's training data.",
        ["no_results"] =
            "⚠️ <strong>No web search results found.</strong> " +
            "Searches ran successfully but returned no data — " +
            "this usually means there is limited public information available about this topic " +
            "(e.g. the company has little online coverage or there are no interview reviews). " +
            "The report below is based solely on the AI model's training data.",
        ["not_searched"] =
            "⚠️ <strong>No web searches were performed.</strong> " +
            "The model generated this report from its training data without querying the web. " +
            "Results may be outdated or incomplete.",
    };

    /// <summary>Prepends the warning div for a non-"ok" search status.</summary>
    public static string Apply(string text, string searchStatus)
    {
        if (!Warnings.TryGetValue(searchStatus, out var warning))
            return text;
        return $"<div class=\"search-warning\">{warning}</div>\n\n" + text;
    }
}
