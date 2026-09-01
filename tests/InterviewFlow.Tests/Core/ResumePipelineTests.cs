using DocumentFormat.OpenXml.Packaging;
using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.Tests.Core;

public sealed class DocxExtractorParityTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static byte[] TestDocx => File.ReadAllBytes(Fixture("Parse-Test-Resume.docx"));

    private static string ReadFixture(string name) =>
        File.ReadAllText(Fixture(name)).Replace("\r\n", "\n").TrimEnd('\n');

    [Fact]
    public void Markdown_extraction_matches_the_python_original_byte_for_byte()
    {
        var expected = ReadFixture("parsed-resume-generated.txt");
        var actual = DocxExtractor.ExtractMarkdown(TestDocx);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Raw_diagnostic_dump_matches_the_python_original_byte_for_byte()
    {
        var expected = ReadFixture("parsed-resume-raw.txt");
        var actual = DocxExtractor.ExtractRaw(TestDocx);
        Assert.Equal(expected, actual);
    }
}

public sealed class ResumeTaggerParityTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string ReadFixture(string name) =>
        File.ReadAllText(Fixture(name)).Replace("\r\n", "\n").TrimEnd('\n');

    [Fact]
    public void Tagged_output_matches_the_python_original_byte_for_byte()
    {
        // Same inputs the original used: its own extracted markdown + the
        // shipped section-headings.md.
        var markdown = ReadFixture("parsed-resume-generated.txt");
        var map = SectionMap.Load(Fixture("section-headings.md"));
        var expected = ReadFixture("parsed-resume-tagged.txt");
        Assert.Equal(expected, ResumeTagger.Tag(markdown, map));
    }

    [Fact]
    public void All_caps_lines_are_headings_up_to_65_chars()
    {
        var map = SectionMap.Load(Fixture("section-headings.md"));
        Assert.True(ResumeTagger.IsSectionHeading("PROFESSIONAL EXPERIENCE", map));
        Assert.True(ResumeTagger.IsSectionHeading("SKILLS & TOOLS (2020-2024)", map));
        Assert.False(ResumeTagger.IsSectionHeading("AB", map)); // < 3 chars
        Assert.False(ResumeTagger.IsSectionHeading(new string('A', 70), map));
        Assert.True(ResumeTagger.IsSectionHeading("Technical Skills", map)); // via map
        Assert.False(ResumeTagger.IsSectionHeading("Some random sentence here", map));
    }
}

public sealed class SectionMapTests
{
    [Fact]
    public void Parses_the_first_matching_table_only()
    {
        const string md = """
            Intro text.

            | Other | Table |
            |---|---|
            | a | b |

            | Section type | Input text | Notes |
            |---|---|---|
            | skills | my custom skills | ignored |
            | summary | executive summary |

            | Section type | Input text |
            |---|---|
            | additional | never reached |
            """;
        var map = SectionMap.ParseMarkdownTable(md);
        Assert.Equal(2, map.Count);
        Assert.Equal("skills", map["my custom skills"]);
        Assert.Equal("summary", map["executive summary"]);
    }

    [Fact]
    public void Load_merges_file_over_builtin_and_hot_reads()
    {
        var path = Path.Combine(Path.GetTempPath(), "if-map-" + Guid.NewGuid().ToString("N")[..8] + ".md");
        File.WriteAllText(path, "| Section type | Input text |\n|---|---|\n| skills | weird heading |\n");
        var map = SectionMap.Load(path);
        Assert.Equal("skills", map["weird heading"]);
        Assert.Equal("experience", map["work experience"]); // builtin retained

        // Hot-reload: editing the file changes the next Load.
        File.WriteAllText(path, "| Section type | Input text |\n|---|---|\n| summary | weird heading |\n");
        Assert.Equal("summary", SectionMap.Load(path)["weird heading"]);
        File.Delete(path);
    }

    [Fact]
    public void Missing_file_returns_builtin() =>
        Assert.Equal("summary", SectionMap.Load(Path.Combine(Path.GetTempPath(), "nope.md"))["profile"]);
}

public sealed class ResumeIntakeTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Docx_intake_produces_text_raw_and_tagged()
    {
        var result = ResumeIntake.Extract("Parse-Test-Resume.docx",
            File.ReadAllBytes(Fixture("Parse-Test-Resume.docx")), Fixture("section-headings.md"));
        Assert.NotNull(result.Raw);
        Assert.Contains("[Section Heading]", result.Tagged);
        Assert.Equal(result.Text.Length, result.Chars);
    }

    [Fact]
    public void Text_intake_passes_through_without_raw()
    {
        var result = ResumeIntake.Extract("resume.md", "# Jane\nSummary line"u8.ToArray(),
            Fixture("section-headings.md"));
        Assert.Null(result.Raw);
        Assert.Equal("# Jane\nSummary line", result.Text);
    }

    [Theory]
    [InlineData("resume.exe", "Unsupported file type: .exe")]
    [InlineData("", "No file provided")]
    public void Rejections_use_the_original_messages(string filename, string expected)
    {
        var ex = Assert.Throws<ResumeIntakeException>(() => ResumeIntake.Extract(filename, [1, 2, 3]));
        Assert.StartsWith(expected, ex.Message);
    }

    [Fact]
    public void Magic_bytes_are_checked()
    {
        var pdfEx = Assert.Throws<ResumeIntakeException>(() =>
            ResumeIntake.Extract("x.pdf", "not a pdf"u8.ToArray()));
        Assert.Equal("File does not appear to be a valid PDF", pdfEx.Message);

        var docxEx = Assert.Throws<ResumeIntakeException>(() =>
            ResumeIntake.Extract("x.docx", "not a zip"u8.ToArray()));
        Assert.Equal("File does not appear to be a valid DOCX", docxEx.Message);
    }

    [Fact]
    public void Oversize_files_are_rejected()
    {
        var ex = Assert.Throws<ResumeIntakeException>(() =>
            ResumeIntake.Extract("x.txt", new byte[ResumeIntake.MaxUploadSize + 1]));
        Assert.Equal("File too large (max 10 MB)", ex.Message);
    }
}

public sealed class PdfExtractorTests
{
    [Fact]
    public void Synthetic_pdf_gets_headings_bullets_and_bold()
    {
        // Build a PDF with PdfPig's writer: big title, body text, bullet line.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.HelveticaBold);
        page.AddText("John Smith", 20, new UglyToad.PdfPig.Core.PdfPoint(40, 780), font);
        page.AddText("Senior engineer with a decade of experience shipping systems.", 11,
            new UglyToad.PdfPig.Core.PdfPoint(40, 750), font);
        page.AddText("Impactful Words", 11, new UglyToad.PdfPig.Core.PdfPoint(40, 730), bold);
        page.AddText("• Led the migration project", 11, new UglyToad.PdfPig.Core.PdfPoint(40, 710), font);

        var markdown = PdfExtractor.Extract(builder.Build());

        Assert.Contains("# John Smith", markdown);        // 20/11 ≥ 1.5 → h1
        Assert.Contains("Senior engineer", markdown);
        Assert.Contains("**Impactful Words**", markdown); // bold span
        Assert.Contains("- Led the migration project", markdown);
    }
}

public sealed class DocxExporterTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private const string Tagged =
        "[Section Heading]Summary\n" +
        "[Summary]Engineer who ships.\n" +
        "[Section Heading]Experience\n" +
        "[Job title]Staff Engineer | Acme | 2020–Present\n" +
        "[Job bullet]Did the thing\n" +
        "[Skill]Languages: C#, Python\n" +
        "untagged trailing line";

    [Fact]
    public void Styled_export_maps_tags_skips_summary_heading_and_bolds_skill_category()
    {
        var bytes = DocxExporter.Build(Tagged, "John Smith", "john@x.com", Fixture("Resume-Template.docx"));

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().ToList();
        var texts = paragraphs.Select(p => p.InnerText).ToList();

        Assert.Equal("John Smith", texts[0]);
        Assert.Equal("john@x.com", texts[1]);
        Assert.DoesNotContain("Summary", texts.Where((_, i) => i >= 2 && texts[i].Trim() == "Summary"));
        Assert.Contains("Engineer who ships.", texts);
        Assert.Contains("Experience", texts);
        Assert.Contains("Staff Engineer | Acme | 2020–Present", texts);
        Assert.Contains("untagged trailing line", texts);

        // sectPr from the template survives the body strip.
        Assert.NotNull(body.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().FirstOrDefault());

        // Skill line: bold "Languages:" run + plain rest.
        var skill = paragraphs.First(p => p.InnerText.StartsWith("Languages:"));
        var runs = skill.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>().ToList();
        Assert.Equal("Languages:", runs[0].InnerText);
        Assert.NotNull(runs[0].RunProperties?.Bold);
        Assert.Equal(" C#, Python", runs[1].InnerText);
        Assert.Null(runs[1].RunProperties?.Bold);

        // Styles applied by name (template has "Job title" etc.).
        var jobTitle = paragraphs.First(p => p.InnerText.StartsWith("Staff Engineer"));
        Assert.NotNull(jobTitle.ParagraphProperties?.ParagraphStyleId);
    }

    [Fact]
    public void Plain_fallback_builds_without_a_template()
    {
        var bytes = DocxExporter.Build("# Jane\n## Skills\n- C#\nplain", "", "", templatePath: null);
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var texts = doc.MainDocumentPart!.Document!.Body!
            .Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText).ToList();
        Assert.Equal("[NAME HERE]", texts[0]);
        Assert.DoesNotContain("Jane", string.Join("|", texts)); // "# " name line skipped
        Assert.Contains("Skills", texts);
        Assert.Contains("• C#", texts);
    }

    [Theory]
    [InlineData("John Smith | v2", "Acme, Inc. | backend", "20260826", "John_Smith_Resume_20260826_Acme_Inc.docx")]
    [InlineData("", "Acme", "20260826", "Resume_20260826_Acme.docx")]
    [InlineData("Jane", "", "20260826", "Jane_Resume_20260826.docx")]
    public void Export_filename_matches_the_original(string name, string company, string date, string expected) =>
        Assert.Equal(expected, DocxExporter.BuildExportFilename(name, company, date));

    [Fact]
    public void Md_plain_strips_inline_markers_and_links()
    {
        Assert.Equal("bold italic both", DocxExporter.MdPlain("**bold** *italic* ***both***"));
        Assert.Equal("site", DocxExporter.MdPlain("[site](https://x.example)"));
    }
}
