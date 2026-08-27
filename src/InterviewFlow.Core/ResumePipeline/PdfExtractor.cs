using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InterviewFlow.Core.ResumePipeline;

/// <summary>
/// PDF → markdown (port of _extract_text_from_pdf): headings by font-size
/// ratio against the modal body size (≥1.5 → #, ≥1.3 → ##, ≥1.15 → ###),
/// bullet characters → "- ", bold/italic spans → **/*. PdfPig replaces
/// PyMuPDF, so span segmentation differs slightly; the thresholds and emission
/// rules are identical (docs/06 §6.2 records the tolerance).
/// </summary>
public static partial class PdfExtractor
{
    private static readonly HashSet<char> BulletChars = [.. "•●◦▪▸▶‣⁃∙"];

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlank();

    private sealed record Span(string Text, double Size, bool Bold, bool Italic);

    public static string Extract(byte[] data)
    {
        using var doc = PdfDocument.Open(data);

        var bodySize = BodyFontSize(doc);
        var pageChunks = new List<string>();

        foreach (var page in doc.GetPages())
        {
            var pageLines = new List<string>();
            foreach (var line in ExtractLines(page))
            {
                var raw = string.Concat(line.Select(s => s.Text));
                if (raw.Trim().Length == 0)
                    continue;
                var maxSize = line.Max(s => s.Size);
                var ratio = bodySize > 0 ? maxSize / bodySize : 1.0;
                var stripped = raw.Trim();

                if (ratio >= 1.15)
                {
                    // Headings always get a blank line before and after.
                    if (pageLines.Count > 0 && pageLines[^1].Length > 0)
                        pageLines.Add("");
                    pageLines.Add(ratio >= 1.5 ? $"# {stripped}"
                        : ratio >= 1.3 ? $"## {stripped}"
                        : $"### {stripped}");
                    pageLines.Add("");
                }
                else if (stripped.Length > 0 && BulletChars.Contains(stripped[0]))
                {
                    pageLines.Add($"- {stripped[1..].TrimStart()}");
                }
                else
                {
                    pageLines.Add(RenderSpans(line));
                }
            }

            if (pageLines.Count > 0)
                pageChunks.Add(string.Join("\n", pageLines));
        }

        return MultiBlank().Replace(string.Join("\n\n", pageChunks), "\n\n").Trim();
    }

    /// <summary>Most common font size weighted by character count = body text.</summary>
    private static double BodyFontSize(PdfDocument doc)
    {
        var sizes = new Dictionary<double, int>();
        foreach (var page in doc.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                var t = word.Text.Trim();
                if (t.Length == 0)
                    continue;
                var size = Math.Round(word.Letters[0].PointSize, 1);
                sizes[size] = sizes.GetValueOrDefault(size) + t.Length;
            }
        }

        return sizes.Count > 0 ? sizes.MaxBy(kv => kv.Value).Key : 11.0;
    }

    /// <summary>
    /// Groups words into visual lines by baseline (top → bottom, left → right)
    /// and into format spans by (bold, italic).
    /// </summary>
    private static List<List<Span>> ExtractLines(Page page)
    {
        var words = page.GetWords()
            .Where(w => w.Text.Length > 0)
            .Select(w => new
            {
                Word = w,
                Baseline = w.Letters[0].StartBaseLine.Y,
                X = w.Letters[0].StartBaseLine.X,
                Size = w.Letters[0].PointSize,
                Bold = w.Letters[0].FontDetails.IsBold,
                Italic = w.Letters[0].FontDetails.IsItalic,
            })
            .ToList();

        // Cluster baselines with half-line tolerance.
        var lines = new List<List<Span>>();
        foreach (var group in words
                     .GroupBy(w => Math.Round(w.Baseline / Math.Max(1.0, w.Size * 0.6)))
                     .OrderByDescending(g => g.Average(w => w.Baseline)))
        {
            var ordered = group.OrderBy(w => w.X).ToList();
            var spans = new List<Span>();
            var text = new StringBuilder();
            var (bold, italic, size) = (ordered[0].Bold, ordered[0].Italic, ordered[0].Size);
            foreach (var w in ordered)
            {
                if (w.Bold != bold || w.Italic != italic)
                {
                    spans.Add(new Span(text.ToString(), size, bold, italic));
                    text.Clear();
                    (bold, italic, size) = (w.Bold, w.Italic, w.Size);
                }

                if (text.Length > 0)
                    text.Append(' ');
                text.Append(w.Word.Text);
                size = Math.Max(size, w.Size);
            }

            spans.Add(new Span(text.ToString(), size, bold, italic));
            lines.Add(spans);
        }

        return lines;
    }

    private static string RenderSpans(List<Span> spans)
    {
        var parts = new List<string>();
        foreach (var span in spans)
        {
            var text = span.Text;
            if (text.Trim().Length == 0 || (!span.Bold && !span.Italic))
            {
                parts.Add(text);
                continue;
            }

            var inner = text.Trim();
            var lead = text[..(text.Length - text.TrimStart().Length)];
            var trailLen = text.Length - text.TrimEnd().Length;
            var suf = trailLen > 0 ? text[^trailLen..] : "";
            var marker = span.Bold && span.Italic ? "***" : span.Bold ? "**" : "*";
            parts.Add($"{lead}{marker}{inner}{marker}{suf}");
        }

        return string.Join(" ", parts);
    }
}
