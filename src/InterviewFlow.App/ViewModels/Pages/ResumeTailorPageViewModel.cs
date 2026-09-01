using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Resume Tailor (§3.7): AI analysis pane | resume editor with
/// Edit/Preview/Comparison tabs, "Use AI Resume" draft extraction, the coach
/// chat panel, and .docx export. Unsaved edits auto-save on page swap.
/// </summary>
public sealed partial class ResumeTailorPageViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;
    private string _lastSaved = "";
    // Diff baseline: the resume as it stood when this page loaded. Unlike
    // _lastSaved it survives Save/auto-save, so the Comparison tab keeps
    // showing what tailoring changed instead of going blank after a save.
    private string _diffBaseline = "";

    public AgentRunViewModel Run { get; }
    public ChatViewModel Coach { get; }
    public ObservableCollection<DiffRow> DiffRows { get; } = [];

    [ObservableProperty] private string _analysis = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _taggedResume = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _showCoach;
    [ObservableProperty] private bool _justSaved;
    [ObservableProperty] private string _updateError = "";
    [ObservableProperty] private bool _hasDiff;
    [ObservableProperty] private string _diffCaption = "";
    [ObservableProperty] private double _costUsd;
    [ObservableProperty] private string _costDetail = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportPath))]
    private string _exportedPath = "";

    public bool HasAnalysis => Analysis.Length > 0;
    public bool HasAiDraft => TailoredResume.HasDraft(Analysis);
    public bool IsDirty => TaggedResume != _lastSaved;
    public bool HasExportPath => ExportedPath.Length > 0;
    public bool HasUpdateError => UpdateError.Length > 0;

    public string ContactName => _shell.Config.ResumeName;
    public string ContactInfo => _shell.Config.ResumeContact;

    /// <summary>The view runs the save dialog + write, then reports the path.</summary>
    public event Func<string, Task<string?>>? ExportRequested;

    public ResumeTailorPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        Run = new AgentRunViewModel(shell, "resume_tailor");
        Run.Completed += Load;

        Coach = new ChatViewModel(_ => Task.FromResult<ChatSessionBase>(BuildCoachSession()),
            userCaption: "You", assistantCaption: "Coach");

        Load();
    }

    private ResumeChatSession BuildCoachSession()
    {
        var s = _shell.CurrentState!;
        return new ResumeChatSession(
            _shell.Config, s.JobPosting, SectionAgents.ResumeForAi(s), s.ResumeReview);
    }

    private void Load()
    {
        var s = _shell.CurrentState;
        if (s is null)
            return;
        Analysis = s.ResumeReview;
        TaggedResume = s.ResumeTagged.Length > 0 ? s.ResumeTagged : s.ResumeText;
        _lastSaved = TaggedResume;
        _diffBaseline = TaggedResume;
        CostUsd = s.ResumeReviewCostUsd;
        CostDetail = AgentPageViewModel.FormatCostDetail(
            s.ResumeReviewModelName, s.ResumeReviewDurationMs, s.ResumeReviewRanAt);
        OnPropertyChanged(nameof(HasAnalysis));
        OnPropertyChanged(nameof(HasAiDraft));
        OnPropertyChanged(nameof(IsDirty));
        RebuildDiff();
    }

    partial void OnTaggedResumeChanged(string value) => RebuildDiff();

    partial void OnAnalysisChanged(string value) => RebuildDiff();

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 2)
            RebuildDiff();
    }

    private void RebuildDiff()
    {
        var rows = LineDiff.Compute(_diffBaseline, TaggedResume);
        var caption = "Resume on entry  →  current editor text";

        // Untouched editor: fall back to the draft inside the analysis so the
        // tab shows what the tailor proposes instead of an all-grey copy.
        if (!rows.Exists(r => r.Kind != DiffKind.Same))
        {
            var draft = TailoredResume.Extract(Analysis);
            if (draft is not null && draft.Trim() != TaggedResume.Trim())
            {
                rows = LineDiff.Compute(TaggedResume, draft);
                caption = "Current resume  →  AI tailored draft";
            }
        }

        DiffRows.Clear();
        foreach (var row in rows)
            DiffRows.Add(row);
        HasDiff = rows.Exists(r => r.Kind != DiffKind.Same);
        DiffCaption = HasDiff ? caption : "";
    }

    [RelayCommand]
    private void UseAiResume()
    {
        UpdateError = "";
        var draft = TailoredResume.Extract(Analysis);
        if (draft is null)
        {
            UpdateError = TailoredResume.NoDraftMessage;
            OnPropertyChanged(nameof(HasUpdateError));
            return;
        }

        TaggedResume = draft;
        JustSaved = false;
        SelectedTab = 0;
        OnPropertyChanged(nameof(HasUpdateError));
    }

    [RelayCommand]
    private async Task ToggleCoachAsync()
    {
        ShowCoach = !ShowCoach;
        if (ShowCoach && !Coach.IsStarted)
            await Coach.StartAsync();
    }

    [RelayCommand]
    private void Save()
    {
        var s = _shell.CurrentState;
        if (s is null)
            return;
        s.ResumeTagged = TaggedResume;
        s.ResumeText = TailoredResume.StripTags(TaggedResume);
        _shell.Store.SaveState(s);
        _lastSaved = TaggedResume;
        OnPropertyChanged(nameof(IsDirty));
        RebuildDiff();
        JustSaved = true;
        _ = ClearSavedFlagAsync();
    }

    private async Task ClearSavedFlagAsync()
    {
        await Task.Delay(2000);
        JustSaved = false;
    }

    [RelayCommand]
    private async Task ExportDocxAsync()
    {
        if (ExportRequested is null || TaggedResume.Trim().Length == 0)
            return;
        var path = await ExportRequested(TaggedResume);
        if (path is not null)
            ExportedPath = path;
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (ExportedPath.Length > 0)
            Platform.ShellOpen.RevealInFileManager(ExportedPath);
    }

    [RelayCommand]
    private void Continue() => _shell.NavigateToStep("stories");

    /// <summary>Shell access for the view's export call.</summary>
    public MainViewModel Shell => _shell;

    public void Dispose()
    {
        // Auto-save unsaved edits on unmount (§3.7).
        if (IsDirty)
            Save();
        Run.Completed -= Load;
        Run.Dispose();
        Coach.Cancel();
    }
}
