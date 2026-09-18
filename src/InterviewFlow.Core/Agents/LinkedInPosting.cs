using System.Net;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// LinkedIn job postings (docs/05 §5.7). A posting URL —
///
///   https://www.linkedin.com/jobs/view/{id}/?trk=…
///   https://www.linkedin.com/jobs/view/{slug}-{id}
///   https://www.linkedin.com/jobs/search/?currentJobId={id}   (and /jobs/collections/…)
///
/// — serves a guest page that is server-rendered, so it strips to plenty of
/// text: the posting under the sign-in header, search bar, "Similar jobs"
/// rail and footer. It carries no JSON-LD, and its OpenGraph title is
/// "{Role} at {Company} — {City} | LinkedIn Jobs", so the page path stored
/// the chrome as the posting and named the employer "Retool — San Francisco,
/// CA | LinkedIn Jobs" (the Retool regression). The same posting is served
/// bare, no sign-in needed, from the guest job API:
///
///   https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{id}
///
/// an HTML fragment holding just the top card (title, employer, location,
/// pay range) and the description with its criteria list — the same markup
/// the full page embeds, so one parser reads both, and the canonical page is
/// the fallback when the guest endpoint rate-limits.
/// </summary>
public static partial class LinkedInPosting
{
    private const string GuestApi = "https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/";

    [GeneratedRegex("""<(?<tag>h[1-6])[^>]*class\s*=\s*["'][^"']*top-card-layout__title[^"']*["'][^>]*>(?<text>.*?)</\k<tag>>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRe();

    [GeneratedRegex("""<a[^>]*class\s*=\s*["'][^"']*topcard__org-name-link[^"']*["'][^>]*>(?<text>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CompanyRe();

    [GeneratedRegex("""<span[^>]*class\s*=\s*["'][^"']*topcard__flavor--bullet[^"']*["'][^>]*>(?<text>.*?)</span>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LocationRe();

    // A whole class token: "compensation__salary-range" wraps this block and its heading.
    [GeneratedRegex("""<div[^>]*class\s*=\s*["'](?:[^"']*\s)?compensation__salary(?=[\s"'])[^"']*["'][^>]*>(?<text>.*?)</div>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SalaryRe();

    [GeneratedRegex("""<h3[^>]*class\s*=\s*["'][^"']*description__job-criteria-subheader[^"']*["'][^>]*>(?<label>.*?)</h3>\s*<span[^>]*>(?<value>.*?)</span>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CriterionRe();

    [GeneratedRegex("""<div[^>]*class\s*=\s*["'][^"']*show-more-less-html__markup[^"']*["'][^>]*>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionOpenRe();

    [GeneratedRegex("""<div[^>]*class\s*=\s*["'][^"']*description__text[^"']*["'][^>]*>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionWrapperOpenRe();

    [GeneratedRegex(@"</?div\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DivTagRe();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagsRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();

    /// <summary>
    /// The numeric job id in a LinkedIn posting URL, or null when the URL is
    /// not one: another host, a company or search page with no job selected.
    /// Country subdomains (uk.linkedin.com) serve the same pages.
    /// </summary>
    public static string? JobId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host;
        if (!host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("jobs", StringComparison.OrdinalIgnoreCase))
        {
            // /jobs/view/{id} or /jobs/view/{slug}-{id}: the id is the digits after the last hyphen.
            if (segments.Length == 3 && segments[1].Equals("view", StringComparison.OrdinalIgnoreCase))
            {
                var last = segments[2];
                var hyphen = last.LastIndexOf('-');
                var id = hyphen >= 0 ? last[(hyphen + 1)..] : last;
                if (IsJobId(id))
                    return id;
            }

            // /jobs/search/?currentJobId={id}, /jobs/collections/{name}/?currentJobId={id}
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in query)
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].Equals("currentJobId", StringComparison.OrdinalIgnoreCase)
                    && IsJobId(pair[(eq + 1)..]))
                {
                    return pair[(eq + 1)..];
                }
            }
        }

        // Already the guest endpoint.
        if (segments.Length == 5 && segments[0].Equals("jobs-guest", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("jobPosting", StringComparison.OrdinalIgnoreCase) && IsJobId(segments[4]))
        {
            return segments[4];
        }

        return null;
    }

    /// <summary>Job ids are long decimal numbers (4466229826).</summary>
    private static bool IsJobId(string id) => id.Length >= 6 && id.All(char.IsAsciiDigit);

    /// <summary>The guest job API endpoint for a posting URL, or null when it isn't one.</summary>
    public static string? GuestApiUrl(string url) => JobId(url) is { } id ? GuestApi + id : null;

    /// <summary>The canonical guest page, without the tracking parameters of a shared link.</summary>
    public static string? PageUrl(string url) => JobId(url) is { } id ? $"https://www.linkedin.com/jobs/view/{id}/" : null;

    /// <summary>
    /// Renders the guest fragment or the full page. Returns null when there
    /// is no description block: a sign-in wall, a rate-limit page, or a
    /// posting that has been removed.
    /// </summary>
    public static PostingDetails? ParseHtml(string html)
    {
        if (html.Length == 0)
            return null;

        var description = DescriptionHtml(html);
        var body = description.Length > 0 ? HtmlText.FragmentToText(description) : "";
        if (body.Length == 0)
            return null;

        var title = Inline(TitleRe().Match(html).Groups["text"].Value);
        var company = Inline(CompanyRe().Match(html).Groups["text"].Value);

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Company", company);
        Add(lines, "Location", Inline(LocationRe().Match(html).Groups["text"].Value));
        Add(lines, "Base pay range", Inline(SalaryRe().Match(html).Groups["text"].Value));
        foreach (Match criterion in CriterionRe().Matches(html))
            Add(lines, Inline(criterion.Groups["label"].Value), Inline(criterion.Groups["value"].Value));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(body);
        return new PostingDetails(string.Join("\n", lines).Trim(), title, company);
    }

    /// <summary>
    /// The inner HTML of the description: the show-more/less markup block
    /// when present (both the fragment and the page use it), else the
    /// description__text wrapper. Found by walking the div nesting from the
    /// opening tag, since a description may itself contain divs.
    /// </summary>
    private static string DescriptionHtml(string html)
    {
        var open = DescriptionOpenRe().Match(html);
        if (!open.Success)
            open = DescriptionWrapperOpenRe().Match(html);
        return open.Success ? BalancedDivContent(html, open.Index + open.Length) : "";
    }

    private static string BalancedDivContent(string html, int start)
    {
        var depth = 1;
        foreach (Match tag in DivTagRe().Matches(html, start))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0)
                return html[start..tag.Index];
        }

        return html[start..]; // unterminated — take the rest rather than nothing
    }

    /// <summary>Tag-stripped, whitespace-collapsed, entity-decoded single line.</summary>
    private static string Inline(string html)
    {
        var text = TagsRe().Replace(html, " ");
        return WebUtility.HtmlDecode(WhitespaceRe().Replace(text, " ")).Trim();
    }

    private static void Add(List<string> lines, string label, string value)
    {
        if (value.Length == 0)
            return;
        lines.Add(label.Length == 0 ? value : $"{label}: {value}");
    }
}
