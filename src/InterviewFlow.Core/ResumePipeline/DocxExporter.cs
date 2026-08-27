using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace InterviewFlow.Core.ResumePipeline;

/// <summary>
/// Styled .docx export (ports of _build_resume_doc* + _build_export_filename):
/// copy the template, strip every body child EXCEPT w:sectPr (page setup
/// survives), then one paragraph per tagged line with the style matching the
/// tag name (case-insensitive). Special cases: "[Section Heading]Summary" is
/// skipped; "[Skill]" bolds up to and including the first colon. Without a
/// template, a plain document is built (bold headings + "• " bullets — a
/// recorded deviation: the original relies on python-docx's default styles).
/// </summary>
public static partial class DocxExporter
{
    public const string TemplateFileName = "resume-template.docx";

    [GeneratedRegex(@"^\[([^\]]+)\](.*)", RegexOptions.Singleline)]
    private static partial Regex StyleTagRe();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MdLinkRe();

    [GeneratedRegex(@"\*{3}(.+?)\*{3}|\*{2}(.+?)\*{2}|\*(.+?)\*|_{2}(.+?)_{2}|_(.+?)_")]
    private static partial Regex MdInlineRe();

    [GeneratedRegex(@"[^\w\s-]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    /// <summary>Strip inline markdown + links → plain text (main.py:_md_plain).</summary>
    public static string MdPlain(string text)
    {
        text = MdInlineRe().Replace(text, m =>
            m.Groups.Values.Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "");
        text = MdLinkRe().Replace(text, "$1");
        return text.Trim();
    }

    /// <summary>Case-insensitive lookup of the template's resume-template.docx (docs/02 §2.6).</summary>
    public static string? FindTemplate(string dataDir)
    {
        if (!Directory.Exists(dataDir))
            return null;
        return Directory.EnumerateFiles(dataDir)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(TemplateFileName, StringComparison.OrdinalIgnoreCase));
    }

    public static byte[] Build(string taggedText, string resumeName, string resumeContact, string? templatePath)
    {
        if (templatePath is not null && File.Exists(templatePath))
            return BuildStyled(taggedText, File.ReadAllBytes(templatePath), resumeName, resumeContact);
        return BuildPlain(taggedText, resumeName, resumeContact);
    }

    /// <summary>FirstName_LastName_Resume_YYYYMMDD_Company.docx (main.py:2601).</summary>
    public static string BuildExportFilename(string resumeName, string companyName, string? date = null)
    {
        static string Clean(string s)
        {
            s = s.Split('|')[0].Trim();
            s = UnsafeChars().Replace(s, "").Trim();
            return WhitespaceRuns().Replace(s, "_");
        }

        var datePart = date ?? DateTime.Now.ToString("yyyyMMdd");
        string[] parts = [Clean(resumeName), "Resume", datePart, Clean(companyName)];
        return string.Join("_", parts.Where(p => p.Length > 0)) + ".docx";
    }

    // ── Styled (template) path ───────────────────────────────────────────────

    private static byte[] BuildStyled(string taggedText, byte[] template, string resumeName, string resumeContact)
    {
        using var stream = new MemoryStream();
        stream.Write(template);
        using (var doc = WordprocessingDocument.Open(stream, isEditable: true))
        {
            var body = doc.MainDocumentPart!.Document!.Body!;
            // Clear body content; sectPr (page margins / size) stays.
            foreach (var child in body.ChildElements.ToList())
            {
                if (child is not SectionProperties)
                    body.RemoveChild(child);
            }

            var styleMap = BuildStyleMap(doc);
            var sectPr = body.Elements<SectionProperties>().FirstOrDefault();

            void Append(Paragraph p)
            {
                if (sectPr is not null)
                    body.InsertBefore(p, sectPr);
                else
                    body.AppendChild(p);
            }

            Append(StyledParagraph(resumeName.Length > 0 ? resumeName : "[NAME HERE]", "Name", styleMap));
            Append(StyledParagraph(resumeContact.Length > 0 ? resumeContact : "[CONTACT INFO]", "Contact line", styleMap));

            foreach (var rawLine in taggedText.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var m = StyleTagRe().Match(line);
                if (!m.Success)
                {
                    // Untagged line — plain paragraph (legacy/plain resumes).
                    Append(new Paragraph(new Run(new Text(MdPlain(line)) { Space = SpaceProcessingModeValues.Preserve })));
                    continue;
                }

                var tag = m.Groups[1].Value.Trim();
                var content = m.Groups[2].Value.Trim();

                if (tag.Equals("section heading", StringComparison.OrdinalIgnoreCase)
                    && content.Equals("summary", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Summary has no section title in the Word output
                }

                if (tag.Equals("skill", StringComparison.OrdinalIgnoreCase))
                {
                    var p = StyledParagraph("", "Skill", styleMap);
                    var colon = content.IndexOf(':');
                    if (colon >= 0)
                    {
                        var cat = content[..colon].Trim() + ":";
                        var rest = content[(colon + 1)..].Trim();
                        p.AppendChild(BoldRun(cat));
                        if (rest.Length > 0)
                            p.AppendChild(PlainRun(" " + rest));
                    }
                    else
                    {
                        p.AppendChild(PlainRun(content));
                    }

                    Append(p);
                }
                else
                {
                    Append(StyledParagraph(content, tag, styleMap));
                }
            }

            doc.MainDocumentPart.Document!.Save();
        }

        return stream.ToArray();
    }

    private static Dictionary<string, string> BuildStyleMap(WordprocessingDocument doc)
    {
        // style NAME (lowercase) → styleId; tags reference names, w:pStyle wants ids.
        var map = new Dictionary<string, string>();
        var styles = doc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null)
            return map;
        foreach (var style in styles.Elements<Style>())
        {
            var name = style.StyleName?.Val?.Value;
            var id = style.StyleId?.Value;
            if (name is not null && id is not null)
                map[name.ToLowerInvariant()] = id;
        }

        return map;
    }

    private static Paragraph StyledParagraph(string text, string styleName, Dictionary<string, string> styleMap)
    {
        var p = new Paragraph();
        if (styleMap.TryGetValue(styleName.ToLowerInvariant(), out var styleId))
        {
            p.AppendChild(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        }

        if (text.Length > 0)
            p.AppendChild(PlainRun(text));
        return p;
    }

    private static Run PlainRun(string text) =>
        new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run BoldRun(string text) =>
        new(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    // ── Plain (no template) fallback ─────────────────────────────────────────

    private static byte[] BuildPlain(string text, string resumeName, string resumeContact)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;

            body.AppendChild(new Paragraph(BoldRun(resumeName.Length > 0 ? resumeName : "[NAME HERE]")));
            body.AppendChild(new Paragraph(PlainRun(resumeContact.Length > 0 ? resumeContact : "[CONTACT INFO]")));

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.StartsWith("# "))
                    continue; // name already written from settings
                if (line.StartsWith("### "))
                    body.AppendChild(HeadingParagraph(MdPlain(line[4..]), 13));
                else if (line.StartsWith("## "))
                    body.AppendChild(HeadingParagraph(MdPlain(line[3..]), 14));
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                    body.AppendChild(new Paragraph(PlainRun("• " + MdPlain(line[2..]))));
                else if (line.Length == 0)
                    body.AppendChild(new Paragraph());
                else
                    body.AppendChild(new Paragraph(PlainRun(MdPlain(line))));
            }

            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph HeadingParagraph(string text, int sizePt) =>
        new(new Run(
            new RunProperties(new Bold(), new FontSize { Val = (sizePt * 2).ToString() }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
}
