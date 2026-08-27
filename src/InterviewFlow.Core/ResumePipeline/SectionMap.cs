using System.Text.RegularExpressions;

namespace InterviewFlow.Core.ResumePipeline;

/// <summary>
/// Resume heading → section-type map: the builtin table from main.py:2189
/// merged with any entries from a user-editable section-headings.md, re-read on
/// EVERY parse (hot-reload, no restart — docs/02 §2.5).
/// </summary>
public static partial class SectionMap
{
    public const string FileName = "section-headings.md";

    [GeneratedRegex("^:?-")]
    private static partial Regex SeparatorRow();

    /// <summary>Builtin fallback map, verbatim from main.py (_TAG_SECTION_MAP).</summary>
    public static readonly IReadOnlyDictionary<string, string> Builtin = new Dictionary<string, string>
    {
        // Summary variants
        ["summary"] = "summary", ["professional summary"] = "summary",
        ["profile"] = "summary", ["objective"] = "summary",
        ["career objective"] = "summary", ["about"] = "summary",
        ["about me"] = "summary", ["overview"] = "summary",
        // Experience variants
        ["experience"] = "experience",
        ["work experience"] = "experience",
        ["professional experience"] = "experience",
        ["employment"] = "experience",
        ["employment history"] = "experience",
        ["career history"] = "experience",
        ["work history"] = "experience",
        // Early / other experience
        ["early career"] = "experience",
        ["early career experience"] = "experience",
        ["earlier experience"] = "experience",
        ["earlier career"] = "experience",
        ["other experience"] = "experience",
        ["additional experience"] = "additional",
        // Skills variants
        ["skills"] = "skills", ["technical skills"] = "skills",
        ["technical skills and tools"] = "skills",
        ["tools & platforms"] = "skills", ["skills & technology"] = "skills",
        ["core competencies"] = "skills", ["competencies"] = "skills",
        ["core expertise"] = "skills",
        ["technologies"] = "skills", ["expertise"] = "skills",
        ["technical expertise"] = "skills",
        // Education & credentials
        ["education"] = "additional",
        ["certifications"] = "additional", ["certificates"] = "additional",
        ["credentials"] = "additional", ["licenses"] = "additional",
        ["license & certifications"] = "additional",
        ["licenses & certifications"] = "additional",
        // Other sections
        ["awards"] = "additional", ["honors"] = "additional", ["honors & awards"] = "additional",
        ["achievements"] = "additional",
        ["publications"] = "additional", ["projects and publications"] = "additional",
        ["conference presentations & speaking"] = "additional", ["projects"] = "additional",
        ["volunteer"] = "additional", ["volunteering"] = "additional",
        ["volunteer experience"] = "additional",
        ["languages"] = "additional",
        ["interests"] = "additional", ["hobbies"] = "additional",
        ["activities"] = "additional",
        ["personal projects"] = "additional",
        ["additional information"] = "additional", ["additional"] = "additional",
    };

    /// <summary>Default file location: beside the executable (user-editable).</summary>
    public static string DefaultPath => Path.Combine(Paths.ExecutableDir(), FileName);

    /// <summary>Builtin merged with the file's entries (file wins). Hot-read.</summary>
    public static Dictionary<string, string> Load(string? path = null)
    {
        var merged = new Dictionary<string, string>(Builtin);
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path))
            {
                foreach (var (key, value) in ParseMarkdownTable(File.ReadAllText(path)))
                    merged[key] = value;
            }
        }
        catch
        {
            // Unreadable file → builtin only, matching the original.
        }

        return merged;
    }

    /// <summary>
    /// First table whose first header cell is "Section type"; columns 1–2 only,
    /// keyed lowercase on column 2 (main.py:_parse_section_map_md).
    /// </summary>
    public static Dictionary<string, string> ParseMarkdownTable(string text)
    {
        var result = new Dictionary<string, string>();
        var inTable = false;
        foreach (var line in text.Split('\n'))
        {
            var stripped = line.Trim();
            if (!stripped.StartsWith('|'))
            {
                if (inTable)
                    break;
                continue;
            }

            var cells = stripped.Trim('|').Split('|').Select(c => c.Trim()).ToList();
            if (cells.Count == 0)
                continue;
            if (!inTable)
            {
                if (cells[0].Equals("section type", StringComparison.OrdinalIgnoreCase))
                    inTable = true;
                continue;
            }

            if (SeparatorRow().IsMatch(cells[0]))
                continue;
            if (cells.Count >= 2 && cells[0].Length > 0 && cells[1].Length > 0)
                result[cells[1].ToLowerInvariant()] = cells[0];
        }

        return result;
    }
}
