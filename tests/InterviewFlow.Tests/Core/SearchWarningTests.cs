using InterviewFlow.Core.Markdown;

namespace InterviewFlow.Tests.Core;

public sealed class SearchWarningTests
{
    private const string Report =
        "<div class=\"search-warning\">⚠️ <strong>Web search unavailable</strong> Results may be stale.</div>\n\n# Report\nBody text.";

    [Fact]
    public void Extracts_leading_warning_and_remaining_markdown()
    {
        var (warning, markdown) = SearchWarning.Extract(Report);

        Assert.NotNull(warning);
        Assert.Equal("Web search unavailable", warning.Title);
        Assert.Equal("Results may be stale.", warning.Body);
        Assert.StartsWith("# Report", markdown);
    }

    [Fact]
    public void No_warning_returns_input_unchanged()
    {
        var input = "# Plain report";
        var (warning, markdown) = SearchWarning.Extract(input);
        Assert.Null(warning);
        Assert.Equal(input, markdown);
    }

    [Fact]
    public void Warning_not_at_start_is_ignored()
    {
        var input = "# Report\n<div class=\"search-warning\">⚠️ <strong>T</strong> b</div>";
        var (warning, markdown) = SearchWarning.Extract(input);
        Assert.Null(warning);
        Assert.Equal(input, markdown);
    }
}
