using System.Runtime.CompilerServices;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.Prompts;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Company Research agent (port of agents/research.py + the route's persist
/// step): builds the prompt from the research template, streams with web search
/// enabled, appends a Sources section from tool uses, prepends the
/// search-warning when applicable, and writes results into the state.
/// </summary>
public static class ResearchAgent
{
    public const string TraceName = "company-research";

    public static string BuildPrompt(string jobPosting, string resume = "")
    {
        var resumeSection = resume.Length > 0
            ? $"## Candidate Resume (for fit assessment)\n<user_provided_resume>\n{resume}\n</user_provided_resume>"
            : "";
        return PromptLoader.LoadPrompt("research")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume_section}", resumeSection);
    }

    public static QueryOptions BuildOptions() => new(
        SystemPrompt: PromptLoader.LoadSystemPrompt("research"),
        UseWebSearch: true);

    /// <summary>
    /// Streams the research run. The final CompleteEvent's Text already carries
    /// the Sources section and any search warning.
    /// </summary>
    public static async IAsyncEnumerable<AgentEvent> StreamAsync(
        AppConfig config, string jobPosting, string resume = "",
        [EnumeratorCancellation] CancellationToken ct = default,
        HttpClient? http = null)
    {
        var prompt = BuildPrompt(jobPosting, resume);
        await foreach (var evt in ProviderRouter.StreamQueryAsync(config, prompt, BuildOptions(), TraceName, ct, http))
        {
            if (evt is CompleteEvent complete)
            {
                var text = complete.Text;
                var sources = BuildSourcesSection(complete.ToolUses);
                if (sources.Length > 0)
                    text = text.TrimEnd() + "\n\n" + sources;
                text = SearchWarnings.Apply(text, complete.SearchStatus);
                yield return complete with { Text = text };
            }
            else
            {
                yield return evt;
            }
        }
    }

    /// <summary>Persist step: mirrors the research route's save_result.</summary>
    public static void SaveResult(InterviewState state, CompleteEvent complete)
    {
        state.Research.RawReport = complete.Text;
        state.Research.QueryCostUsd = complete.CostUsd;
        state.Research.QueryModelName = complete.ModelName;
        state.Research.QueryDurationMs = complete.DurationMs;
        state.Research.QueryRanAt = ModelDefaults.NowIso();
        state.Research.ResearchedAt = state.Research.QueryRanAt;
        if (!state.CompletedSteps.Contains("research"))
            state.CompletedSteps.Add("research");
    }

    /// <summary>Markdown Sources section from tool uses (research.py:39-77).</summary>
    public static string BuildSourcesSection(IReadOnlyList<ToolUseEvent> toolUses)
    {
        var seenUrls = new List<(string Url, string Title)>();
        var seenUrlSet = new HashSet<string>();
        var seenQueries = new List<string>();
        var seenQuerySet = new HashSet<string>();

        foreach (var tu in toolUses)
        {
            if (tu.Tool == "WebFetch")
            {
                var url = tu.Url.Trim();
                if (url.Length > 0 && seenUrlSet.Add(url))
                    seenUrls.Add((url, tu.Title.Trim()));
            }
            else if (tu.Tool == "WebSearch")
            {
                var q = tu.Query.Trim();
                if (q.Length > 0 && seenQuerySet.Add(q))
                    seenQueries.Add(q);
            }
        }

        if (seenUrls.Count == 0 && seenQueries.Count == 0)
            return "";

        var lines = new List<string> { "---", "## Sources" };
        foreach (var (url, title) in seenUrls)
            lines.Add($"- [{(title.Length > 0 ? title : url)}]({url})");
        if (seenQueries.Count > 0)
        {
            if (seenUrls.Count > 0)
                lines.Add("");
            lines.Add("**Search queries used:**");
            foreach (var q in seenQueries)
                lines.Add($"- {q}");
        }

        return string.Join("\n", lines);
    }
}
