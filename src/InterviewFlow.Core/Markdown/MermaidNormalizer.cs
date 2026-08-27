using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Markdown;

/// <summary>
/// Normalizes LLM-produced mermaid source before rendering, replicating the original
/// app's pre-render fixes (docs/04-markdown-rendering.md §4.4). Each step exists
/// because a real model output broke the renderer without it.
/// </summary>
public static partial class MermaidNormalizer
{
    [GeneratedRegex("\\[\"?`([\\s\\S]*?)`\"?\\]")]
    private static partial Regex BacktickLabel();

    [GeneratedRegex(@"\bgraph TD\b", RegexOptions.Multiline)]
    private static partial Regex GraphTd();

    [GeneratedRegex(@"<br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BrVariant();

    [GeneratedRegex(@"^\s*subgraph\b", RegexOptions.Multiline)]
    private static partial Regex SubgraphOpen();

    [GeneratedRegex(@"^\s*end\s*$", RegexOptions.Multiline)]
    private static partial Regex SubgraphEnd();

    public static string Normalize(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        // 1. HTML-entity decode (the original round-trips through a <textarea>).
        var text = WebUtility.HtmlDecode(source);

        // 2. Backtick markdown-string labels ["`...`"] → ["..."] with bullets
        //    stripped and lines joined by a literal \n (step 4 turns it into <br/>).
        text = BacktickLabel().Replace(text, m =>
        {
            var lines = m.Groups[1].Value
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..]
                           : l.StartsWith("* ", StringComparison.Ordinal) ? l[2..]
                           : l);
            return "[\"" + string.Join("\\n", lines) + "\"]";
        });

        // 3. First `graph TD` → `graph LR`; `flowchart TD` is deliberately left alone.
        text = GraphTd().Replace(text, "graph LR", 1);

        // 4. Literal \n sequences and every <br> variant → <br/>.
        text = text.Replace("\\n", "<br/>");
        text = BrVariant().Replace(text, "<br/>");

        // 5. Balance unclosed subgraph blocks.
        var missing = SubgraphOpen().Count(text) - SubgraphEnd().Count(text);
        if (missing > 0)
        {
            var sb = new StringBuilder(text);
            for (var i = 0; i < missing; i++)
                sb.Append("\n    end");
            text = sb.ToString();
        }

        return text;
    }
}
