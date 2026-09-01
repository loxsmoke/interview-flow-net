using System.Net;
using System.Text.Json;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Greenhouse-hosted postings (docs/05 §5.7). The public page is server-rendered,
/// so scraping it "works" — but it drags in the site chrome ("Back to jobs",
/// "Apply"), carries no JSON-LD, and names the employer nowhere a parser can
/// reach it. The board API returns the posting on its own:
///
///   https://job-boards.greenhouse.io/{board}/jobs/{id}
///   → https://boards-api.greenhouse.io/v1/boards/{board}/jobs/{id}
///
/// with <c>title</c>, <c>company_name</c>, <c>location.name</c> and an
/// entity-escaped HTML <c>content</c> body.
/// </summary>
public static class GreenhousePosting
{
    private const string GreenhouseHostSuffix = "greenhouse.io";

    private static bool IsGreenhouseHost(string host) =>
        host.Equals(GreenhouseHostSuffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + GreenhouseHostSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a posting URL to its board-API endpoint, or null when the URL isn't
    /// a Greenhouse job page. Handles the classic and job-boards hosts, the EU
    /// region, and the embedded ?for=&amp;token= form.
    /// </summary>
    public static string? ApiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsGreenhouseHost(uri.Host))
            return null;

        // boards-api hosts the EU region under its own subdomain.
        var api = uri.Host.Contains(".eu.", StringComparison.OrdinalIgnoreCase)
            ? "boards-api.eu.greenhouse.io"
            : "boards-api.greenhouse.io";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Embedded application form: /embed/job_app?for={board}&token={id}
        if (segments.Length > 0 && segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var board = query["for"];
            var token = query["token"];
            return board is { Length: > 0 } && token is { Length: > 0 }
                ? $"https://{api}/v1/boards/{board}/jobs/{token}"
                : null;
        }

        // /{board}/jobs/{id}, optionally behind an /embed/ or locale segment.
        var jobs = Array.FindIndex(segments, s => s.Equals("jobs", StringComparison.OrdinalIgnoreCase));
        if (jobs < 1 || jobs + 1 >= segments.Length)
            return null;
        var id = segments[jobs + 1].Split('?')[0];
        return id.Length == 0 ? null : $"https://{api}/v1/boards/{segments[jobs - 1]}/jobs/{id}";
    }

    /// <summary>
    /// Renders a board-API job payload. Returns null when it carries no body
    /// (wrong id, unpublished posting, error envelope).
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

        // content is HTML escaped once inside the JSON string.
        var body = HtmlText.FragmentToText(WebUtility.HtmlDecode(Str(job, "content")));
        if (body.Length == 0)
            return null;

        var title = Str(job, "title");
        var company = Str(job, "company_name");

        var lines = new List<string>();
        Add(lines, "", title);
        Add(lines, "Company", company);
        Add(lines, "Location", Str(job, "location", "name"));
        Add(lines, "Requisition", Str(job, "requisition_id"));
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(body);
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
