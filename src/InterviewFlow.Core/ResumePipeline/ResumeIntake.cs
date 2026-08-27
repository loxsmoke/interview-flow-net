using System.Text;

namespace InterviewFlow.Core.ResumePipeline;

/// <summary>Validation failures surface the original route's messages.</summary>
public sealed class ResumeIntakeException(string message) : Exception(message);

/// <summary>Upload response shape of /api/upload-resume.</summary>
public sealed record ResumeExtractResult(string Text, string? Raw, string Tagged, string Filename, int Chars);

/// <summary>
/// File intake (port of upload_resume, main.py:1813): extension allow-list,
/// 10 MB cap, magic-byte checks, then extraction + tagging. DOCX gets both the
/// clean markdown and the diagnostic raw dump; other formats markdown only.
/// </summary>
public static class ResumeIntake
{
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string> { ".pdf", ".docx", ".doc", ".txt", ".md", ".rtf" };

    public const int MaxUploadSize = 10 * 1024 * 1024;

    public static ResumeExtractResult Extract(string filename, byte[] data, string? sectionMapPath = null)
    {
        if (filename.Length == 0)
            throw new ResumeIntakeException("No file provided");

        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new ResumeIntakeException(
                $"Unsupported file type: {ext}. Accepted: {string.Join(", ", AllowedExtensions.Order())}");

        if (data.Length > MaxUploadSize)
            throw new ResumeIntakeException("File too large (max 10 MB)");

        if (ext == ".pdf" && !(data.Length >= 5 && data.AsSpan(0, 4).SequenceEqual("%PDF"u8)))
            throw new ResumeIntakeException("File does not appear to be a valid PDF");
        if (ext is ".docx" or ".doc" && !(data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8)))
            throw new ResumeIntakeException("File does not appear to be a valid DOCX");

        string text;
        string? raw = null;
        try
        {
            if (ext is ".docx" or ".doc")
            {
                text = DocxExtractor.ExtractMarkdown(data);
                raw = DocxExtractor.ExtractRaw(data);
            }
            else if (ext == ".pdf")
            {
                text = PdfExtractor.Extract(data);
            }
            else
            {
                text = Encoding.UTF8.GetString(data).Trim();
            }
        }
        catch (ResumeIntakeException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ResumeIntakeException("Could not extract text from file. Try a different format.");
        }

        if (text.Trim().Length == 0)
            throw new ResumeIntakeException(
                "Could not extract any text from the file. Try pasting your resume instead.");

        var tagged = ResumeTagger.Tag(text, SectionMap.Load(sectionMapPath));
        return new ResumeExtractResult(text, raw, tagged, filename, text.Length);
    }
}
