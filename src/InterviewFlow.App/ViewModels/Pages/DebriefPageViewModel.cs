using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Debrief screen (docs/03-ui-spec.md §3.8): total cost badge with a
/// per-section tooltip, notes editor, insert timestamp, save (appends to
/// debrief_notes + a ProgressEntry, matching the original route).
/// </summary>
public sealed partial class DebriefPageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _justSaved;

    public double TotalCost { get; private set; }
    public int QueryCount { get; private set; }
    public string CostLabel => $"Cost: ${TotalCost:0.00} · Queries: {QueryCount}";
    public string CostDetail { get; private set; } = "";

    public DebriefPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        var s = shell.CurrentState;
        if (s is null)
            return;
        // Continue editing the latest note; Save appends a fresh entry.
        _notes = s.DebriefNotes.Count > 0 ? s.DebriefNotes[^1] : "";
        ComputeTotals(s);
    }

    private void ComputeTotals(InterviewState s)
    {
        var rows = new List<(string Section, double Cost, string Model)>
        {
            ("Company Research", s.Research.QueryCostUsd, s.Research.QueryModelName),
            ("Interview Intel", s.InterviewIntel.QueryCostUsd, s.InterviewIntel.QueryModelName),
            ("Job Decoder", s.JdAnalysis.QueryCostUsd, s.JdAnalysis.QueryModelName),
            ("Resume Tailor", s.ResumeReviewCostUsd, s.ResumeReviewModelName),
            ("Story Bank", s.StoriesCostUsd, s.StoriesModelName),
            ("Pitch", s.Pitch.QueryCostUsd, s.Pitch.QueryModelName),
            ("Concerns", s.ConcernsCostUsd, s.ConcernsModelName),
            ("Salary", s.CompData.QueryCostUsd, s.CompData.QueryModelName),
        };
        rows.AddRange(s.CustomActionResults.Select(kv => (kv.Key, kv.Value.CostUsd, kv.Value.ModelName)));

        var detail = new StringBuilder();
        foreach (var (section, cost, model) in rows)
        {
            if (model.Length == 0 && cost == 0)
                continue;
            QueryCount++;
            TotalCost += cost;
            detail.AppendLine($"{section}: ${cost:0.0000} ({model})");
        }

        CostDetail = detail.ToString().TrimEnd();
        OnPropertyChanged(nameof(CostLabel));
        OnPropertyChanged(nameof(CostDetail));
    }

    [RelayCommand]
    private void InsertTimestamp()
    {
        var stamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] ";
        Notes = Notes.Length == 0 ? stamp : Notes.TrimEnd() + "\n\n" + stamp;
    }

    [RelayCommand]
    private void SaveDebrief()
    {
        var s = _shell.CurrentState;
        if (s is null || Notes.Trim().Length == 0)
            return;
        s.DebriefNotes.Add(Notes);
        s.Progress.Add(new ProgressEntry { EventType = "debrief", Notes = Notes });
        if (!s.CompletedSteps.Contains("debrief"))
            s.CompletedSteps.Add("debrief");
        _shell.Store.SaveState(s);
        _shell.NotifyStateChanged();
        JustSaved = true;
        _ = ClearSavedFlagAsync();
    }

    private async Task ClearSavedFlagAsync()
    {
        await Task.Delay(2000);
        JustSaved = false;
    }
}
