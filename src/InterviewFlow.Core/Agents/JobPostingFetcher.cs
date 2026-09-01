using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Core.Agents;

/// <summary>Outcome of resolving a pasted job posting.</summary>
/// <param name="Text">The posting text (input unchanged when it wasn't a URL).</param>
/// <param name="WasFetched">True when a URL was resolved.</param>
/// <param name="UsedLlmFallback">True when the LLM path produced the text.</param>
/// <param name="Error">Set when a URL could not be resolved.</param>
/// <param name="Company">Employer named by the source, or "" — Setup fills its
/// Company field from this.</param>
/// <param name="Position">Role named by the source, or "".</param>
public sealed record JobPostingResult(
    string Text, bool WasFetched = false, bool UsedLlmFallback = false, string? Error = null,
    string Company = "", string Position = "");

/// <summary>
/// Job-posting URL resolution (docs/05 §5.7, decided in the porting plan).
/// Four escalating steps, stopping at the first that yields enough text
/// (&#8805; 200 chars, the original's JS-rendered-page heuristic):
/// <list type="number">
///   <item>A job board's own API, when the URL is one it serves: Workday CXS
///         JSON, Greenhouse's board API.</item>
///   <item>Plain HTTP fetch behind the SSRF guard + a block-aware strip that
///         keeps paragraphs and bullets.</item>
///   <item>Structured data already in that HTML — JSON-LD, then OpenGraph.</item>
///   <item>One LLM call: extraction from the fetched HTML when we have it,
///         a server-side web fetch only when the request itself failed.</item>
/// </list>
/// Steps 1 and 3 exist because client-rendered postings (Workday and most ATS
/// SPAs) strip to zero characters — the earlier design went straight from that
/// to the LLM, which cannot fetch a named URL with a web-search tool.
/// </summary>
public static partial class JobPostingFetcher
{
    /// <summary>The original's "paste it instead" message for unresolvable pages.</summary>
    public const string CouldNotExtractMessage =
        "Couldn't extract the posting from this page — paste the text instead.";

    private const int ThinTextThreshold = 200;

    [GeneratedRegex(@"^https?://\S+$")]
    private static partial Regex BareUrlRe();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyleRe();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagsRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();

    /// <summary>True when the pasted text is a bare http(s) URL (main.py:222).</summary>
    public static bool LooksLikeUrl(string input) => BareUrlRe().IsMatch(input.Trim());

    /// <summary>
    /// SSRF guard (port of _is_safe_url): resolve the host and reject private,
    /// loopback, reserved, or link-local addresses.
    /// </summary>
    public static bool IsSafeUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            var addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses.Length == 0)
                return false;
            return addresses.All(IsGlobalAddress);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGlobalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6UniqueLocal && !ip.IsIPv6Multicast;

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => false,                                    // 10.0.0.0/8
            127 => false,                                   // loopback
            169 when b[1] == 254 => false,                  // link-local
            172 when b[1] >= 16 && b[1] <= 31 => false,      // 172.16.0.0/12
            192 when b[1] == 168 => false,                   // 192.168.0.0/16
            0 => false,                                      // "this network"
            >= 224 => false,                                 // multicast / reserved
            _ => true,
        };
    }

    /// <summary>
    /// Flat strip: the literal port of the original's _html_to_text, kept as the
    /// parity reference. The pipeline uses <see cref="HtmlText.PageToText"/>
    /// instead, so a resolved posting keeps its paragraphs and bullets.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var cleaned = ScriptOrStyleRe().Replace(html, "");
        var text = TagsRe().Replace(cleaned, " ");
        text = WhitespaceRe().Replace(text, " ").Trim();
        return WebUtility.HtmlDecode(text);
    }

    /// <summary>
    /// Resolves a pasted posting. Non-URL input is returned unchanged, so this
    /// is safe to call on every Save.
    /// </summary>
    public static async Task<JobPostingResult> ResolveAsync(
        AppConfig config, string rawInput, CancellationToken ct = default, HttpClient? http = null)
    {
        var input = rawInput.Trim();
        if (!LooksLikeUrl(input))
            return new JobPostingResult(rawInput);

        if (!IsSafeUrl(input))
            return new JobPostingResult(rawInput, Error: "That URL points to a private or unreachable address.");

        var client = http ?? ProviderHttp.Default;

        // 1. A board API, where the host has one. Cleaner than the page every
        //    time: no site chrome, and the employer is named outright.
        if (await FromBoardApiAsync(client, input, ct) is { } fromApi)
            return fromApi;

        // 2. Plain fetch, then a block-aware strip so the posting keeps its
        //    headings and bullet lists. Structured metadata is read either way:
        //    even a page with plenty of body text names the role and employer
        //    only in its JSON-LD or its <title>, and Setup fills Company/
        //    Position from that.
        var html = await GetStringAsync(client, input, "text/html", ct);
        var structured = html.Length > 0 ? StructuredPosting.Extract(html) : PostingDetails.Empty;
        var text = HtmlText.PageToText(html);
        if (text.Length >= ThinTextThreshold)
        {
            var named = structured with { Text = text };
            if (named.Company.Length == 0)
                named = named with { Company = StructuredPosting.CompanyFromPage(html) };
            return Resolved(named);
        }

        // 3. Thin strip → the page is JS-rendered, but the posting is usually
        //    still in the shell as JSON-LD or OpenGraph metadata.
        if (structured.Text.Length >= ThinTextThreshold)
            return Resolved(structured);

        // 4. Last resort: one LLM call.
        var provider = ProviderRouter.ResolveProvider(config);
        if (provider == "ollama")
        {
            // Ollama can't help either way: its tool loop fetches locally over
            // plain HTTP — the same thing that just failed — and a page of
            // markup overruns the context a local model runs with. Steps 1–3
            // are what cover JS-rendered pages for local setups.
            return new JobPostingResult(rawInput, Error: CouldNotExtractMessage);
        }

        try
        {
            var fetched = await FetchViaLlmAsync(config, input, html, ct, http);
            if (fetched.Trim().Length >= ThinTextThreshold)
            {
                // Keep whatever the metadata named even though the text is the
                // model's — the two describe the same posting.
                return new JobPostingResult(fetched.Trim(), WasFetched: true, UsedLlmFallback: true,
                    Company: structured.Company, Position: structured.Title);
            }
        }
        catch (Exception ex)
        {
            Logging.DiagnosticLog.Warn("fetch", $"LLM fallback failed: {ex.Message}");
        }

        return new JobPostingResult(rawInput, Error: CouldNotExtractMessage);
    }

    /// <summary>
    /// Board-API handlers, tried in turn. A miss (unknown host, dead id, empty
    /// body) falls through to the page fetch rather than failing the resolve.
    /// </summary>
    private static async Task<JobPostingResult?> FromBoardApiAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        (string? Endpoint, Func<string, PostingDetails?> Parse)[] boards =
        [
            (WorkdayPosting.CxsUrl(url), WorkdayPosting.ParseCxsJson),
            (GreenhousePosting.ApiUrl(url), GreenhousePosting.ParseJobJson),
        ];

        foreach (var (endpoint, parse) in boards)
        {
            if (endpoint is null)
                continue;
            var json = await GetStringAsync(client, endpoint, "application/json", ct);
            if (json.Length > 0 && parse(json) is { } posting
                && posting.Text.Length >= ThinTextThreshold)
            {
                return Resolved(posting);
            }

            Logging.DiagnosticLog.Warn("fetch", $"board api yielded nothing: {endpoint}");
        }

        return null;
    }

    private static JobPostingResult Resolved(PostingDetails details) =>
        new(details.Text, WasFetched: true, Company: details.Company, Position: details.Title);

    /// <summary>GET as a browser would; "" on any failure (logged, never thrown).</summary>
    private static async Task<string> GetStringAsync(
        HttpClient client, string url, string accept, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36");
            request.Headers.Add("Accept", accept);
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Logging.DiagnosticLog.Warn("fetch", $"GET {url} failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>Cap on HTML handed to the model — ~40 k tokens of markup.</summary>
    private const int MaxHtmlChars = 160_000;

    private static async Task<string> FetchViaLlmAsync(
        AppConfig config, string url, string html, CancellationToken ct, HttpClient? http)
    {
        // With the page in hand, extraction beats retrieval: no provider here
        // offers a reliable "fetch this exact URL" tool, and asking a web-SEARCH
        // tool to do it returns zero tool calls and a shrug.
        var haveHtml = html.Length > 0;
        var prompt = haveHtml
            ? $"Below is the raw HTML of {url}. Extract the job posting from it.\n\n" +
              "Return ONLY the posting content — title, company, location, responsibilities, " +
              "requirements, compensation and any other posting details, as plain text. " +
              "Preserve the wording. No commentary, no summary. If the HTML contains no job " +
              "posting, reply with exactly: NO_POSTING\n\n" +
              "--- HTML ---\n" +
              (html.Length > MaxHtmlChars ? html[..MaxHtmlChars] : html)
            : $"Fetch this URL and return the job posting text verbatim: {url}\n\n" +
              "Return ONLY the posting content — title, responsibilities, requirements, " +
              "compensation and any other details on the page. No commentary, no summary.";
        var options = new QueryOptions(
            haveHtml
                ? "You extract job postings from raw HTML and return them as plain text, verbatim."
                : "You retrieve web pages and return their text content exactly as published.",
            UseWebSearch: !haveHtml)
        {
            Temperature = TemperatureSetting.Explicit(0.0),
        };

        var text = "";
        await foreach (var evt in ProviderRouter.StreamQueryAsync(config, prompt, options, "job-posting-fetch", ct, http))
        {
            if (evt is CompleteEvent complete)
                text = complete.Text;
        }

        return text.Trim() == "NO_POSTING" ? "" : text;
    }
}
