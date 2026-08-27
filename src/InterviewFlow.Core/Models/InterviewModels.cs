using System.Text.Json.Serialization;

namespace InterviewFlow.Core.Models;

// Mirrors app/models.py of the original 1:1 — same field names (via
// JsonPropertyName), same declaration order (drives JSON property order), same
// defaults. Deviations from Pydantic semantics are deliberate and noted:
// unknown JSON fields are ignored on read (matches Pydantic), and fields that
// Pydantic marks required (Story.title, InterviewQuestion.question) default to
// "" here — the port is permissive where the original would skip the entry.

/// <summary>Shared id/timestamp helpers matching the original's formats.</summary>
public static class ModelDefaults
{
    /// <summary>uuid4().hex[:12] equivalent.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Path-traversal guard from app/state.py — 12 lowercase hex chars.</summary>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length != 12)
            return false;
        foreach (var c in id)
        {
            if (c is (< '0' or > '9') and (< 'a' or > 'f'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// datetime.now().isoformat() equivalent: naive local time with microseconds,
    /// e.g. 2026-05-08T15:27:44.718213. The original parses these back — never
    /// switch to UTC or add an offset.
    /// </summary>
    public static string NowIso() =>
        DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
}

// ── Resume Library ───────────────────────────────────────────────────────────

public sealed class Resume
{
    [JsonPropertyName("id")] public string Id { get; set; } = ModelDefaults.NewId();
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = ModelDefaults.NowIso();
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

// ── Custom Actions ────────────────────────────────────────────────────────────

public sealed class CustomAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = ModelDefaults.NewId();
    [JsonPropertyName("name")] public string Name { get; set; } = "Custom Action";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("prompt_template")] public string PromptTemplate { get; set; } = "";
    /// <summary>null = use the API default temperature.</summary>
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = ModelDefaults.NowIso();
}

public sealed class CustomActionResult
{
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("cost_usd")] public double CostUsd { get; set; }
    [JsonPropertyName("model_name")] public string ModelName { get; set; } = "";
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    [JsonPropertyName("ran_at")] public string RanAt { get; set; } = "";
}

// ── Story Bank ───────────────────────────────────────────────────────────────

public sealed class Story
{
    [JsonPropertyName("id")] public string Id { get; set; } = ModelDefaults.NewId();
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("situation")] public string Situation { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    /// <summary>The spiky, proprietary insight.</summary>
    [JsonPropertyName("earned_secret")] public string EarnedSecret { get; set; } = "";
    /// <summary>question_type → Strong Fit|Workable|Stretch|Gap.</summary>
    [JsonPropertyName("fit_scores")] public Dictionary<string, string> FitScores { get; set; } = [];
    [JsonPropertyName("times_used")] public int TimesUsed { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = ModelDefaults.NowIso();
}

// ── Mock Interview ───────────────────────────────────────────────────────────

public sealed class InterviewQuestion
{
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";
    /// <summary>dimension → 1-5.</summary>
    [JsonPropertyName("scores")] public Dictionary<string, int> Scores { get; set; } = [];
    [JsonPropertyName("feedback")] public string Feedback { get; set; } = "";
    [JsonPropertyName("interviewer_thoughts")] public string InterviewerThoughts { get; set; } = "";
}

public sealed class MockSession
{
    [JsonPropertyName("id")] public string Id { get; set; } = ModelDefaults.NewId();
    /// <summary>behavioral|system_design|case_study|panel|bar_raiser.</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "behavioral";
    [JsonPropertyName("questions")] public List<InterviewQuestion> Questions { get; set; } = [];
    [JsonPropertyName("overall_scores")] public Dictionary<string, double> OverallScores { get; set; } = [];
    [JsonPropertyName("bottleneck")] public string Bottleneck { get; set; } = "";
    [JsonPropertyName("root_cause")] public string RootCause { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = ModelDefaults.NowIso();
}

// ── Research Outputs ─────────────────────────────────────────────────────────

public sealed class CompanyResearch
{
    [JsonPropertyName("company_name")] public string CompanyName { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("culture")] public string Culture { get; set; } = "";
    [JsonPropertyName("reputation")] public string Reputation { get; set; } = "";
    [JsonPropertyName("tech_stack")] public List<string> TechStack { get; set; } = [];
    [JsonPropertyName("products")] public List<string> Products { get; set; } = [];
    [JsonPropertyName("challenges")] public List<string> Challenges { get; set; } = [];
    [JsonPropertyName("green_flags")] public List<string> GreenFlags { get; set; } = [];
    [JsonPropertyName("red_flags")] public List<string> RedFlags { get; set; } = [];
    /// <summary>0-100.</summary>
    [JsonPropertyName("fit_score")] public int FitScore { get; set; }
    [JsonPropertyName("raw_report")] public string RawReport { get; set; } = "";
    [JsonPropertyName("query_cost_usd")] public double QueryCostUsd { get; set; }
    [JsonPropertyName("query_model_name")] public string QueryModelName { get; set; } = "";
    [JsonPropertyName("query_duration_ms")] public long QueryDurationMs { get; set; }
    [JsonPropertyName("query_ran_at")] public string QueryRanAt { get; set; } = "";
    [JsonPropertyName("researched_at")] public string ResearchedAt { get; set; } = "";
}

public sealed class JdAnalysis
{
    [JsonPropertyName("raw_jd")] public string RawJd { get; set; } = "";
    [JsonPropertyName("requirements")] public List<string> Requirements { get; set; } = [];
    [JsonPropertyName("nice_to_haves")] public List<string> NiceToHaves { get; set; } = [];
    [JsonPropertyName("hidden_signals")] public List<string> HiddenSignals { get; set; } = [];
    [JsonPropertyName("cultural_cues")] public List<string> CulturalCues { get; set; } = [];
    [JsonPropertyName("missing_signals")] public List<string> MissingSignals { get; set; } = [];
    /// <summary>requirement → HIGH|MEDIUM|LOW.</summary>
    [JsonPropertyName("confidence_tags")] public Dictionary<string, string> ConfidenceTags { get; set; } = [];
    [JsonPropertyName("raw_analysis")] public string RawAnalysis { get; set; } = "";
    [JsonPropertyName("query_cost_usd")] public double QueryCostUsd { get; set; }
    [JsonPropertyName("query_model_name")] public string QueryModelName { get; set; } = "";
    [JsonPropertyName("query_duration_ms")] public long QueryDurationMs { get; set; }
    [JsonPropertyName("query_ran_at")] public string QueryRanAt { get; set; } = "";
}

// ── Interview Intel ──────────────────────────────────────────────────────────

public sealed class InterviewIntel
{
    [JsonPropertyName("raw_report")] public string RawReport { get; set; } = "";
    [JsonPropertyName("query_cost_usd")] public double QueryCostUsd { get; set; }
    [JsonPropertyName("query_model_name")] public string QueryModelName { get; set; } = "";
    [JsonPropertyName("query_duration_ms")] public long QueryDurationMs { get; set; }
    [JsonPropertyName("query_ran_at")] public string QueryRanAt { get; set; } = "";
}

// ── Salary & Negotiation ────────────────────────────────────────────────────

public sealed class CompData
{
    [JsonPropertyName("range_low")] public long RangeLow { get; set; }
    [JsonPropertyName("range_high")] public long RangeHigh { get; set; }
    [JsonPropertyName("equity_notes")] public string EquityNotes { get; set; } = "";
    [JsonPropertyName("negotiation_scripts")] public List<string> NegotiationScripts { get; set; } = [];
    [JsonPropertyName("fallback_language")] public List<string> FallbackLanguage { get; set; } = [];
    [JsonPropertyName("raw_analysis")] public string RawAnalysis { get; set; } = "";
    [JsonPropertyName("query_cost_usd")] public double QueryCostUsd { get; set; }
    [JsonPropertyName("query_model_name")] public string QueryModelName { get; set; } = "";
    [JsonPropertyName("query_duration_ms")] public long QueryDurationMs { get; set; }
    [JsonPropertyName("query_ran_at")] public string QueryRanAt { get; set; } = "";
}

// ── Interview Pitch ──────────────────────────────────────────────────────────

public sealed class Pitch
{
    [JsonPropertyName("elevator_10s")] public string Elevator10s { get; set; } = "";
    [JsonPropertyName("networking_30s")] public string Networking30s { get; set; } = "";
    [JsonPropertyName("recruiter_60s")] public string Recruiter60s { get; set; } = "";
    [JsonPropertyName("interview_90s")] public string Interview90s { get; set; } = "";
    [JsonPropertyName("value_proposition")] public string ValueProposition { get; set; } = "";
    [JsonPropertyName("query_cost_usd")] public double QueryCostUsd { get; set; }
    [JsonPropertyName("query_model_name")] public string QueryModelName { get; set; } = "";
    [JsonPropertyName("query_duration_ms")] public long QueryDurationMs { get; set; }
    [JsonPropertyName("query_ran_at")] public string QueryRanAt { get; set; } = "";
    [JsonPropertyName("talking_points")] public List<string> TalkingPoints { get; set; } = [];
    [JsonPropertyName("thirty_sixty_ninety")] public string ThirtySixtyNinety { get; set; } = "";
}

// ── Progress Tracking ────────────────────────────────────────────────────────

public sealed class ProgressEntry
{
    [JsonPropertyName("date")] public string Date { get; set; } = ModelDefaults.NowIso();
    /// <summary>mock|real_interview|debrief|rejection.</summary>
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("scores")] public Dictionary<string, double> Scores { get; set; } = [];
    [JsonPropertyName("self_assessment")] public Dictionary<string, double> SelfAssessment { get; set; } = [];
}

// ── Master State ─────────────────────────────────────────────────────────────

/// <summary>Full persistent state for one job opportunity.</summary>
public sealed class InterviewState
{
    [JsonPropertyName("id")] public string Id { get; set; } = ModelDefaults.NewId();
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = ModelDefaults.NowIso();
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = ModelDefaults.NowIso();

    // Inputs
    [JsonPropertyName("job_posting")] public string JobPosting { get; set; } = "";
    [JsonPropertyName("resume")] public string ResumeText { get; set; } = "";
    /// <summary>Diagnostic format from DOCX extraction (original marks it TEMPORARY).</summary>
    [JsonPropertyName("resume_raw")] public string ResumeRaw { get; set; } = "";
    [JsonPropertyName("resumes")] public List<Resume> Resumes { get; set; } = [];
    /// <summary>Keyed by custom action NAME, not id.</summary>
    [JsonPropertyName("custom_action_results")] public Dictionary<string, CustomActionResult> CustomActionResults { get; set; } = [];
    [JsonPropertyName("company_name")] public string CompanyName { get; set; } = "";
    [JsonPropertyName("position")] public string Position { get; set; } = "";

    // Workflow outputs
    [JsonPropertyName("research")] public CompanyResearch Research { get; set; } = new();
    [JsonPropertyName("interview_intel")] public InterviewIntel InterviewIntel { get; set; } = new();
    [JsonPropertyName("jd_analysis")] public JdAnalysis JdAnalysis { get; set; } = new();
    [JsonPropertyName("stories")] public List<Story> Stories { get; set; } = [];
    [JsonPropertyName("stories_cost_usd")] public double StoriesCostUsd { get; set; }
    [JsonPropertyName("stories_model_name")] public string StoriesModelName { get; set; } = "";
    [JsonPropertyName("stories_duration_ms")] public long StoriesDurationMs { get; set; }
    [JsonPropertyName("stories_ran_at")] public string StoriesRanAt { get; set; } = "";
    [JsonPropertyName("mock_sessions")] public List<MockSession> MockSessions { get; set; } = [];
    [JsonPropertyName("comp_data")] public CompData CompData { get; set; } = new();
    [JsonPropertyName("pitch")] public Pitch Pitch { get; set; } = new();
    [JsonPropertyName("progress")] public List<ProgressEntry> Progress { get; set; } = [];

    // Resume tailoring
    [JsonPropertyName("resume_review")] public string ResumeReview { get; set; } = "";
    [JsonPropertyName("resume_review_cost_usd")] public double ResumeReviewCostUsd { get; set; }
    [JsonPropertyName("resume_review_model_name")] public string ResumeReviewModelName { get; set; } = "";
    [JsonPropertyName("resume_review_duration_ms")] public long ResumeReviewDurationMs { get; set; }
    [JsonPropertyName("resume_review_ran_at")] public string ResumeReviewRanAt { get; set; } = "";
    [JsonPropertyName("tailored_resume")] public string TailoredResume { get; set; } = "";
    [JsonPropertyName("resume_tagged")] public string ResumeTagged { get; set; } = "";

    // Concerns & follow-ups
    [JsonPropertyName("concerns_analysis")] public string ConcernsAnalysis { get; set; } = "";
    [JsonPropertyName("concerns_cost_usd")] public double ConcernsCostUsd { get; set; }
    [JsonPropertyName("concerns_model_name")] public string ConcernsModelName { get; set; } = "";
    [JsonPropertyName("concerns_duration_ms")] public long ConcernsDurationMs { get; set; }
    [JsonPropertyName("concerns_ran_at")] public string ConcernsRanAt { get; set; } = "";
    /// <summary>[{concern, counter_evidence}].</summary>
    [JsonPropertyName("interviewer_concerns")] public List<Dictionary<string, string>> InterviewerConcerns { get; set; } = [];
    [JsonPropertyName("thank_you_drafts")] public List<string> ThankYouDrafts { get; set; } = [];
    [JsonPropertyName("debrief_notes")] public List<string> DebriefNotes { get; set; } = [];

    // Workflow tracking
    [JsonPropertyName("completed_steps")] public List<string> CompletedSteps { get; set; } = [];
    [JsonPropertyName("current_step")] public string CurrentStep { get; set; } = "setup";
}
