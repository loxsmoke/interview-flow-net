using System.Runtime.CompilerServices;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.Providers;
using InterviewFlow.Core.State;

namespace InterviewFlow.Core.Agents;

/// <summary>
/// Section key → agent stream with persistence (port of main.py's
/// _queued_section_stream + _stream_saved_*): validates preconditions with the
/// original's messages, streams the agent, and on completion writes the result
/// into a freshly loaded state and saves. The re-emitted CompleteEvent carries
/// the persisted text and RanAt. Faithful quirk: only research and
/// interview_intel update current_step on save.
/// </summary>
public sealed class SectionRunner(AppConfig config, StateStore store, CustomActionStore actionStore)
{
    public IAsyncEnumerable<AgentEvent> Stream(
        string stateId, string sectionKey, CancellationToken ct = default, HttpClient? http = null)
    {
        var state = store.LoadState(stateId);
        if (state is null)
            return Error("Workflow not found.");

        var providerError = CheckProviderConfigured();
        if (providerError is not null)
            return Error(providerError);

        var resume = SectionAgents.ResumeForAi(state);

        switch (sectionKey)
        {
            case "research":
                return WithSave(
                    ResearchAgent.StreamAsync(config, state.JobPosting, resume, ct, http),
                    stateId, (s, r) =>
                    {
                        s.Research.RawReport = r.Text;
                        s.Research.QueryCostUsd = r.CostUsd;
                        s.Research.QueryModelName = r.ModelName;
                        s.Research.QueryDurationMs = r.DurationMs;
                        s.Research.QueryRanAt = r.RanAt;
                        s.Research.ResearchedAt = r.RanAt;
                        MarkStep(s, "research");
                        s.CurrentStep = "research";
                    }, ct);

            case "interview_intel":
                return WithSave(
                    SectionAgents.StreamInterviewIntel(config,
                        SectionAgents.StripComment(state.CompanyName), state.JobPosting,
                        SectionAgents.StripComment(state.Position), ct, http),
                    stateId, (s, r) =>
                    {
                        s.InterviewIntel.RawReport = r.Text;
                        s.InterviewIntel.QueryCostUsd = r.CostUsd;
                        s.InterviewIntel.QueryModelName = r.ModelName;
                        s.InterviewIntel.QueryDurationMs = r.DurationMs;
                        s.InterviewIntel.QueryRanAt = r.RanAt;
                        MarkStep(s, "interview_intel");
                        s.CurrentStep = "interview_intel";
                    }, ct);

            case "jd_decode":
                return WithSave(
                    SectionAgents.StreamDecodeJd(config, state.JobPosting, ct, http),
                    stateId, (s, r) =>
                    {
                        s.JdAnalysis.RawAnalysis = r.Text;
                        s.JdAnalysis.QueryCostUsd = r.CostUsd;
                        s.JdAnalysis.QueryModelName = r.ModelName;
                        s.JdAnalysis.QueryDurationMs = r.DurationMs;
                        s.JdAnalysis.QueryRanAt = r.RanAt;
                        MarkStep(s, "jd_decode");
                    }, ct);

            case "resume_tailor":
                if (state.ResumeText.Length == 0)
                    return Error("Resume required for tailoring");
                return WithSave(
                    SectionAgents.StreamResumeReview(config, state.JobPosting, resume, ct, http),
                    stateId, (s, r) =>
                    {
                        s.ResumeReview = r.Text;
                        s.ResumeReviewCostUsd = r.CostUsd;
                        s.ResumeReviewModelName = r.ModelName;
                        s.ResumeReviewDurationMs = r.DurationMs;
                        s.ResumeReviewRanAt = r.RanAt;
                        MarkStep(s, "resume_tailor");
                    }, ct);

            case "pitch":
                if (state.ResumeText.Length == 0)
                    return Error("Resume required for pitch building");
                return WithSave(
                    SectionAgents.StreamBuildPitches(config, state.JobPosting, resume, ct, http),
                    stateId, (s, r) =>
                    {
                        s.Pitch.ValueProposition = r.Text;
                        s.Pitch.QueryCostUsd = r.CostUsd;
                        s.Pitch.QueryModelName = r.ModelName;
                        s.Pitch.QueryDurationMs = r.DurationMs;
                        s.Pitch.QueryRanAt = r.RanAt;
                        MarkStep(s, "pitch");
                    }, ct);

            case "concerns":
                if (state.ResumeText.Length == 0)
                    return Error("Resume required for concern anticipation");
                return WithSave(
                    SectionAgents.StreamAnticipateConcerns(config, state.JobPosting, resume, ct, http),
                    stateId, (s, r) =>
                    {
                        s.ConcernsAnalysis = r.Text;
                        s.ConcernsCostUsd = r.CostUsd;
                        s.ConcernsModelName = r.ModelName;
                        s.ConcernsDurationMs = r.DurationMs;
                        s.ConcernsRanAt = r.RanAt;
                        MarkStep(s, "concerns");
                    }, ct);

            case "salary":
                return WithSave(
                    SectionAgents.StreamSalaryCoach(config, state.JobPosting, resume, ct, http),
                    stateId, (s, r) =>
                    {
                        s.CompData.RawAnalysis = r.Text;
                        s.CompData.QueryCostUsd = r.CostUsd;
                        s.CompData.QueryModelName = r.ModelName;
                        s.CompData.QueryDurationMs = r.DurationMs;
                        s.CompData.QueryRanAt = r.RanAt;
                        MarkStep(s, "salary");
                    }, ct);

            case "stories":
                if (state.ResumeText.Length == 0)
                    return Error("Resume required for story mining");
                return StoriesStream(stateId, resume, state.JobPosting, ct, http);

            default:
                if (sectionKey.StartsWith("custom:", StringComparison.Ordinal))
                    return CustomActionStream(stateId, sectionKey[7..], state, ct, http);
                return Error("Section queue execution is not implemented yet");
        }
    }

    private string? CheckProviderConfigured()
    {
        var provider = ProviderRouter.ResolveProvider(config);
        var missing = provider switch
        {
            "anthropic" => config.AnthropicApiKey.Length == 0,
            "openai" => config.OpenAiApiKey.Length == 0,
            "gemini" => config.GeminiApiKey.Length == 0,
            _ => false, // ollama needs no key
        };
        return missing
            ? "No AI provider configured. Add an API key in Configuration."
            : null;
    }

    private static void MarkStep(InterviewState s, string step)
    {
        if (!s.CompletedSteps.Contains(step))
            s.CompletedSteps.Add(step);
    }

    private static async IAsyncEnumerable<AgentEvent> Error(string message)
    {
        await Task.CompletedTask;
        yield return new ErrorEvent(message);
    }

    private async IAsyncEnumerable<AgentEvent> WithSave(
        IAsyncEnumerable<AgentEvent> stream,
        string stateId,
        Action<InterviewState, CompleteEvent> save,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in stream.WithCancellation(ct))
        {
            if (evt is CompleteEvent complete)
            {
                var stamped = complete with { RanAt = ModelDefaults.NowIso() };
                var fresh = store.LoadState(stateId);
                if (fresh is not null)
                {
                    save(fresh, stamped);
                    store.SaveState(fresh);
                }

                yield return stamped;
            }
            else
            {
                yield return evt;
            }
        }
    }

    private async IAsyncEnumerable<AgentEvent> StoriesStream(
        string stateId, string resume, string jobPosting,
        [EnumeratorCancellation] CancellationToken ct, HttpClient? http)
    {
        CompleteEvent? complete = null;
        await foreach (var evt in SectionAgents.StreamMineStories(config, resume, jobPosting, ct, http))
        {
            if (evt is CompleteEvent c)
            {
                complete = c;
                break;
            }

            yield return evt;
        }

        if (complete is null)
        {
            yield return new ErrorEvent("Story mining stream ended before completion");
            yield break;
        }

        var stories = SectionAgents.ParseMinedStories(complete.Text, out var parseError);
        if (stories is null)
        {
            yield return new ErrorEvent(parseError);
            yield break;
        }

        var ranAt = ModelDefaults.NowIso();
        var fresh = store.LoadState(stateId);
        if (fresh is not null)
        {
            fresh.Stories = stories;
            fresh.StoriesCostUsd = complete.CostUsd;
            fresh.StoriesModelName = complete.ModelName;
            fresh.StoriesDurationMs = complete.DurationMs;
            fresh.StoriesRanAt = ranAt;
            MarkStep(fresh, "stories");
            store.SaveState(fresh);
        }

        yield return complete with { RanAt = ranAt };
    }

    private async IAsyncEnumerable<AgentEvent> CustomActionStream(
        string stateId, string actionId, InterviewState state,
        [EnumeratorCancellation] CancellationToken ct, HttpClient? http)
    {
        var action = actionStore.Load().FirstOrDefault(a => a.Id == actionId);
        if (action is null)
        {
            yield return new ErrorEvent("Custom action not found");
            yield break;
        }

        await foreach (var evt in CustomActionAgent.Stream(config, action, state, ct, http))
        {
            if (evt is CompleteEvent complete)
            {
                var ranAt = ModelDefaults.NowIso();
                var fresh = store.LoadState(stateId);
                if (fresh is not null)
                {
                    fresh.CustomActionResults[action.Name] = new CustomActionResult
                    {
                        Result = complete.Text,
                        CostUsd = complete.CostUsd,
                        ModelName = complete.ModelName,
                        DurationMs = complete.DurationMs,
                        RanAt = ranAt,
                    };
                    MarkStep(fresh, $"custom_{actionId}");
                    store.SaveState(fresh);
                }

                yield return complete with { RanAt = ranAt };
            }
            else
            {
                yield return evt;
            }
        }
    }
}
