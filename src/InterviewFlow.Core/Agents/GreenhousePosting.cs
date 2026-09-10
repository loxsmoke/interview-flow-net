using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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
///
/// A corporate careers site can embed the same board under its own host
/// (Roblox: <c>careers.roblox.com/jobs/8171506?gh_jid=8171506</c>). The
/// <c>gh_jid</c> parameter is Greenhouse's job id; the board token is not in
/// the URL, but the same API serves the posting once it is known. Job ids are
/// global across Greenhouse, so a guessed token is safe to try: the API answers
/// 404 for any board the job is not on, and the resolve falls through.
/// </summary>
public static partial class GreenhousePosting
{
    private const string GreenhouseHostSuffix = "greenhouse.io";

    /// <summary>The job id Greenhouse's embed script puts on the host page's URL.</summary>
    private const string EmbeddedJobIdParameter = "gh_jid";

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRe();

    /// <summary>
    /// Board token from an embedded board's own references: the embed script
    /// (<c>boards.greenhouse.io/embed/job_board/js?for={board}</c>), the
    /// application frame (<c>…/embed/job_app?for={board}&amp;token={id}</c>),
    /// or a board-hosted link (<c>boards.greenhouse.io/{board}/jobs/{id}</c>).
    /// </summary>
    [GeneratedRegex(
        @"greenhouse\.io/(?:embed/job_(?:board|app)(?:/js)?\?(?:[^""'\s&]*&(?:amp;)?)*for=(?<board>[A-Za-z0-9_-]+)"
        + @"|(?<board>[A-Za-z0-9_-]+)/jobs/\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmbedBoardRe();

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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (!IsGreenhouseHost(uri.Host))
        {
            // An embedded board on the employer's own host. The token is
            // almost always the company's domain name (careers.roblox.com →
            // "roblox"), and a wrong guess costs one 404.
            var embeddedId = EmbeddedJobId(uri);
            return embeddedId is null ? null : ApiUrlFor(BoardFromHost(uri.Host), embeddedId);
        }

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
    /// The board-API endpoint for an embedded posting whose page names its
    /// board, or null. Read from the page after the host-derived guess in
    /// <see cref="ApiUrl"/> missed: the embed script and application frame
    /// carry the token exactly, and the frame carries the job id too, so this
    /// also covers a host page with no <c>gh_jid</c> of its own.
    /// </summary>
    public static string? ApiUrlFromPage(string html, string pageUrl)
    {
        if (html.Length == 0 || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || IsGreenhouseHost(uri.Host))
            return null;

        var board = "";
        foreach (Match match in EmbedBoardRe().Matches(html))
        {
            var candidate = match.Groups["board"].Value;
            // The /{board}/jobs/{id} form also matches "/embed/job_app?…" hosts
            // and the "v1/boards" API path; those are not boards.
            if (candidate.Equals("embed", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("boards", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            board = candidate;
            break;
        }

        if (board.Length == 0)
            return null;

        var id = EmbeddedJobId(uri) ?? FrameToken(html);
        return id is null ? null : ApiUrlFor(board, id);
    }

    private static string? ApiUrlFor(string board, string id) =>
        board.Length == 0 ? null : $"https://boards-api.greenhouse.io/v1/boards/{board}/jobs/{id}";

    /// <summary>The numeric gh_jid on the URL, or null.</summary>
    private static string? EmbeddedJobId(Uri uri)
    {
        var id = System.Web.HttpUtility.ParseQueryString(uri.Query)[EmbeddedJobIdParameter];
        return id is { Length: > 0 } && DigitsRe().IsMatch(id) ? id : null;
    }

    [GeneratedRegex(@"greenhouse\.io/embed/job_app\?[^""'\s]*?token=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FrameTokenRe();

    private static string? FrameToken(string html) =>
        FrameTokenRe().Match(html) is { Success: true } m ? m.Groups["id"].Value : null;

    /// <summary>
    /// The organisation label of a host: the label before the public suffix,
    /// where a two-part suffix is a short second-level label under a two-letter
    /// country code (co.uk, com.au). careers.roblox.com → roblox,
    /// jobs.acme.co.uk → acme, acme.com → acme. "" when the host has no such label.
    /// </summary>
    internal static string BoardFromHost(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
            return "";
        var suffix = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3 ? 2 : 1;
        return labels.Length > suffix ? labels[^(suffix + 1)].ToLowerInvariant() : "";
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
