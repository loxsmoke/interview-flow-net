using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Prompts;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Tests.Core;

public sealed class PricingTests
{
    [Fact]
    public void Known_models_use_their_table_rows()
    {
        Assert.Equal(2.0 + 10.0, Pricing.AnthropicCost("claude-sonnet-5", 1_000_000, 1_000_000));
        Assert.Equal(5.0 + 25.0, Pricing.AnthropicCost("claude-opus-5", 1_000_000, 1_000_000));
        Assert.Equal(3.0 + 15.0, Pricing.AnthropicCost("claude-sonnet-4-6", 1_000_000, 1_000_000));
        Assert.Equal(1.0 + 5.0, Pricing.AnthropicCost("claude-haiku-4-5", 1_000_000, 1_000_000));
        Assert.Equal(4.0 + 20.0, Pricing.OpenAiCost("gpt-5.6-sol", 1_000_000, 1_000_000));
        Assert.Equal(2.0 + 12.0, Pricing.OpenAiCost("gpt-5.6-terra", 1_000_000, 1_000_000));
        Assert.Equal(2.50 + 10.0, Pricing.OpenAiCost("gpt-4o", 1_000_000, 1_000_000));
        Assert.Equal(5.0 + 30.0, Pricing.OpenAiCost("gpt-5.5", 1_000_000, 1_000_000));
        Assert.Equal(0.75 + 3.75, Pricing.GeminiCost("gemini-3.6-flash", 1_000_000, 1_000_000));
    }

    [Fact]
    public void Unknown_models_fall_back_to_provider_defaults()
    {
        Assert.Equal(3.0 + 15.0, Pricing.AnthropicCost("claude-next", 1_000_000, 1_000_000));
        Assert.Equal(2.50 + 10.0, Pricing.OpenAiCost("gpt-99", 1_000_000, 1_000_000));
        Assert.Equal(1.25 + 5.0, Pricing.GeminiCost("gemini-99", 1_000_000, 1_000_000));
    }
}

public sealed class TemperatureTests
{
    [Theory]
    [InlineData("resume-review", 0.3)]
    [InlineData("decode-jd", 0.3)]
    [InlineData("mine-stories", 0.3)]
    [InlineData("company-research", 0.5)]
    [InlineData("build-pitches", 0.9)]
    [InlineData("mock-interview", 0.9)]
    [InlineData("unknown-section", 0.7)]
    public void Section_map_matches_the_original(string section, double expected) =>
        Assert.Equal(expected, Temperatures.ForSection(section));

    [Fact]
    public void Anthropic_and_ollama_clamp_at_one()
    {
        Assert.Equal(0.9, TemperatureSetting.FromSection.Resolve("build-pitches", "openai"));
        Assert.Equal(0.9, TemperatureSetting.FromSection.Resolve("build-pitches", "anthropic"));
        Assert.Equal(1.0, TemperatureSetting.Explicit(1.5).Resolve("x", "anthropic"));
        Assert.Equal(1.0, TemperatureSetting.Explicit(1.5).Resolve("x", "ollama"));
        Assert.Equal(1.5, TemperatureSetting.Explicit(1.5).Resolve("x", "openai"));
    }

    [Fact]
    public void Api_default_resolves_to_null() =>
        Assert.Null(TemperatureSetting.ApiDefault.Resolve("company-research", "anthropic"));
}

public sealed class RetryParsingTests
{
    [Theory]
    [InlineData("Rate limit reached … Please try again in 1.5s. Visit …", 1.5)]
    [InlineData("try again in 800ms", 0.8)]
    [InlineData("Please try again in 1m30s", 90)]
    [InlineData("try again in 2m", 120)]
    public void OpenAi_message_formats_parse(string message, double expected) =>
        Assert.Equal(expected, RetryParsing.ParseOpenAiRetryAfter(message)!.Value, 3);

    [Fact]
    public void No_hint_returns_null() =>
        Assert.Null(RetryParsing.ParseOpenAiRetryAfter("quota exceeded, contact support"));

    [Fact]
    public void Retry_after_header_falls_back_to_60() =>
        Assert.Equal(60.0, RetryParsing.ParseRetryAfterHeader("nonsense"));
}

public sealed class ProviderSelectionTests
{
    private static AppConfig Config(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "if-prov-" + Guid.NewGuid().ToString("N")[..8] + ".env");
        File.WriteAllText(path, content);
        return new AppConfig(EnvFile.Load(path));
    }

    [Fact]
    public void Explicit_provider_wins() =>
        Assert.Equal("gemini", ProviderRouter.ResolveProvider(Config("ACTIVE_PROVIDER=gemini\nOPENAI_API_KEY=sk-x\n")));

    [Fact]
    public void Openai_key_alone_selects_openai() =>
        Assert.Equal("openai", ProviderRouter.ResolveProvider(Config("OPENAI_API_KEY=sk-x\n")));

    [Fact]
    public void Default_is_anthropic() =>
        Assert.Equal("anthropic", ProviderRouter.ResolveProvider(Config("")));
}

public sealed class PromptLoaderTests
{
    [Theory]
    [InlineData("research")]
    [InlineData("interview_intel")]
    [InlineData("interview_intel_technical")]
    [InlineData("jd_decode")]
    [InlineData("mock_interview")]
    [InlineData("pitch")]
    [InlineData("resume_chat")]
    [InlineData("resume_review")]
    [InlineData("salary_coach")]
    [InlineData("story_mining")]
    [InlineData("concerns")]
    public void Every_template_has_a_prompt_section(string name) =>
        Assert.False(string.IsNullOrWhiteSpace(PromptLoader.LoadPrompt(name)));

    [Fact]
    public void Research_system_prompt_loads() =>
        Assert.False(string.IsNullOrWhiteSpace(PromptLoader.LoadSystemPrompt("research")));

    [Fact]
    public void Fence_extraction_is_exact()
    {
        const string doc = "# T\n\n## Prompt\n\n````\nline1\n{tag}\n````\n\n## System Prompt\n\n````\nsys\n````\n";
        Assert.Equal("line1\n{tag}", PromptLoader.ExtractFence(doc, "Prompt"));
        Assert.Equal("sys", PromptLoader.ExtractFence(doc, "System Prompt"));
    }
}

public sealed class ResearchAgentTests
{
    [Fact]
    public void Prompt_substitutes_posting_and_wraps_resume()
    {
        var prompt = ResearchAgent.BuildPrompt("JOB TEXT HERE", "RESUME TEXT");
        Assert.Contains("JOB TEXT HERE", prompt);
        Assert.Contains("<user_provided_resume>\nRESUME TEXT\n</user_provided_resume>", prompt);
        Assert.DoesNotContain("{job_posting}", prompt);
        Assert.DoesNotContain("{resume_section}", prompt);

        var noResume = ResearchAgent.BuildPrompt("JOB");
        Assert.DoesNotContain("user_provided_resume", noResume);
    }

    [Fact]
    public void Sources_section_dedupes_and_formats_like_the_original()
    {
        var section = ResearchAgent.BuildSourcesSection(
        [
            new ToolUseEvent("WebSearch", Query: "acme reviews"),
            new ToolUseEvent("WebSearch", Query: "acme reviews"),
            new ToolUseEvent("WebFetch", Url: "https://a.example", Title: "Acme"),
            new ToolUseEvent("WebFetch", Url: "https://a.example", Title: "dup"),
            new ToolUseEvent("WebFetch", Url: "https://b.example", Title: ""),
        ]);

        var expected = string.Join("\n",
        [
            "---",
            "## Sources",
            "- [Acme](https://a.example)",
            "- [https://b.example](https://b.example)",
            "",
            "**Search queries used:**",
            "- acme reviews",
        ]);
        Assert.Equal(expected, section);
    }

    [Fact]
    public void No_tool_uses_means_no_sources_section() =>
        Assert.Equal("", ResearchAgent.BuildSourcesSection([]));

    [Fact]
    public void Search_warning_applies_only_to_known_statuses()
    {
        Assert.StartsWith("<div class=\"search-warning\">⚠️ <strong>No web searches were performed.</strong>",
            SearchWarnings.Apply("# R", "not_searched"));
        Assert.Contains("connection error", SearchWarnings.Apply("# R", "connection_error"));
        Assert.Equal("# R", SearchWarnings.Apply("# R", "ok"));
    }

    [Fact]
    public void Injected_warning_round_trips_through_the_renderer_extractor()
    {
        var text = SearchWarnings.Apply("# Report body", "no_results");
        var (warning, rest) = InterviewFlow.Core.Markdown.SearchWarning.Extract(text);
        Assert.NotNull(warning);
        Assert.Equal("No web search results found.", warning!.Title);
        Assert.StartsWith("# Report body", rest);
    }
}

public sealed class SearchStatusTests
{
    [Theory]
    [InlineData(0, 0, 0, "not_searched")]
    [InlineData(3, 0, 0, "ok")]
    [InlineData(3, 1, 1, "ok")]
    [InlineData(2, 2, 0, "connection_error")]
    [InlineData(2, 1, 1, "connection_error")]
    [InlineData(2, 0, 2, "no_results")]
    public void Classifier_matches_the_original(int done, int failed, int empty, string expected) =>
        Assert.Equal(expected, OllamaProvider.ClassifySearchStatus(done, failed, empty));
}
