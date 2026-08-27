using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Local web tools for the Ollama loop (port of _search_duckduckgo/_fetch_url).
/// The result-string prefixes ("Web search failed", "No results found") are
/// load-bearing: the search-status classifier keys on them.
/// </summary>
public static partial class WebTools
{
    [GeneratedRegex("""<a[^>]*class="[^"]*result__a[^"]*"[^>]*href="(?<href>[^"]+)"[^>]*>(?<title>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex DdgResult();

    [GeneratedRegex("""<a[^>]*class="[^"]*result__snippet[^"]*"[^>]*>(?<body>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex DdgSnippet();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlocks();

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("&[a-zA-Z]+;")]
    private static partial Regex Entities();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// DuckDuckGo text search via the html endpoint (the ddgs library isn't
    /// available in .NET). Formats results exactly like the original.
    /// </summary>
    public static async Task<string> SearchDuckDuckGoAsync(
        string query, HttpClient? http = null, CancellationToken ct = default, int maxResults = 5)
    {
        http ??= ProviderHttp.Default;
        string html;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://html.duckduckgo.com/html/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; interview-flow/1.0)");
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception exc)
        {
            return $"Web search failed for '{query}': {exc.Message}";
        }

        var links = DdgResult().Matches(html);
        var snippets = DdgSnippet().Matches(html);
        if (links.Count == 0)
            return $"No results found for query: {query}";

        var lines = new List<string> { $"Search results for '{query}':", "" };
        for (var i = 0; i < Math.Min(maxResults, links.Count); i++)
        {
            var title = CleanFragment(links[i].Groups["title"].Value);
            var href = WebUtility.HtmlDecode(links[i].Groups["href"].Value);
            var body = i < snippets.Count ? CleanFragment(snippets[i].Groups["body"].Value) : "";
            lines.Add($"{i + 1}. {(title.Length > 0 ? title : "No title")}");
            lines.Add($"   URL: {href}");
            lines.Add($"   {body}");
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Fetch a URL and return stripped text, capped at 8 000 chars.</summary>
    public static async Task<string> FetchUrlAsync(
        string url, HttpClient? http = null, CancellationToken ct = default)
    {
        http ??= ProviderHttp.Default;
        string text;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; interview-flow/1.0)");
            using var response = await http.SendAsync(request, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception exc)
        {
            return $"Failed to fetch {url}: {exc.Message}";
        }

        text = StyleBlocks().Replace(text, " ");
        text = ScriptBlocks().Replace(text, " ");
        text = Tags().Replace(text, " ");
        text = Entities().Replace(text, " ");
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length > 8000 ? text[..8000] : text;
    }

    private static string CleanFragment(string html) =>
        WebUtility.HtmlDecode(Tags().Replace(html, "")).Trim();
}
