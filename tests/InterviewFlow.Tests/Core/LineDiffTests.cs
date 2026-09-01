using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.Tests.Core;

public sealed class LineDiffTests
{
    private static string Render(IEnumerable<DiffRow> rows) =>
        string.Join("\n", rows.Select(r => r.Prefix + r.Text));

    [Fact]
    public void Identical_text_is_all_same()
    {
        var rows = LineDiff.Compute("a\nb\nc", "a\nb\nc");
        Assert.All(rows, r => Assert.Equal(DiffKind.Same, r.Kind));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Replacement_shows_deletion_before_insertion()
    {
        var rows = LineDiff.Compute("keep\nold line\ntail", "keep\nnew line\ntail");
        Assert.Equal(" keep\n−old line\n+new line\n tail", Render(rows));
    }

    [Fact]
    public void Pure_insertion_and_deletion()
    {
        Assert.Equal(" a\n+b\n c", Render(LineDiff.Compute("a\nc", "a\nb\nc")));
        Assert.Equal(" a\n−b\n c", Render(LineDiff.Compute("a\nb\nc", "a\nc")));
    }

    [Fact]
    public void Empty_sides_are_handled()
    {
        // "" splits to a single empty line (same as the original's JS split),
        // so it shows as one deleted/added blank alongside the real content.
        var added = LineDiff.Compute("", "a\nb");
        Assert.Equal(2, added.Count(r => r.Kind == DiffKind.Added && r.Text.Length > 0));
        Assert.DoesNotContain(added, r => r.Kind == DiffKind.Deleted && r.Text.Length > 0);

        var deleted = LineDiff.Compute("a\nb", "");
        Assert.Equal(2, deleted.Count(r => r.Kind == DiffKind.Deleted && r.Text.Length > 0));
        Assert.DoesNotContain(deleted, r => r.Kind == DiffKind.Added && r.Text.Length > 0);
    }

    [Fact]
    public void Completely_different_text_keeps_every_line()
    {
        var rows = LineDiff.Compute("x\ny", "p\nq");
        Assert.Equal(2, rows.Count(r => r.Kind == DiffKind.Deleted));
        Assert.Equal(2, rows.Count(r => r.Kind == DiffKind.Added));
    }

    [Fact]
    public void Tagged_resume_edit_diffs_at_line_level()
    {
        const string before = "[Section Heading]Experience\n[Job title]Engineer | Acme\n[Job bullet]Did work";
        const string after = "[Section Heading]Experience\n[Job title]Staff Engineer | Acme\n[Job bullet]Did work";
        var rows = LineDiff.Compute(before, after);
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, r => r.Kind == DiffKind.Deleted);
        Assert.Single(rows, r => r.Kind == DiffKind.Added);
    }
}
