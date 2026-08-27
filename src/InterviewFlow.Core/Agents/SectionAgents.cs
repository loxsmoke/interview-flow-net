using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.Prompts;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// The remaining single-shot agents (port of agents/story_miner.py): prompt
/// builders + streams for interview intel, JD decode, resume review, pitches,
/// concerns, salary, and story mining. Persistence lives in SectionRunner.
/// </summary>
public static partial class SectionAgents
{
    [GeneratedRegex(@"^\[[^\]]+\]\s*", RegexOptions.Multiline)]
    private static partial Regex TagPrefix();

    /// <summary>Strip "| comment" suffixes from company/position (main.py:332).</summary>
    public static string StripComment(string value) => value.Split('|')[0].Trim();

    /// <summary>
    /// Resume text safe for AI: the tagged version with tag prefixes stripped
    /// (name/contact excluded), falling back to the plain resume (main.py:2413).
    /// </summary>
    public static string ResumeForAi(InterviewState s) =>
        s.ResumeTagged.Length > 0 ? TagPrefix().Replace(s.ResumeTagged, "").Trim() : s.ResumeText;

    /// <summary>Stories formatted for prompt inclusion (main.py:314).</summary>
    public static string StoriesAsText(IReadOnlyList<Story> stories)
    {
        if (stories.Count == 0)
            return "No stories yet.";
        return string.Join("\n\n", stories.Select(s =>
            $"### {s.Title}\n" +
            $"- Situation: {s.Situation}\n" +
            $"- Task: {s.Task}\n" +
            $"- Action: {s.Action}\n" +
            $"- Result: {s.Result}\n" +
            $"- Earned Secret: {s.EarnedSecret}\n" +
            $"- Tags: {string.Join(", ", s.Tags)}"));
    }

    private static readonly string[] TechnicalKeywords =
    [
        "engineer", "developer", "programmer", "software", "coding", "swe",
        "backend", "frontend", "fullstack", "full-stack", "full stack",
        "data scientist", "data engineer", "ml engineer", "machine learning",
        "devops", "sre",
    ];

    public static bool IsTechnicalRole(string position)
    {
        var lower = position.ToLowerInvariant();
        return TechnicalKeywords.Any(lower.Contains);
    }

    // ── Prompt builders (templates are Python str.format with named braces) ──

    public static string BuildInterviewIntelPrompt(string companyName, string jobPosting, string position = "")
    {
        var technicalSection = IsTechnicalRole(position)
            ? PromptLoader.LoadPrompt("interview_intel_technical").Replace("{company_name}", companyName)
            : "";
        return PromptLoader.LoadPrompt("interview_intel")
            .Replace("{company_name}", companyName)
            .Replace("{job_posting}", jobPosting)
            .Replace("{technical_section}", technicalSection);
    }

    public static string BuildDecodeJdPrompt(string jobPosting) =>
        PromptLoader.LoadPrompt("jd_decode").Replace("{job_posting}", jobPosting);

    public static string BuildResumeReviewPrompt(string jobPosting, string resume) =>
        PromptLoader.LoadPrompt("resume_review")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume);

    public static string BuildPitchPrompt(string jobPosting, string resume) =>
        PromptLoader.LoadPrompt("pitch")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume);

    public static string BuildConcernsPrompt(string jobPosting, string resume) =>
        PromptLoader.LoadPrompt("concerns")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume);

    public static string BuildSalaryPrompt(string jobPosting, string resume) =>
        PromptLoader.LoadPrompt("salary_coach")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume.Length > 0 ? resume : "Not provided");

    public static string BuildMiningPrompt(string resume, string jobPosting, string existingStories = "None") =>
        PromptLoader.LoadPrompt("story_mining")
            .Replace("{resume}", resume)
            .Replace("{job_posting}", jobPosting)
            .Replace("{existing_stories}", existingStories);

    // ── Streams ──────────────────────────────────────────────────────────────

    public static IAsyncEnumerable<AgentEvent> StreamInterviewIntel(
        AppConfig config, string companyName, string jobPosting, string position,
        CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildInterviewIntelPrompt(companyName, jobPosting, position),
            new QueryOptions(PromptLoader.LoadSystemPrompt("interview_intel"), UseWebSearch: true),
            "interview-intel", ct, http);

    public static IAsyncEnumerable<AgentEvent> StreamDecodeJd(
        AppConfig config, string jobPosting, CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildDecodeJdPrompt(jobPosting),
            new QueryOptions(PromptLoader.LoadSystemPrompt("jd_decode")),
            "decode-jd", ct, http);

    public static IAsyncEnumerable<AgentEvent> StreamResumeReview(
        AppConfig config, string jobPosting, string resume, CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildResumeReviewPrompt(jobPosting, resume),
            new QueryOptions(PromptLoader.LoadSystemPrompt("resume_review")),
            "resume-review", ct, http);

    public static IAsyncEnumerable<AgentEvent> StreamBuildPitches(
        AppConfig config, string jobPosting, string resume, CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildPitchPrompt(jobPosting, resume),
            new QueryOptions(PromptLoader.LoadSystemPrompt("pitch")),
            "build-pitches", ct, http);

    public static IAsyncEnumerable<AgentEvent> StreamAnticipateConcerns(
        AppConfig config, string jobPosting, string resume, CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildConcernsPrompt(jobPosting, resume),
            new QueryOptions(PromptLoader.LoadSystemPrompt("concerns")),
            "anticipate-concerns", ct, http);

    /// <summary>Salary coaching — web mode, Sources section appended like research.</summary>
    public static async IAsyncEnumerable<AgentEvent> StreamSalaryCoach(
        AppConfig config, string jobPosting, string resume,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        HttpClient? http = null)
    {
        var stream = ProviderRouter.StreamQueryAsync(config,
            BuildSalaryPrompt(jobPosting, resume),
            new QueryOptions(PromptLoader.LoadSystemPrompt("salary_coach"), UseWebSearch: true),
            "salary-coach", ct, http);
        await foreach (var evt in stream)
        {
            if (evt is CompleteEvent complete)
            {
                var sources = ResearchAgent.BuildSourcesSection(complete.ToolUses);
                var text = sources.Length > 0 ? complete.Text.TrimEnd() + "\n\n" + sources : complete.Text;
                yield return complete with { Text = text };
            }
            else
            {
                yield return evt;
            }
        }
    }

    public static IAsyncEnumerable<AgentEvent> StreamMineStories(
        AppConfig config, string resume, string jobPosting, CancellationToken ct = default, HttpClient? http = null)
        => ProviderRouter.StreamQueryAsync(config,
            BuildMiningPrompt(resume, jobPosting),
            new QueryOptions(PromptLoader.LoadSystemPrompt("story_mining")),
            "mine-stories", ct, http);

    /// <summary>
    /// Parses mined stories from the model's JSON (fence-stripping port,
    /// main.py:1149-1174). Returns null when unparseable; empty-vs-wrong shape
    /// distinguished via <paramref name="error"/> using the original's messages.
    /// </summary>
    public static List<Story>? ParseMinedStories(string raw, out string error)
    {
        error = "";
        var text = raw.Trim();
        if (text.Contains("```"))
        {
            if (text.Contains("```json"))
            {
                var afterFence = text[(text.LastIndexOf("```json", StringComparison.Ordinal) + 7)..];
                var close = afterFence.IndexOf("```", StringComparison.Ordinal);
                text = close >= 0 ? afterFence[..close] : afterFence;
            }
            else
            {
                var parts = text.Split("```");
                text = parts.Length > 1 ? parts[1] : text;
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text.Trim());
        }
        catch (JsonException)
        {
            error = "Story mining returned unparseable JSON. Please try again.";
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Story mining returned unexpected format. Please try again.";
                return null;
            }

            var stories = new List<Story>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                stories.Add(new Story
                {
                    Title = GetString(r, "title", "Untitled"),
                    Situation = GetString(r, "situation"),
                    Task = GetString(r, "task"),
                    Action = GetString(r, "action"),
                    Result = GetString(r, "result"),
                    EarnedSecret = GetString(r, "earned_secret"),
                    Tags = GetStringList(r, "tags"),
                    FitScores = GetStringMap(r, "fit_scores"),
                });
            }

            return stories;
        }
    }

    private static string GetString(JsonElement e, string name, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static List<string> GetStringList(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
        return list;
    }

    private static Dictionary<string, string> GetStringMap(JsonElement e, string name)
    {
        var map = new Dictionary<string, string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var prop in v.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
        return map;
    }
}
