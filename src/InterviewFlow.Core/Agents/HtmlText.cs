using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// HTML → readable text for job postings (docs/05 §5.7). Block tags become line
/// breaks and list items become bullets, because a posting is mostly headings
/// and bullet lists — flattening it to one paragraph (which the original's
/// <c>_html_to_text</c> does) loses the structure the AI agents and the user
/// both read.
/// </summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|noscript|svg)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NonContentRe();

    // Removed in a second pass, once the scripts are gone: a script that quotes
    // an HTML e-mail template holds a literal "</head>" (Jibe's i18n bundle),
    // which would end the head early and leak the rest of the script as text.
    [GeneratedRegex(@"<head\b[^>]*>.*?</head>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadRe();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagsRe();

    // Everything horizontal, including NBSP — newlines are the payload here.
    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex InlineSpaceRe();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRunRe();

    /// <summary>A description fragment: the markup is already content-only.</summary>
    public static string FragmentToText(string html) => Convert(html);

    /// <summary>
    /// A whole page: drop the parts that are never posting text first. Site
    /// chrome ("Back to jobs", cookie banners) still survives — a job-board
    /// handler that hits the site's own API avoids that, this is the fallback.
    /// </summary>
    public static string PageToText(string html) =>
        html.Length == 0 ? "" : Convert(HeadRe().Replace(NonContentRe().Replace(html, "\n"), "\n"));

    private static string Convert(string html)
    {
        if (html.Length == 0)
            return "";

        var withBreaks = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                withBreaks.Append(html[i++]);
                continue;
            }

            var end = html.IndexOf('>', i);
            if (end < 0)
                break;
            var raw = html[(i + 1)..end];
            var closing = raw.StartsWith('/');
            var tag = raw.TrimStart('/').Split([' ', '\t', '\r', '\n', '/'], 2)[0].ToLowerInvariant();
            withBreaks.Append(tag switch
            {
                // Only the opening <li> earns a bullet; </li> is just a break.
                "li" when !closing => "\n• ",
                // A heading gets a blank line above it so sections stay apart.
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" when !closing => "\n\n",
                "li" or "br" or "p" or "div" or "tr" or "ul" or "ol" or "table"
                    or "section" or "article" or "header" or "footer" or "blockquote"
                    or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "\n",
                _ => "",
            });
            i = end + 1;
        }

        var text = WebUtility.HtmlDecode(TagsRe().Replace(withBreaks.ToString(), ""));
        text = InlineSpaceRe().Replace(text, " ");
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        return BlankRunRe().Replace(string.Join('\n', ReattachBullets(lines)), "\n\n").Trim();
    }

    /// <summary>
    /// Postings nest the item text in its own block (<c>&lt;li&gt;&lt;p&gt;…</c>),
    /// which strands the bullet on a line of its own. Rejoin it with the text it
    /// belongs to, and drop bullets that never had any.
    /// </summary>
    private static IEnumerable<string> ReattachBullets(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] != "•")
            {
                yield return lines[i];
                continue;
            }

            var next = i + 1;
            while (next < lines.Count && lines[next].Length == 0)
                next++;
            if (next < lines.Count && lines[next] != "•")
            {
                yield return "• " + lines[next];
                i = next;
            }
        }
    }
}
