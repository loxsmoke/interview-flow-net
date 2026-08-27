using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Generic agent screen (docs/03-ui-spec.md §3.4) for the markdown-report
/// sections: research, interview_intel, jd_decode, pitch, concerns, salary.
/// Body state (report/cost) reloads from the shell's current workflow whenever
/// a run completes.
/// </summary>
public sealed partial class AgentPageViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;

    public string SectionKey { get; }
    public string Title { get; }
    public string Description { get; }
    public bool WebSearch { get; }
    public AgentRunViewModel Run { get; }

    [ObservableProperty] private string _report = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private double _costUsd;
    [ObservableProperty] private string _costDetail = "";

    public bool HasReport => Report.Length > 0;

    public AgentPageViewModel(MainViewModel shell, StepItemViewModel step)
    {
        _shell = shell;
        SectionKey = step.Key;
        Title = step.Label;
        Description = step.Description;
        WebSearch = step.WebSearch;
        Run = new AgentRunViewModel(shell, step.Key);
        Run.Completed += Load;
        Load();
    }

    private void Load()
    {
        var s = _shell.CurrentState;
        if (s is null)
            return;
        var (report, cost, model, durationMs, ranAt) = SectionKey switch
        {
            "research" => (s.Research.RawReport, s.Research.QueryCostUsd, s.Research.QueryModelName,
                s.Research.QueryDurationMs, s.Research.QueryRanAt),
            "interview_intel" => (s.InterviewIntel.RawReport, s.InterviewIntel.QueryCostUsd,
                s.InterviewIntel.QueryModelName, s.InterviewIntel.QueryDurationMs, s.InterviewIntel.QueryRanAt),
            "jd_decode" => (s.JdAnalysis.RawAnalysis, s.JdAnalysis.QueryCostUsd, s.JdAnalysis.QueryModelName,
                s.JdAnalysis.QueryDurationMs, s.JdAnalysis.QueryRanAt),
            "pitch" => (s.Pitch.ValueProposition, s.Pitch.QueryCostUsd, s.Pitch.QueryModelName,
                s.Pitch.QueryDurationMs, s.Pitch.QueryRanAt),
            "concerns" => (s.ConcernsAnalysis, s.ConcernsCostUsd, s.ConcernsModelName,
                s.ConcernsDurationMs, s.ConcernsRanAt),
            "salary" => (s.CompData.RawAnalysis, s.CompData.QueryCostUsd, s.CompData.QueryModelName,
                s.CompData.QueryDurationMs, s.CompData.QueryRanAt),
            _ => ("", 0.0, "", 0L, ""),
        };
        Report = report;
        CostUsd = cost;
        CostDetail = FormatCostDetail(model, durationMs, ranAt);
    }

    /// <summary>Tooltip: model, duration, local run time (§3.4).</summary>
    public static string FormatCostDetail(string model, long durationMs, string ranAt)
    {
        if (model.Length == 0 && ranAt.Length == 0)
            return "";
        var duration = durationMs >= 1000 ? $"{durationMs / 1000.0:0.#}s" : $"{durationMs}ms";
        var ran = ranAt.Length >= 16 ? ranAt[..16].Replace('T', ' ') : ranAt;
        return $"Model: {model}\nDuration: {duration}\nRan: {ran}";
    }

    [RelayCommand]
    private void Continue()
    {
        var steps = _shell.Steps;
        var index = steps.ToList().FindIndex(s => s.Key == SectionKey);
        if (index >= 0 && index + 1 < steps.Count)
            _shell.NavigateToStep(steps[index + 1].Key);
    }

    public void Dispose()
    {
        Run.Completed -= Load;
        Run.Dispose();
    }
}
