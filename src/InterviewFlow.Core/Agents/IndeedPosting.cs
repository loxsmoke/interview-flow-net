using System.Text.Json;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Indeed postings (docs/05 §5.7). A posting URL —
///
///   https://www.indeed.com/viewjob?jk={jk}&amp;tk=…&amp;from=…     (job-alert email links)
///   https://www.indeed.com/jobs?…&amp;vjk={jk}                     (search with a job selected)
///   https://www.indeed.com/m/viewjob?jk={jk}, /rc/clk?jk={jk}     (mobile, click-through)
///
/// — is behind a bot check: every plain fetch of the page gets a 403
/// "Security Check" challenge, whatever the user agent, so the page path had
/// nothing and the LLM step had no page to extract from (the Indeed
/// regression). Two endpoints on the same host still answer a plain request:
///
///   /viewjob?jk={jk}&amp;spa=1     — the page's own JSON: title, employer, location,
///                                pay, job type and the sanitised description HTML;
///                                challenged about half the time, so it is tried twice
///   /rpc/jobdescs?jks={jk}     — {jk: description HTML}, reliable but nameless
///
/// The country sites (uk.indeed.com, ca.indeed.com) serve the same endpoints
/// for their own job keys, so requests stay on the URL's host.
/// </summary>
public static class IndeedPosting
{
    /// <summary>The job key from a posting URL, or null when the URL isn't an Indeed posting.</summary>
    public static string? JobKey(string url)
    {
        if (Host(url) is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if ((key.Equals("jk", StringComparison.OrdinalIgnoreCase)
                 || key.Equals("vjk", StringComparison.OrdinalIgnoreCase)
                 || key.Equals("jks", StringComparison.OrdinalIgnoreCase))
                && IsJobKey(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The Indeed host of the URL (kept for country sites), or null for any other site.</summary>
    private static string? Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host;
        var isIndeed = host.Equals("indeed.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".indeed.com", StringComparison.OrdinalIgnoreCase);
        return isIndeed ? host : null;
    }

    /// <summary>Job keys are 16 lowercase hex digits (fb7d36a03f4830f4).</summary>
    private static bool IsJobKey(string key) =>
        key.Length == 16 && key.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>The page's JSON endpoint for a posting URL, or null when it isn't one.</summary>
    public static string? SpaUrl(string url) =>
        JobKey(url) is { } jk ? $"https://{Host(url)}/viewjob?jk={jk}&spa=1" : null;

    /// <summary>The description-only RPC for a posting URL, or null when it isn't one.</summary>
    public static string? DescriptionsUrl(string url) =>
        JobKey(url) is { } jk ? $"https://{Host(url)}/rpc/jobdescs?jks={jk}" : null;

    /// <summary>
    /// Renders the <c>spa=1</c> payload. Null when it isn't the success
    /// envelope with a description (a challenge page is HTML, not JSON).
    /// </summary>
    public static PostingDetails? ParseSpaJson(string json)
    {
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var found) || found.ValueKind != JsonValueKind.Object)
                return null;
            body = found.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        var info = Element(body, "jobInfoWrapperModel", "jobInfoModel");
        var text = HtmlText.FragmentToText(Str(info, "sanitizedJobDescription"));
        if (text.Length == 0)
            return null;

        var header = Element(info, "jobInfoHeaderModel");
        var title = Str(header, "jobTitle");
        if (title.Length == 0)
            title = Str(body, "jobTitle");
        var company = Str(header, "companyName");

        var location = Str(header, "formattedLocation");
        if (Flag(header, "remoteLocation") && !location.Contains("remote", StringComparison.OrdinalIgnoreCase))
            location = location.Length == 0 ? "Remote" : $"{location} (Remote)";

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Company", company);
        Add(lines, "Location", location);
        Add(lines, "Pay", Str(Element(body, "salaryInfoModel"), "salaryText"));
        Add(lines, "Job type", Str(Element(info, "jobMetadataHeaderModel"), "jobType"));
        Add(lines, "Benefits", Benefits(body));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(text);
        return new PostingDetails(string.Join("\n", lines).Trim(), title, company);
    }

    /// <summary>
    /// Renders the <c>rpc/jobdescs</c> payload: the first description in the
    /// map. It never names the role or the employer, so Title and Company stay
    /// blank for the user to fill in.
    /// </summary>
    public static PostingDetails? ParseDescriptionsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                    continue;
                var text = HtmlText.FragmentToText(entry.Value.GetString() ?? "");
                if (text.Length > 0)
                    return new PostingDetails(text);
            }
        }
        catch (JsonException)
        {
            // Not JSON — a challenge page or an error document.
        }

        return null;
    }

    /// <summary>"Health insurance, 401(k), Paid time off": the employer's listed benefits, in order.</summary>
    private static string Benefits(JsonElement body)
    {
        var benefits = Element(body, "benefitsModel", "benefits");
        if (benefits.ValueKind != JsonValueKind.Array)
            return "";
        var labels = benefits.EnumerateArray().Select(b => Str(b, "label")).Where(l => l.Length > 0);
        return string.Join(", ", labels);
    }

    private static JsonElement Element(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return default;
        }

        return current;
    }

    private static string Str(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    }

    private static bool Flag(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static void Add(List<string> lines, string label, string value)
    {
        if (value.Length == 0)
            return;
        lines.Add(label.Length == 0 ? value : $"{label}: {value}");
    }
}
