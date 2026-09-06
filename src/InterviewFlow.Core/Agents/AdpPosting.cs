using System.Text.Json;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// ADP WorkforceNow postings (docs/05 §5.7). A career-center job page,
///
///   https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html
///     ?cid={client}&amp;ccId={careerCenter}&amp;jobId={id}&amp;lang=en_US
///
/// is an Angular shell that strips to a browser-compatibility notice — no
/// JSON-LD, no OpenGraph, no frame — so every non-LLM step comes up empty and
/// the LLM is handed markup with no posting in it. The same career center
/// serves the requisition as JSON from its public staffing endpoint:
///
///   https://{host}/mascsr/default/careercenter/public/events/staffing/v1/job-requisitions/{id}
///     ?cid={client}&amp;ccId={careerCenter}&amp;locale={lang}
///
/// with <c>requisitionTitle</c>, <c>requisitionLocations</c>, <c>workLevelCode</c>
/// and an HTML <c>requisitionDescription</c>. The payload never names the
/// employer — ADP identifies the client only by its id — so Company stays
/// blank from this source.
/// </summary>
public static class AdpPosting
{
    private const string HostSuffix = ".adp.com";
    private const string PagePath = "/mascsr/default/mdf/recruitment/recruitment.html";
    private const string ApiPath = "/mascsr/default/careercenter/public/events/staffing/v1/job-requisitions/";

    /// <summary>
    /// Maps a career-center job page to its requisition endpoint, or null when
    /// the URL isn't one: another ADP product, a listing page with no jobId, or
    /// a page missing the client/career-center ids the endpoint needs. A URL
    /// that already is the endpoint is returned as is.
    /// </summary>
    public static string? ApiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        if (uri.AbsolutePath.StartsWith(ApiPath, StringComparison.OrdinalIgnoreCase))
            return uri.GetLeftPart(UriPartial.Query);

        if (!uri.AbsolutePath.Equals(PagePath, StringComparison.OrdinalIgnoreCase))
            return null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var cid = query["cid"];
        var ccId = query["ccId"];
        var jobId = query["jobId"];
        if (cid is not { Length: > 0 } || ccId is not { Length: > 0 }
            || jobId is not { Length: > 0 } || !jobId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var api = $"{uri.Scheme}://{uri.Authority}{ApiPath}{jobId}"
            + $"?cid={Uri.EscapeDataString(cid)}&ccId={Uri.EscapeDataString(ccId)}";
        return query["lang"] is { Length: > 0 } lang
            ? api + $"&locale={Uri.EscapeDataString(lang)}"
            : api;
    }

    /// <summary>
    /// Renders a requisition payload as posting text plus the role name.
    /// Returns null when it carries no description (dead id, error envelope).
    /// </summary>
    public static PostingDetails? ParseJobJson(string json)
    {
        JsonElement job;
        try
        {
            using var doc = JsonDocument.Parse(json);
            job = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (job.ValueKind != JsonValueKind.Object)
            return null;

        var description = HtmlText.FragmentToText(Str(job, "requisitionDescription"));
        if (description.Length == 0)
            return null;

        var title = Str(job, "requisitionTitle");

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Location", Location(job));
        Add(lines, "Employment type", Str(job, "workLevelCode", "shortName"));
        Add(lines, "Requisition", Str(job, "clientRequisitionID"));
        Add(lines, "Posted", Str(job, "postDate"));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(description);
        return new PostingDetails(string.Join("\n", lines).Trim(), title);
    }

    /// <summary>
    /// The first location's display name (" Portsmouth, NH, US" — ADP pads it
    /// with a leading space), falling back to city/state from its address.
    /// </summary>
    private static string Location(JsonElement job)
    {
        if (!job.TryGetProperty("requisitionLocations", out var locations)
            || locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() == 0)
            return "";

        var first = locations[0];
        var name = Str(first, "nameCode", "shortName").Trim();
        if (name.Length > 0)
            return name;

        var parts = new[]
        {
            Str(first, "address", "cityName"),
            Str(first, "address", "countrySubdivisionLevel1", "codeValue"),
        };
        return string.Join(", ", parts.Where(p => p.Length > 0));
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
