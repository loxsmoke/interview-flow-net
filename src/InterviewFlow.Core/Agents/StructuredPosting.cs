using System.Text.Json;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Structured-data recovery for client-rendered postings (docs/05 §5.7). When
/// tag-stripping a page yields nothing, the posting is usually still in the
/// shell as schema.org JobPosting JSON-LD (Greenhouse, Lever, SmartRecruiters,
/// most ATS templates) or, failing that, as OpenGraph meta tags. Both are read
/// from the HTML we already fetched — no extra request, no provider call.
/// </summary>
public static partial class StructuredPosting
{
    [GeneratedRegex("""<script[^>]*type\s*=\s*["']application/ld\+json["'][^>]*>(?<json>.*?)</script>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdBlockRe();

    [GeneratedRegex("""<meta[^>]*?(?:property|name)\s*=\s*["'](?:og:)?(?<key>title|description|site_name)["'][^>]*?content\s*=\s*["'](?<value>[^"']*)["']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MetaRe();

    [GeneratedRegex("""<meta[^>]*?content\s*=\s*["'](?<value>[^"']*)["'][^>]*?(?:property|name)\s*=\s*["'](?:og:)?(?<key>title|description|site_name)["']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MetaReversedRe();

    [GeneratedRegex("<title[^>]*>(?<title>.*?)</title>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRe();

    /// <summary>
    /// Best structured rendering of the page, or <see cref="PostingDetails.Empty"/>
    /// when there is none. Prefers JSON-LD (full description, named employer)
    /// over OpenGraph (usually truncated).
    /// </summary>
    public static PostingDetails Extract(string html) =>
        FromJsonLd(html) is { IsEmpty: false } jsonLd ? jsonLd : FromMetaTags(html);

    /// <summary>schema.org JobPosting embedded as ld+json.</summary>
    public static PostingDetails FromJsonLd(string html)
    {
        foreach (Match block in JsonLdBlockRe().Matches(html))
        {
            var raw = System.Net.WebUtility.HtmlDecode(block.Groups["json"].Value).Trim();
            if (raw.Length == 0)
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                foreach (var candidate in Flatten(doc.RootElement))
                {
                    if (!IsJobPosting(candidate))
                        continue;
                    var rendered = Render(candidate);
                    if (!rendered.IsEmpty)
                        return rendered;
                }
            }
        }

        return PostingDetails.Empty;
    }

    /// <summary>
    /// The employer named by the page shell, or "". og:site_name first, then the
    /// document title, which job boards write as "… at {Company}" (Greenhouse:
    /// "Job Application for Staff Software Engineer at CareDx, Inc.").
    /// </summary>
    public static string CompanyFromPage(string html)
    {
        if (MetaValue(html, "site_name") is { Length: > 0 } site)
            return site;

        var title = TitleRe().Match(html) is { Success: true } m
            ? System.Net.WebUtility.HtmlDecode(m.Groups["title"].Value).Trim()
            : "";
        var at = title.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return "";
        var company = title[(at + 4)..].Trim();
        // Guard against a title that merely ends in a preposition phrase.
        return company.Length is > 0 and <= 80 ? company : "";
    }

    private static string MetaValue(string html, string key)
    {
        foreach (var match in MetaRe().Matches(html).Concat(MetaReversedRe().Matches(html)))
        {
            if (!match.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    /// <summary>
    /// OpenGraph title + description — the last resort before the LLM. og:title
    /// is the role; the employer comes from <see cref="CompanyFromPage"/>.
    /// </summary>
    public static PostingDetails FromMetaTags(string html)
    {
        string title = "", description = "", site = "";
        foreach (var match in MetaRe().Matches(html).Concat(MetaReversedRe().Matches(html)))
        {
            var value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            if (value.Length == 0)
                continue;
            var key = match.Groups["key"].Value.ToLowerInvariant();
            if (key == "title")
                title = title.Length >= value.Length ? title : value;
            else if (key == "site_name")
                site = site.Length >= value.Length ? site : value;
            else
                description = description.Length >= value.Length ? description : value;
        }

        if (description.Length == 0)
            return PostingDetails.Empty;
        var text = title.Length == 0 ? description : $"{title}\n\n{description}";
        return new PostingDetails(text, title, site.Length > 0 ? site : CompanyFromPage(html));
    }

    /// <summary>A JSON-LD document may be an object, an array, or an @graph.</summary>
    private static IEnumerable<JsonElement> Flatten(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in Flatten(item))
                    yield return nested;
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        yield return element;
        if (element.TryGetProperty("@graph", out var graph))
        {
            foreach (var nested in Flatten(graph))
                yield return nested;
        }
    }

    private static bool IsJobPosting(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
            return false;
        return type.ValueKind switch
        {
            JsonValueKind.String => Matches(type),
            JsonValueKind.Array => type.EnumerateArray().Any(Matches),
            _ => false,
        };

        static bool Matches(JsonElement t) =>
            t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), "JobPosting", StringComparison.OrdinalIgnoreCase);
    }

    private static PostingDetails Render(JsonElement posting)
    {
        var description = HtmlText.FragmentToText(Text(posting, "description"));
        if (description.Length == 0)
            return PostingDetails.Empty;

        var title = Text(posting, "title");
        var company = Text(posting, "hiringOrganization", "name");

        var lines = new List<string>();
        AddLine(lines, "", title);
        AddLine(lines, "Company", company);
        AddLine(lines, "Location", Location(posting));
        AddLine(lines, "Employment type", Text(posting, "employmentType"));
        AddLine(lines, "Posted", Text(posting, "datePosted"));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(description);
        return new PostingDetails(string.Join("\n", lines).Trim(), title, company);
    }

    private static string Location(JsonElement posting)
    {
        if (!posting.TryGetProperty("jobLocation", out var location))
            return "";
        if (location.ValueKind == JsonValueKind.Array)
            location = location.EnumerateArray().FirstOrDefault();
        if (location.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new[]
        {
            Text(location, "address", "addressLocality"),
            Text(location, "address", "addressRegion"),
            Text(location, "address", "addressCountry"),
        };
        // iCIMS fills address fields it doesn't have with the literal
        // "UNAVAILABLE"; a placeholder is not a place.
        return string.Join(", ", parts.Where(p =>
            p.Length > 0 && !p.Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddLine(List<string> lines, string label, string value)
    {
        if (value.Length == 0)
            return;
        lines.Add(label.Length == 0 ? value : $"{label}: {value}");
    }

    private static string Text(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return "";
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? "",
            // addressCountry is sometimes {"@type":"Country","name":"US"}.
            JsonValueKind.Object => current.TryGetProperty("name", out var name)
                                    && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "",
            JsonValueKind.Array => string.Join(", ", current.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())),
            _ => "",
        };
    }
}
