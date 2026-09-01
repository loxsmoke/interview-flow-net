using System.Text.Json;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Workday-hosted postings (docs/05 §5.7). Every *.myworkdayjobs.com job page is
/// a client-rendered shell — stripping its tags yields no text at all — but the
/// same posting is served as JSON from the tenant's CXS endpoint:
///
///   https://{tenant}.wdN.myworkdayjobs.com/{site}/job/{rest}
///   → https://{tenant}.wdN.myworkdayjobs.com/wday/cxs/{tenant}/{site}/job/{rest}
///
/// Rewriting to that endpoint is what makes Workday postings resolvable without
/// a browser; it covers every employer on the platform, not just one tenant.
/// </summary>
public static partial class WorkdayPosting
{
    private const string WorkdayHostSuffix = ".myworkdayjobs.com";

    /// <summary>Locale segment some tenants insert ahead of the site id ("en-US").</summary>
    [GeneratedRegex("^[a-z]{2}(-[A-Za-z]{2})?$")]
    private static partial Regex LocaleSegmentRe();

    public static bool IsWorkdayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWorkdayHost(uri.Host);

    private static bool IsWorkdayHost(string host) =>
        host.EndsWith(WorkdayHostSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a public posting URL to its CXS JSON endpoint, or null when the URL
    /// isn't a Workday job page. Already-CXS URLs are returned unchanged.
    /// </summary>
    public static string? CxsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsWorkdayHost(uri.Host))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;
        if (segments[0].Equals("wday", StringComparison.OrdinalIgnoreCase))
            return uri.GetLeftPart(UriPartial.Path);

        // Tenant is the first host label: brooksauto.wd1.myworkdayjobs.com.
        var tenant = uri.Host.Split('.')[0];
        if (tenant.Length == 0)
            return null;

        var index = 0;
        if (LocaleSegmentRe().IsMatch(segments[0]))
            index = 1;
        if (segments.Length < index + 3)
            return null;

        var site = segments[index];
        // "job" on classic sites, "details" on the newer card layout; both are
        // served by the same CXS route under /job/.
        var kind = segments[index + 1];
        if (!kind.Equals("job", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("details", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = string.Join('/', segments.Skip(index + 2));
        return $"{uri.Scheme}://{uri.Authority}/wday/cxs/{tenant}/{site}/job/{rest}";
    }

    /// <summary>
    /// Renders a CXS response as posting text plus the role/employer names.
    /// Returns null when the payload carries no description (wrong endpoint,
    /// expired requisition, error body).
    /// </summary>
    public static PostingDetails? ParseCxsJson(string json)
    {
        JsonElement info, root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("jobPostingInfo", out var node))
                return null;
            info = node.Clone();
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        var description = HtmlText.FragmentToText(Str(info, "jobDescription"));
        if (description.Length == 0)
            return null;

        var title = Str(info, "title");
        var company = Str(root, "hiringOrganization", "name");

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Company", company);
        Add(lines, "Location", Str(info, "location") is { Length: > 0 } loc
            ? loc
            : Str(info, "jobRequisitionLocation", "descriptor"));
        Add(lines, "Employment type", Str(info, "timeType"));
        Add(lines, "Requisition", Str(info, "jobReqId"));
        Add(lines, "Posted", Str(info, "postedOn"));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(description);
        return new PostingDetails(string.Join("\n", lines).Trim(), title, company);
    }

    private static void Add(List<string> lines, string label, string value)
    {
        if (value.Length == 0)
            return;
        lines.Add(label.Length == 0 ? value : $"{label}: {value}");
    }

    private static string Str(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return "";
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
    }
}
