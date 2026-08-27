using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.Prompts;
using InterviewFlow.Core.Providers;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Shared multi-turn chat plumbing (ports of MockInterviewSession /
/// ResumeChatSession): keeps the provider-facing message list and the
/// user-visible history, and runs one non-streaming turn per exchange.
/// Sessions live in memory only, like the original's session dictionaries.
/// </summary>
public abstract class ChatSessionBase(AppConfig config, HttpClient? http = null)
{
    private readonly List<ChatMessage> _messages = [];

    protected AppConfig Config { get; } = config;

    /// <summary>Visible transcript (no system prompt).</summary>
    public List<ChatMessage> History { get; } = [];

    public bool IsStarted { get; private set; }

    protected abstract string BuildSystemPrompt();

    protected abstract string OpeningUserMessage { get; }

    protected abstract double Temperature { get; }

    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        _messages.Clear();
        History.Clear();
        _messages.Add(new ChatMessage("system", BuildSystemPrompt()));
        _messages.Add(new ChatMessage("user", OpeningUserMessage));

        var response = await ChatProvider.CompleteAsync(Config, _messages, Temperature, ct, http);
        _messages.Add(new ChatMessage("assistant", response));
        History.Add(new ChatMessage("assistant", response));
        IsStarted = true;
        OnAssistantTurn(response);
        return response;
    }

    public async Task<string> RespondAsync(string userMessage, CancellationToken ct = default)
    {
        if (!IsStarted)
            throw new InvalidOperationException("Session not started");

        History.Add(new ChatMessage("user", userMessage));
        _messages.Add(new ChatMessage("user", userMessage));

        var response = await ChatProvider.CompleteAsync(Config, _messages, Temperature, ct, http);
        _messages.Add(new ChatMessage("assistant", response));
        History.Add(new ChatMessage("assistant", response));
        OnAssistantTurn(response);
        return response;
    }

    /// <summary>Hook for completion detection (mock interview's END_OF_INTERVIEW).</summary>
    protected virtual void OnAssistantTurn(string response)
    {
    }
}

/// <summary>Mock interview session (port of agents/mock_interview.py).</summary>
public sealed class MockInterviewSession(
    AppConfig config, string companyName, string jobPosting, string resume, string stories,
    string interviewFormat = "behavioral", HttpClient? http = null)
    : ChatSessionBase(config, http)
{
    /// <summary>The token that ends an interview (§3.6).</summary>
    public const string EndToken = "END_OF_INTERVIEW";

    /// <summary>Per-format instructions, verbatim from mock_interview.py:17.</summary>
    public static readonly IReadOnlyDictionary<string, string> FormatInstructions =
        new Dictionary<string, string>
        {
            ["behavioral"] = "Focus on behavioral questions (Tell me about a time when...). Probe for STAR structure. Test leadership, conflict resolution, ambiguity, failure, and impact.",
            ["system_design"] = "Present a system design problem relevant to the company's products. Evaluate: scoping, API design, high-level architecture, data model, scalability, tradeoffs. Push back on initial designs. When illustrating architecture or data flows, use Mermaid diagrams (```mermaid code blocks) — never ASCII art.",
            ["case_study"] = "Present a product/business case relevant to the company. Evaluate: problem framing, structure, creativity, data-driven thinking, prioritization, communication.",
            ["panel"] = "Simulate a panel with 2-3 interviewers (give each a name and role). Each asks questions from their perspective. Test how the candidate handles different communication styles.",
            ["bar_raiser"] = "Channel Amazon's bar raiser style — deeply behavioral, principle-focused, with rigorous follow-ups. Push until the candidate either demonstrates depth or runs out of substance.",
        };

    /// <summary>Display metadata for the format tiles (§3.6).</summary>
    public static readonly IReadOnlyList<(string Key, string Icon, string Label)> Formats =
    [
        ("behavioral", "💬", "Behavioral"),
        ("system_design", "🏗️", "System Design"),
        ("case_study", "📊", "Case Study"),
        ("panel", "👥", "Panel"),
        ("bar_raiser", "⚡", "Bar Raiser"),
    ];

    public string InterviewFormat { get; } = interviewFormat;

    public bool IsComplete { get; private set; }

    protected override double Temperature => Temperatures.ForSection("mock-interview");

    protected override string OpeningUserMessage => "Begin the interview.";

    protected override string BuildSystemPrompt() =>
        PromptLoader.LoadPrompt("mock_interview")
            .Replace("{company_name}", companyName.Length > 0 ? companyName : "the company")
            .Replace("{format}", InterviewFormat)
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume.Length > 0 ? resume : "Not provided")
            .Replace("{stories}", stories.Length > 0 ? stories : "No stories in bank yet.")
            .Replace("{format_instructions}",
                FormatInstructions.GetValueOrDefault(InterviewFormat, FormatInstructions["behavioral"]));

    protected override void OnAssistantTurn(string response)
    {
        if (response.Contains(EndToken, StringComparison.Ordinal))
            IsComplete = true;
    }

    /// <summary>
    /// Persist step from the mock/respond route: the final assistant turn
    /// becomes the session summary (main.py:3030).
    /// </summary>
    public MockSession BuildRecord(string finalMessage) => new()
    {
        Format = InterviewFormat,
        Summary = finalMessage,
    };
}

/// <summary>Resume coach chat session (port of agents/resume_chat.py).</summary>
public sealed class ResumeChatSession(
    AppConfig config, string jobPosting, string resume, string review = "", HttpClient? http = null)
    : ChatSessionBase(config, http)
{
    protected override double Temperature => Temperatures.ForSection("resume-chat");

    protected override string OpeningUserMessage =>
        "I'd like to work on tailoring my resume for this role. " +
        "Give me a brief summary of the top 3 changes that would have the most impact, " +
        "then ask me which area I'd like to start with.";

    protected override string BuildSystemPrompt() =>
        PromptLoader.LoadPrompt("resume_chat")
            .Replace("{job_posting}", jobPosting)
            .Replace("{resume}", resume)
            .Replace("{review_section}", review.Length > 0 ? $"### Previous AI Analysis\n{review}" : "");
}
