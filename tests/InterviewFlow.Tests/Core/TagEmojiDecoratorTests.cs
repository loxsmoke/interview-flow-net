using InterviewFlow.Core.Markdown;

namespace InterviewFlow.Tests.Core;

public sealed class TagEmojiDecoratorTests
{
    [Theory]
    [InlineData("[VERIFIED]", "✅ [VERIFIED]")]
    [InlineData("[REPORTED]", "✅ [REPORTED]")]
    [InlineData("[LIKELY]", "🟡 [LIKELY]")]
    [InlineData("[SPECULATIVE]", "❓ [SPECULATIVE]")]
    [InlineData("[HIGH]", "🟢 [HIGH]")]
    [InlineData("[MEDIUM]", "🟡 [MEDIUM]")]
    [InlineData("[LOW]", "🔴 [LOW]")]
    public void Decorates_known_tags(string tag, string expected)
    {
        Assert.Equal($"x {expected} y", TagEmojiDecorator.Decorate($"x {tag} y"));
    }

    [Fact]
    public void Leaves_mermaid_fences_untouched()
    {
        var input = "before [HIGH]\n```mermaid\ngraph TD\n  A[\"[HIGH] risk\"]\n```\nafter [LOW]";
        var result = TagEmojiDecorator.Decorate(input);

        Assert.Contains("before 🟢 [HIGH]", result);
        Assert.Contains("after 🔴 [LOW]", result);
        // Inside the fence the tag must stay bare.
        Assert.Contains("A[\"[HIGH] risk\"]", result);
        Assert.DoesNotContain("A[\"🟢", result);
    }

    [Fact]
    public void Unknown_tags_pass_through()
    {
        Assert.Equal("[UNKNOWN] text", TagEmojiDecorator.Decorate("[UNKNOWN] text"));
    }
}
