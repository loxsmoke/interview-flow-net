using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// "Use AI Resume" extraction (exact port of updateFromAnalysis, index.html):
/// pulls everything after the "6 … tailored resume draft" heading, then strips
/// a trailing "# … a note …" block.
/// </summary>
public static partial class TailoredResume
{
    [GeneratedRegex(@"^#{1,6}\s*6[^#\n]*tailored resume draft[^#\n]*\n([\s\S]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DraftRe();

    [GeneratedRegex(@"\n#{1,6}\s*a note\b[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNoteRe();

    /// <summary>True when the analysis contains a section-6 draft (enables the button).</summary>
    public static bool HasDraft(string analysis) =>
        analysis.Length > 0 && DraftRe().IsMatch(analysis);

    /// <summary>The verbatim error the original shows when no draft is present.</summary>
    public const string NoDraftMessage =
        "No tailored resume draft found in AI analysis. Make sure the AI has run and produced a section 6.";

    /// <summary>Extracted draft, or null when the analysis has no section 6.</summary>
    public static string? Extract(string analysis)
    {
        var match = DraftRe().Match(analysis);
        if (!match.Success)
            return null;
        return TrailingNoteRe().Replace(match.Groups[1].Value.Trim(), "").Trim();
    }

    /// <summary>Strips [Tag] prefixes to get the plain resume body (stripResumeTags).</summary>
    public static string StripTags(string tagged) =>
        Regex.Replace(tagged, @"^\[[^\]]+\]\s*", "", RegexOptions.Multiline).Trim();
}
