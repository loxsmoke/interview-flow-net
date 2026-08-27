using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Tests.Core;

public sealed class TailoredResumeTests
{
    private const string Analysis = """
        ## 1. Fit Analysis
        Some prose.

        ## 6. Tailored Resume Draft
        [Section Heading]Summary
        [Summary]Engineer who ships.

        ## A note on the changes
        I removed two bullets.
        """;

    [Fact]
    public void Extracts_section_six_and_strips_trailing_note()
    {
        Assert.True(TailoredResume.HasDraft(Analysis));
        var draft = TailoredResume.Extract(Analysis);
        Assert.Equal("[Section Heading]Summary\n[Summary]Engineer who ships.", draft);
    }

    [Theory]
    [InlineData("###### 6 — tailored resume draft (final)\ncontent")]
    [InlineData("# 6. TAILORED RESUME DRAFT\ncontent")]
    [InlineData("## 6) tailored resume draft\ncontent")]
    public void Heading_variants_are_recognized(string analysis)
    {
        Assert.True(TailoredResume.HasDraft(analysis));
        Assert.Equal("content", TailoredResume.Extract(analysis));
    }

    [Fact]
    public void Missing_draft_returns_null_with_the_original_message()
    {
        Assert.False(TailoredResume.HasDraft("## 1. Analysis\nno draft here"));
        Assert.Null(TailoredResume.Extract("## 1. Analysis\nno draft here"));
        Assert.StartsWith("No tailored resume draft found", TailoredResume.NoDraftMessage);
    }

    [Fact]
    public void Strip_tags_produces_the_plain_body() =>
        Assert.Equal("Summary\nEngineer who ships.",
            TailoredResume.StripTags("[Section Heading]Summary\n[Summary]Engineer who ships."));
}

public sealed class ChatSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-chat-" + Guid.NewGuid().ToString("N")[..8]);

    private (AppConfig Config, FakeHandler Handler) Make()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, "ACTIVE_PROVIDER=ollama\nOLLAMA_MODEL=llama3.2\n");
        return (new AppConfig(EnvFile.Load(path)), new FakeHandler());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void EnqueueReply(FakeHandler handler, string content) =>
        handler.Enqueue(HttpStatusCode.OK,
            $"{{\"message\":{{\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}}}}",
            "application/json");

    [Fact]
    public async Task Mock_session_starts_and_tracks_history()
    {
        var (config, handler) = Make();
        EnqueueReply(handler, "Welcome. Tell me about yourself.");
        var session = new MockInterviewSession(config, "Acme", "JD", "resume", "stories",
            "behavioral", new HttpClient(handler));

        var opening = await session.StartAsync();
        Assert.Equal("Welcome. Tell me about yourself.", opening);
        Assert.True(session.IsStarted);
        Assert.False(session.IsComplete);
        Assert.Single(session.History);

        // The system prompt carries the format instructions and context.
        Assert.Contains("behavioral", handler.Requests[0].Body);
        Assert.Contains("STAR structure", handler.Requests[0].Body);
        Assert.Contains("Begin the interview.", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Mock_session_completes_on_end_token_and_builds_a_record()
    {
        var (config, handler) = Make();
        EnqueueReply(handler, "Opening question?");
        EnqueueReply(handler, "Good answer. Final debrief… END_OF_INTERVIEW");
        var session = new MockInterviewSession(config, "Acme", "JD", "resume", "stories",
            "system_design", new HttpClient(handler));

        await session.StartAsync();
        var reply = await session.RespondAsync("My answer");

        Assert.True(session.IsComplete);
        Assert.Equal(3, session.History.Count); // opening, user, final
        var record = session.BuildRecord(reply);
        Assert.Equal("system_design", record.Format);
        Assert.Contains("Final debrief", record.Summary);
    }

    [Fact]
    public async Task Mock_session_sends_the_full_transcript_each_turn()
    {
        var (config, handler) = Make();
        EnqueueReply(handler, "Q1");
        EnqueueReply(handler, "Q2");
        var session = new MockInterviewSession(config, "Acme", "JD", "", "", "panel", new HttpClient(handler));

        await session.StartAsync();
        await session.RespondAsync("A1");

        // Second request replays system + opening + assistant + user turns.
        var body = handler.Requests[1].Body;
        Assert.Contains("Begin the interview.", body);
        Assert.Contains("Q1", body);
        Assert.Contains("A1", body);
        Assert.Contains("2-3 interviewers", body); // panel format instructions
    }

    [Fact]
    public async Task Resume_chat_uses_the_opening_message_and_review_section()
    {
        var (config, handler) = Make();
        EnqueueReply(handler, "Here are the top 3 changes…");
        var session = new ResumeChatSession(config, "JD", "resume text", "PRIOR ANALYSIS",
            new HttpClient(handler));

        var opening = await session.StartAsync();
        Assert.StartsWith("Here are the top 3", opening);
        var body = handler.Requests[0].Body;
        Assert.Contains("top 3 changes", body);          // opening user message
        Assert.Contains("Previous AI Analysis", body);   // review section injected
        Assert.Contains("PRIOR ANALYSIS", body);
    }

    [Fact]
    public async Task Responding_before_start_throws()
    {
        var (config, handler) = Make();
        var session = new ResumeChatSession(config, "JD", "resume", http: new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RespondAsync("hi"));
    }

    [Fact]
    public async Task Chat_temperature_is_the_section_value_clamped_for_ollama()
    {
        var (config, handler) = Make();
        EnqueueReply(handler, "ok");
        var session = new MockInterviewSession(config, "Acme", "JD", "", "", "behavioral",
            new HttpClient(handler));
        await session.StartAsync();
        // mock-interview → 0.9, under the 1.0 Ollama clamp.
        Assert.Contains("\"temperature\":0.9", handler.Requests[0].Body);
    }

    [Fact]
    public void Format_metadata_matches_the_original()
    {
        Assert.Equal(5, MockInterviewSession.Formats.Count);
        Assert.Equal(["behavioral", "system_design", "case_study", "panel", "bar_raiser"],
            MockInterviewSession.Formats.Select(f => f.Key));
        Assert.All(MockInterviewSession.Formats,
            f => Assert.True(MockInterviewSession.FormatInstructions.ContainsKey(f.Key)));
        Assert.Contains("Mermaid diagrams", MockInterviewSession.FormatInstructions["system_design"]);
    }
}
