namespace InterviewFlow.Core.ResumePipeline;

/// <summary>How a diff row should render (docs/03-ui-spec.md §3.7).</summary>
public enum DiffKind
{
    /// <summary>Unchanged — slate-400, no prefix.</summary>
    Same,

    /// <summary>Removed — red, "−" prefix.</summary>
    Deleted,

    /// <summary>Inserted — green, "+" prefix.</summary>
    Added,
}

public sealed record DiffRow(DiffKind Kind, string Text)
{
    public string Prefix => Kind switch
    {
        DiffKind.Deleted => "−",
        DiffKind.Added => "+",
        _ => " ",
    };
}

/// <summary>
/// LCS line diff behind the Resume Tailor Comparison tab. Deletions precede
/// insertions at each divergence, matching the original's rendering order.
/// </summary>
public static class LineDiff
{
    public static List<DiffRow> Compute(string oldText, string newText)
    {
        var a = oldText.Replace("\r\n", "\n").Split('\n');
        var b = newText.Replace("\r\n", "\n").Split('\n');

        // Classic LCS table; resume-sized inputs make the O(n·m) cost irrelevant.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var rows = new List<DiffRow>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                rows.Add(new DiffRow(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                rows.Add(new DiffRow(DiffKind.Deleted, a[x]));
                x++;
            }
            else
            {
                rows.Add(new DiffRow(DiffKind.Added, b[y]));
                y++;
            }
        }

        for (; x < a.Length; x++)
            rows.Add(new DiffRow(DiffKind.Deleted, a[x]));
        for (; y < b.Length; y++)
            rows.Add(new DiffRow(DiffKind.Added, b[y]));

        return rows;
    }
}
