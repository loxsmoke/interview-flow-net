namespace InterviewFlow.Core.Agents;

/// <summary>
/// iCIMS-hosted postings (docs/05 §5.7). A tenant's job URL,
///
///   https://{tenant}.icims.com/jobs/{id}/{slug}/job?…
///
/// serves the employer's corporate site wrapped around the posting, which is
/// loaded into a same-host frame: the same URL with <c>in_iframe=1</c>. The
/// wrapper is site navigation that strips to well past the thin-page threshold,
/// so the plain-fetch path would accept the menus as the posting. The frame
/// document carries the posting as schema.org JobPosting JSON-LD, employer
/// named, so it is read like a board API: fetched directly, parsed as structured
/// data, and a miss falls through to the page.
/// </summary>
public static class IcimsPosting
{
    private const string HostSuffix = ".icims.com";

    /// <summary>
    /// The frame-document URL for a posting URL, or null when the URL isn't an
    /// iCIMS job page. A URL that already asks for the frame is returned as is.
    /// </summary>
    public static string? FrameUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        // /jobs/{id}/… — a numeric requisition id right after "jobs"; search
        // and intro pages live under /jobs/ too but carry no id.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("jobs", StringComparison.OrdinalIgnoreCase)
            || segments[1].Length == 0 || !segments[1].All(char.IsAsciiDigit))
            return null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var withoutFragment = uri.GetLeftPart(UriPartial.Query);
        if (query["in_iframe"] == "1")
            return withoutFragment;
        return withoutFragment + (uri.Query.Length > 1 ? "&" : "?") + "in_iframe=1";
    }

    /// <summary>
    /// The posting from the frame document's JSON-LD, or null when the page
    /// carries none (an error page, a tenant without structured data).
    /// </summary>
    public static PostingDetails? ParseFrameHtml(string html) =>
        StructuredPosting.FromJsonLd(html) is { IsEmpty: false } posting ? posting : null;
}
