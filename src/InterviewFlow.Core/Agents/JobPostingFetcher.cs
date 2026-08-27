using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Core.Agents;

/// <summary>Outcome of resolving a pasted job posting.</summary>
/// <param name="Text">The posting text (input unchanged when it wasn't a URL).</param>
/// <param name="WasFetched">True when a URL was resolved.</param>
/// <param name="UsedLlmFallback">True when the LLM web-fetch path produced the text.</param>
/// <param name="Error">Set when a URL could not be resolved.</param>
public sealed record JobPostingResult(
    string Text, bool WasFetched = false, bool UsedLlmFallback = false, string? Error = null);

/// <summary>
/// Job-posting URL resolution (docs/05 §5.7, decided in the porting plan):
/// plain HTTP fetch behind an SSRF guard, then — when extraction is thin
/// (&lt; 200 chars, the original's JS-rendered-page heuristic) — a one-shot LLM
/// web-fetch instead of the original's headless Chromium.
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

    /// <summary>Strip tags/scripts and collapse whitespace (port of _html_to_text).</summary>
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
        var text = "";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, input);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            text = HtmlToText(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            Logging.DiagnosticLog.Warn("fetch", $"job posting fetch failed: {ex.Message}");
        }

        if (text.Length >= ThinTextThreshold)
            return new JobPostingResult(text, WasFetched: true);

        // Thin extraction → the page is likely JS-rendered. Ask the active
        // provider to fetch it server-side (no local browser dependency).
        var provider = ProviderRouter.ResolveProvider(config);
        if (provider == "ollama")
        {
            // Ollama's tool loop fetches locally over plain HTTP — the same
            // thing that just failed. Don't burn a call on it.
            return new JobPostingResult(rawInput, Error: CouldNotExtractMessage);
        }

        try
        {
            var fetched = await FetchViaLlmAsync(config, input, ct, http);
            if (fetched.Trim().Length >= ThinTextThreshold)
                return new JobPostingResult(fetched.Trim(), WasFetched: true, UsedLlmFallback: true);
        }
        catch (Exception ex)
        {
            Logging.DiagnosticLog.Warn("fetch", $"LLM web-fetch fallback failed: {ex.Message}");
        }

        return new JobPostingResult(rawInput, Error: CouldNotExtractMessage);
    }

    private static async Task<string> FetchViaLlmAsync(
        AppConfig config, string url, CancellationToken ct, HttpClient? http)
    {
        var prompt =
            $"Fetch this URL and return the job posting text verbatim: {url}\n\n" +
            "Return ONLY the posting content — title, responsibilities, requirements, " +
            "compensation and any other details on the page. No commentary, no summary.";
        var options = new QueryOptions(
            "You retrieve web pages and return their text content exactly as published.",
            UseWebSearch: true)
        {
            Temperature = TemperatureSetting.Explicit(0.0),
        };

        var text = "";
        await foreach (var evt in ProviderRouter.StreamQueryAsync(config, prompt, options, "job-posting-fetch", ct, http))
        {
            if (evt is CompleteEvent complete)
                text = complete.Text;
        }

        return text;
    }
}
