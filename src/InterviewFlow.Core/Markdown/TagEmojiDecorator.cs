using System.Text;
using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Markdown;

/// <summary>
/// Pre-parse markdown pass that prefixes confidence/priority tags with emoji,
/// matching the original app's <c>addTagEmojis</c> (docs/04-markdown-rendering.md §4.1 step 1).
/// Mermaid fences are left untouched so tag-like text inside diagrams is not mangled.
/// </summary>
public static partial class TagEmojiDecorator
{
    private static readonly (string From, string To)[] Replacements =
    [
        ("[VERIFIED]", "✅ [VERIFIED]"),
        ("[REPORTED]", "✅ [REPORTED]"),
        ("[LIKELY]", "🟡 [LIKELY]"),
        ("[SPECULATIVE]", "❓ [SPECULATIVE]"),
        ("[HIGH]", "🟢 [HIGH]"),
        ("[MEDIUM]", "🟡 [MEDIUM]"),
        ("[LOW]", "🔴 [LOW]"),
    ];

    [GeneratedRegex(@"(```mermaid[\s\S]*?```)")]
    private static partial Regex MermaidFence();

    public static string Decorate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Regex.Split with a capturing group keeps the fence segments in the result,
        // at odd indices — the same trick the original JS uses.
        var segments = MermaidFence().Split(text);
        var sb = new StringBuilder(text.Length + 64);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (i % 2 == 0)
            {
                foreach (var (from, to) in Replacements)
                    segment = segment.Replace(from, to);
            }

            sb.Append(segment);
        }

        return sb.ToString();
    }
}
