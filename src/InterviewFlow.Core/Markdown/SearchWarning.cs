using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Markdown;

/// <summary>
/// The one raw-HTML shape the original backend injects into stored reports
/// (docs/04-markdown-rendering.md §4.5). Per ADR-001 the native renderer
/// recognizes this block and renders it as a styled banner instead of executing HTML.
/// </summary>
/// <param name="Title">Bold lead-in, e.g. "Web search unavailable".</param>
/// <param name="Body">Remaining warning text after the title.</param>
public sealed record SearchWarning(string Title, string Body)
{
    private static readonly Regex Block = new(
        """<div class="search-warning">\s*⚠️\s*<strong>(?<title>[\s\S]*?)</strong>(?<body>[\s\S]*?)</div>\s*""",
        RegexOptions.Compiled);

    /// <summary>
    /// Splits a stored report into its leading search-warning (if any) and the
    /// markdown that follows. Reports written by the original app carry the block
    /// inline at the top; the port stores the same shape for data compatibility.
    /// </summary>
    public static (SearchWarning? Warning, string Markdown) Extract(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return (null, markdown);

        var m = Block.Match(markdown);
        if (!m.Success || m.Index != 0)
            return (null, markdown);

        var warning = new SearchWarning(
            m.Groups["title"].Value.Trim(),
            m.Groups["body"].Value.Trim());
        return (warning, markdown[m.Length..]);
    }
}
