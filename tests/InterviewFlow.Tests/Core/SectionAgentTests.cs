using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.Core;

public sealed class SectionAgentHelperTests
{
    [Fact]
    public void Strip_comment_cuts_at_pipe()
    {
        Assert.Equal("Acme", SectionAgents.StripComment("Acme | backend team"));
        Assert.Equal("Acme", SectionAgents.StripComment("Acme"));
    }

    [Fact]
    public void Resume_for_ai_strips_tag_prefixes_or_falls_back()
    {
        var s = new InterviewState
        {
            ResumeText = "plain resume",
            ResumeTagged = "[Section Heading]Experience\n[Job title]Staff Engineer | Acme\n[Job bullet]Did things",
        };
        Assert.Equal("Experience\nStaff Engineer | Acme\nDid things", SectionAgents.ResumeForAi(s));

        s.ResumeTagged = "";
        Assert.Equal("plain resume", SectionAgents.ResumeForAi(s));
    }

    [Fact]
    public void Stories_as_text_formats_like_the_original()
    {
        Assert.Equal("No stories yet.", SectionAgents.StoriesAsText([]));

        var text = SectionAgents.StoriesAsText([new Story
        {
            Title = "Migration",
            Situation = "S", Task = "T", Action = "A", Result = "R",
            EarnedSecret = "E", Tags = ["x", "y"],
        }]);
        Assert.StartsWith("### Migration\n- Situation: S\n- Task: T\n- Action: A\n- Result: R\n- Earned Secret: E\n- Tags: x, y", text);
    }

    [Fact]
    public void Intel_prompt_injects_technical_section_only_for_technical_roles()
    {
        var tech = SectionAgents.BuildInterviewIntelPrompt("Acme", "JD", "Staff Software Engineer");
        var nonTech = SectionAgents.BuildInterviewIntelPrompt("Acme", "JD", "Head of Marketing");
        Assert.True(tech.Length > nonTech.Length);
        Assert.DoesNotContain("{technical_section}", tech);
        Assert.DoesNotContain("{technical_section}", nonTech);
        Assert.DoesNotContain("{company_name}", tech);
    }

    [Fact]
    public void Salary_prompt_uses_not_provided_for_missing_resume()
    {
        Assert.Contains("Not provided", SectionAgents.BuildSalaryPrompt("JD", ""));
        Assert.DoesNotContain("Not provided", SectionAgents.BuildSalaryPrompt("JD", "resume text"));
    }

    [Fact]
    public void Mined_stories_parse_with_and_without_fences()
    {
        const string json = """[{"title":"T1","situation":"S","tags":["a"],"fit_scores":{"behavioral":"Strong Fit"}}]""";

        foreach (var raw in new[] { json, $"```json\n{json}\n```", $"noise\n```\n{json}\n```\nmore" })
        {
            var stories = SectionAgents.ParseMinedStories(raw, out var error);
            Assert.NotNull(stories);
            Assert.Equal("", error);
            var story = Assert.Single(stories!);
            Assert.Equal("T1", story.Title);
            Assert.Equal("Strong Fit", story.FitScores["behavioral"]);
        }
    }

    [Fact]
    public void Mined_stories_error_messages_match_the_original()
    {
        Assert.Null(SectionAgents.ParseMinedStories("not json at all", out var e1));
        Assert.Equal("Story mining returned unparseable JSON. Please try again.", e1);

        Assert.Null(SectionAgents.ParseMinedStories("""{"stories":[]}""", out var e2));
        Assert.Equal("Story mining returned unexpected format. Please try again.", e2);
    }

    [Fact]
    public void Missing_title_defaults_to_untitled()
    {
        var stories = SectionAgents.ParseMinedStories("""[{"situation":"S"}]""", out _);
        Assert.Equal("Untitled", stories![0].Title);
    }
}

public sealed class CustomActionAgentTests
{
    [Fact]
    public void Substitutes_known_tags_with_wrapped_content()
    {
        var state = new InterviewState
        {
            CompanyName = "Acme | note",
            Position = "SE",
            JobPosting = "the posting",
        };
        var result = CustomActionAgent.SubstituteTags(
            "Company: {{company_name}}\nJD: {{job_posting}}\nResearch: {{research}}", state);

        Assert.Contains("<user_provided_company_name>\nAcme\n</user_provided_company_name>", result);
        Assert.Contains("<user_provided_job_posting>\nthe posting\n</user_provided_job_posting>", result);
        Assert.Contains("Research: (not provided)", result); // empty → literal
        Assert.DoesNotContain("{{", result);
    }

    [Fact]
    public void Unknown_tags_are_left_alone_and_detected()
    {
        var result = CustomActionAgent.SubstituteTags("Hello {{wat}}", new InterviewState());
        Assert.Contains("{{wat}}", result);

        Assert.Equal(["wat"], CustomActionAgent.FindUnknownTags("x {{wat}} {{resume}} {{wat}}"));
        Assert.Empty(CustomActionAgent.FindUnknownTags("{{resume}} {{comp_data}}"));
    }

    [Fact]
    public void Pitch_tag_joins_populated_variants()
    {
        var state = new InterviewState();
        state.Pitch.ValueProposition = "VP";
        state.Pitch.Elevator10s = "E10";
        var result = CustomActionAgent.SubstituteTags("{{pitch}}", state);
        Assert.Contains("VP\n\nE10", result);
    }
}

public sealed class SectionRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-runner-" + Guid.NewGuid().ToString("N")[..8]);

    private (SectionRunner Runner, StateStore Store, CustomActionStore Actions, FakeHandler Handler) Make()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, "ACTIVE_PROVIDER=ollama\nOLLAMA_MODEL=llama3.2\n");
        var config = new AppConfig(EnvFile.Load(envPath));
        var store = new StateStore(_dir);
        var actions = new CustomActionStore(_dir);
        var handler = new FakeHandler();
        return (new SectionRunner(config, store, actions), store, actions, handler);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task<List<AgentEvent>> Drain(IAsyncEnumerable<AgentEvent> stream)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in stream)
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Jd_decode_streams_and_persists()
    {
        var (runner, store, _, handler) = Make();
        var state = new InterviewState { JobPosting = "the JD" };
        store.SaveState(state);
        handler.Enqueue(HttpStatusCode.OK,
            "{\"message\":{\"content\":\"## Six lens read\"}}", "application/x-ndjson");

        var events = await Drain(runner.Stream(state.Id, "jd_decode", http: new HttpClient(handler)));

        var complete = Assert.IsType<CompleteEvent>(events[^1]);
        Assert.NotEmpty(complete.RanAt);
        var saved = store.LoadState(state.Id)!;
        Assert.Equal("## Six lens read", saved.JdAnalysis.RawAnalysis);
        Assert.Equal(complete.RanAt, saved.JdAnalysis.QueryRanAt);
        Assert.Contains("jd_decode", saved.CompletedSteps);
        Assert.NotEqual("jd_decode", saved.CurrentStep); // only research/intel move current_step
    }

    [Fact]
    public async Task Resume_required_sections_error_with_original_messages()
    {
        var (runner, store, _, _) = Make();
        var state = new InterviewState { JobPosting = "JD" }; // no resume
        store.SaveState(state);

        foreach (var (section, message) in new[]
        {
            ("resume_tailor", "Resume required for tailoring"),
            ("pitch", "Resume required for pitch building"),
            ("concerns", "Resume required for concern anticipation"),
            ("stories", "Resume required for story mining"),
        })
        {
            var events = await Drain(runner.Stream(state.Id, section));
            var error = Assert.IsType<ErrorEvent>(Assert.Single(events));
            Assert.Equal(message, error.Message);
        }
    }

    [Fact]
    public async Task Stories_parse_and_replace_state_stories()
    {
        var (runner, store, _, handler) = Make();
        var state = new InterviewState { JobPosting = "JD", ResumeText = "resume" };
        state.Stories.Add(new Story { Title = "Old story" });
        store.SaveState(state);
        handler.Enqueue(HttpStatusCode.OK,
            "{\"message\":{\"content\":\"[{\\\"title\\\":\\\"Fresh\\\",\\\"tags\\\":[\\\"t\\\"]}]\"}}",
            "application/x-ndjson");

        var events = await Drain(runner.Stream(state.Id, "stories", http: new HttpClient(handler)));

        Assert.IsType<CompleteEvent>(events[^1]);
        var saved = store.LoadState(state.Id)!;
        var story = Assert.Single(saved.Stories);
        Assert.Equal("Fresh", story.Title);
        Assert.Contains("stories", saved.CompletedSteps);
    }

    [Fact]
    public async Task Custom_action_saves_result_keyed_by_name()
    {
        var (runner, store, actions, handler) = Make();
        var action = new CustomAction { Name = "My action", PromptTemplate = "Do {{company_name}}" };
        actions.Save([action]);
        var state = new InterviewState { CompanyName = "Acme" };
        store.SaveState(state);
        handler.Enqueue(HttpStatusCode.OK,
            "{\"message\":{\"content\":\"the answer\"}}", "application/x-ndjson");

        var events = await Drain(runner.Stream(state.Id, $"custom:{action.Id}", http: new HttpClient(handler)));

        Assert.IsType<CompleteEvent>(events[^1]);
        var saved = store.LoadState(state.Id)!;
        Assert.Equal("the answer", saved.CustomActionResults["My action"].Result);
        Assert.Contains($"custom_{action.Id}", saved.CompletedSteps);
        // The substituted prompt reached the provider.
        Assert.Contains("user_provided_company_name", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Unknown_custom_action_errors()
    {
        var (runner, store, _, _) = Make();
        var state = new InterviewState();
        store.SaveState(state);
        var events = await Drain(runner.Stream(state.Id, "custom:deadbeef0000"));
        Assert.Equal("Custom action not found", Assert.IsType<ErrorEvent>(Assert.Single(events)).Message);
    }

    [Fact]
    public async Task Missing_provider_key_errors_before_streaming()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env2");
        File.WriteAllText(envPath, "ACTIVE_PROVIDER=anthropic\n"); // no key
        var store = new StateStore(_dir);
        var state = new InterviewState { JobPosting = "JD" };
        store.SaveState(state);
        var runner = new SectionRunner(new AppConfig(EnvFile.Load(envPath)), store, new CustomActionStore(_dir));

        var events = await Drain(runner.Stream(state.Id, "research"));
        Assert.Contains("No AI provider configured", Assert.IsType<ErrorEvent>(Assert.Single(events)).Message);
    }
}
