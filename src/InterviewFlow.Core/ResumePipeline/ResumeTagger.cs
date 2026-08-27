using System.Text.RegularExpressions;

namespace InterviewFlow.Core.ResumePipeline;

/// <summary>
/// The tagging heuristic (exact port of _tag_resume_heuristic, main.py:2296):
/// skips the name/contact header, classifies section headings via the section
/// map + the ALL-CAPS rule, defers job-title candidates until the next line is
/// known, and produces the [Tag]content line DSL consumed by the preview and
/// the .docx exporter. Byte parity is tested against Python-generated fixtures.
/// </summary>
public static partial class ResumeTagger
{
    [GeneratedRegex(@"\b(19|20)\d{2}\b|\bpresent\b|\bcurrent\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateRe();

    [GeneratedRegex(@"^[•·◦○▪▸\-\*]\s+")]
    private static partial Regex BulletRe();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex MdHeadingRe();

    [GeneratedRegex(@"\*{1,3}|_{1,2}")]
    private static partial Regex MdInlineRe();

    [GeneratedRegex(@"@|\blinkedin\b|github\.com|\(\d{3}\)|\d{3}[-.\s]\d{3}[-.\s]\d{4}|https?://|www\.",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContactRe();

    [GeneratedRegex(@"^[A-Z][A-Z\s\d&/()\-]+$")]
    private static partial Regex AllCapsRe();

    /// <summary>## Heading → Heading; **bold**/*italic* markers removed.</summary>
    public static string StripMdLine(string line)
    {
        var s = MdHeadingRe().Replace(line.Trim(), "");
        s = MdInlineRe().Replace(s, "");
        return s.Trim();
    }

    public static bool IsSectionHeading(string line, IReadOnlyDictionary<string, string> sectionMap)
    {
        var s = StripMdLine(line).TrimEnd(':');
        if (s.Length == 0 || s.Length > 65)
            return false;
        if (AllCapsRe().IsMatch(s) && s.Length >= 3)
            return true;
        return sectionMap.ContainsKey(s.ToLowerInvariant());
    }

    public static string Tag(string text, IReadOnlyDictionary<string, string>? sectionMap = null)
    {
        var map = sectionMap ?? SectionMap.Load();
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();

        // Skip the name/contact header: always skip the first non-blank line
        // (name comes from settings), then contact-like lines; stop at the first
        // section heading or clearly-content line.
        var start = -1;
        var nonBlank = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var s = lines[i].Trim();
            if (s.Length == 0)
                continue;
            nonBlank++;
            if (nonBlank == 1)
                continue; // name line — always skip
            if (IsSectionHeading(s, map))
            {
                start = i;
                break;
            }

            if (nonBlank <= 4 && ContactRe().IsMatch(s))
                continue; // contact / address line — skip
            start = i;
            break;
        }

        if (start < 0)
            return "";

        var result = new List<string>();
        string? sectionType = null; // summary | experience | skills | additional | null
        var inJob = false;
        var jobNeedsSummary = false;
        string? pendingJobTitle = null;

        bool IsJobTitleCandidate(string s) =>
            s.Contains('|') && (DateRe().IsMatch(s) || sectionType == "experience");

        void FlushPending(bool nextIsHeading, bool nextIsCandidate)
        {
            if (pendingJobTitle is null)
                return;
            if (nextIsHeading || nextIsCandidate)
            {
                // No job content under it — additional info (e.g. an education row).
                result.Add($"[Additional info]{pendingJobTitle}");
                inJob = false;
                jobNeedsSummary = false;
            }
            else
            {
                result.Add($"[Job title]{pendingJobTitle}");
                inJob = true;
                jobNeedsSummary = true;
                sectionType ??= "experience";
            }

            pendingJobTitle = null;
        }

        foreach (var line in lines.Skip(start))
        {
            var raw = line.Trim();
            if (raw.Length == 0)
                continue;
            var s = StripMdLine(raw);
            if (s.Length == 0)
                continue;

            var norm = s.TrimEnd(':');
            var isHeading = IsSectionHeading(norm, map);
            var isCandidate = IsJobTitleCandidate(s);

            FlushPending(isHeading, isCandidate);

            if (isHeading)
            {
                sectionType = map.GetValueOrDefault(norm.ToLowerInvariant(), "additional");
                inJob = false;
                jobNeedsSummary = false;
                result.Add($"[Section Heading]{norm}");
                continue;
            }

            if (BulletRe().IsMatch(raw) || (raw.Length > 2 && (raw.StartsWith("- ") || raw.StartsWith("* "))))
            {
                var content = BulletRe().Replace(s, "").Trim();
                if (content.Length == 0)
                    content = s.Length > 2 ? s[2..].Trim() : "";
                jobNeedsSummary = false;
                var tag = sectionType == "additional" ? "[Additional info]"
                    : sectionType == "skills" ? "[Skill]" : "[Job bullet]";
                result.Add($"{tag}{content}");
                continue;
            }

            if (isCandidate)
            {
                pendingJobTitle = s;
                continue;
            }

            switch (sectionType)
            {
                case "summary":
                    result.Add($"[Summary]{s}");
                    break;
                case "skills":
                    result.Add($"[Skill]{s}");
                    break;
                case "experience":
                    if (inJob && jobNeedsSummary)
                    {
                        result.Add($"[Job summary]{s}");
                        jobNeedsSummary = false;
                    }
                    else
                    {
                        result.Add($"[Job bullet]{s}");
                    }

                    break;
                case "additional":
                    result.Add($"[Additional info]{s}");
                    break;
                default:
                    // Before any section heading — treat as summary.
                    result.Add($"[Summary]{s}");
                    break;
            }
        }

        FlushPending(false, sectionType == "additional");
        return string.Join("\n", result);
    }
}
