using System.Text.Json;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// SmartRecruiters-hosted postings (docs/05 §5.7). A posting URL,
///
///   https://jobs.smartrecruiters.com/{company}/{id}-{slug}
///
/// is server-rendered, so the page strips to plenty of text — the posting
/// with the site's cookie banner, browser-support notice, "I'm interested"
/// buttons and footer around it. It carries schema.org microdata but no
/// JSON-LD, and its &lt;title&gt; is "{Company} {Role} | SmartRecruiters", so the
/// page path stored the chrome and left Company blank. The same posting is
/// served, no key needed, from the public Posting API:
///
///   https://api.smartrecruiters.com/v1/companies/{company}/postings/{id}
///
/// with <c>name</c>, <c>company.name</c>, <c>location.fullLocation</c>,
/// <c>typeOfEmployment.label</c>, <c>refNumber</c> and the ad as titled HTML
/// sections under <c>jobAd.sections</c> (company description, job description,
/// qualifications, additional information).
/// </summary>
public static class SmartRecruitersPosting
{
    private const string ApiHost = "api.smartrecruiters.com";

    /// <summary>Hosts that serve posting pages: the job board and hosted career sites.</summary>
    private static readonly string[] PageHosts = ["jobs.smartrecruiters.com", "careers.smartrecruiters.com"];

    /// <summary>
    /// Maps a posting URL to its Posting API endpoint, or null when the URL
    /// isn't a SmartRecruiters job page: another host, a company's listing page
    /// with no posting id, or a one-click-apply route. A URL that already is
    /// the endpoint is returned as is.
    /// </summary>
    public static string? ApiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            // /v1/companies/{company}/postings/{id}
            return segments.Length == 5
                   && segments[0].Equals("v1", StringComparison.OrdinalIgnoreCase)
                   && segments[1].Equals("companies", StringComparison.OrdinalIgnoreCase)
                   && segments[3].Equals("postings", StringComparison.OrdinalIgnoreCase)
                   && IsPostingId(segments[4])
                ? uri.GetLeftPart(UriPartial.Path)
                : null;
        }

        if (!PageHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return null;

        // /{company}/{id}-{slug}: the id is the digits ahead of the first hyphen.
        if (segments.Length != 2)
            return null;
        var company = segments[0];
        var id = segments[1].Split('-')[0];
        if (company.Length == 0 || !IsPostingId(id))
            return null;

        return $"https://{ApiHost}/v1/companies/{Uri.EscapeDataString(company)}/postings/{id}";
    }

    /// <summary>Posting ids are long decimal numbers (743999…, 744000…).</summary>
    private static bool IsPostingId(string id) =>
        id.Length >= 6 && id.All(char.IsAsciiDigit);

    /// <summary>
    /// Renders a Posting API payload. Returns null when it carries no ad text
    /// (dead id, unpublished posting, error envelope).
    /// </summary>
    public static PostingDetails? ParsePostingJson(string json)
    {
        JsonElement posting;
        try
        {
            using var doc = JsonDocument.Parse(json);
            posting = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (posting.ValueKind != JsonValueKind.Object)
            return null;

        var body = Sections(posting);
        if (body.Length == 0)
            return null;

        var title = Str(posting, "name");
        var company = Str(posting, "company", "name");

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Company", company);
        Add(lines, "Location", Location(posting));
        Add(lines, "Employment type", Str(posting, "typeOfEmployment", "label"));
        Add(lines, "Experience level", Str(posting, "experienceLevel", "label"));
        Add(lines, "Requisition", Str(posting, "refNumber"));
        Add(lines, "Posted", Str(posting, "releasedDate"));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(body);
        return new PostingDetails(string.Join("\n", lines).Trim(), title, company);
    }

    /// <summary>
    /// The ad's sections in the order the payload lists them, each as its title
    /// on its own line over its text. Sections with no text are skipped.
    /// </summary>
    private static string Sections(JsonElement posting)
    {
        if (!posting.TryGetProperty("jobAd", out var ad) || ad.ValueKind != JsonValueKind.Object
            || !ad.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        foreach (var section in sections.EnumerateObject())
        {
            var text = HtmlText.FragmentToText(Str(section.Value, "text"));
            if (text.Length == 0)
                continue;
            var heading = Str(section.Value, "title");
            parts.Add(heading.Length > 0 ? $"{heading}\n{text}" : text);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// "Mountain View, CA, United States (Hybrid)": the payload's own full
    /// location, or city/region/country when it lacks one, with the workplace
    /// flags the API keeps as booleans.
    /// </summary>
    private static string Location(JsonElement posting)
    {
        if (!posting.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object)
            return "";

        var place = Str(location, "fullLocation");
        if (place.Length == 0)
        {
            place = string.Join(", ", new[]
            {
                Str(location, "city"),
                Str(location, "region"),
                Str(location, "country").ToUpperInvariant(),
            }.Where(p => p.Length > 0));
        }

        var workplace = Flag(location, "remote") ? "Remote" : Flag(location, "hybrid") ? "Hybrid" : "";
        if (workplace.Length == 0)
            return place;
        return place.Length == 0 ? workplace : $"{place} ({workplace})";
    }

    private static bool Flag(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

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
