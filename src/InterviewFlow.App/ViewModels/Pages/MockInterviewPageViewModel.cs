using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>One format tile on the Mock Interview setup screen (§3.6).</summary>
public sealed partial class FormatTileViewModel(
    string key, string icon, string label, Action<FormatTileViewModel> select) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

/// <summary>
/// Mock Interview screen (§3.6): format tiles → chat. The session lives in
/// memory (like the original) and persists a MockSession when the interviewer
/// emits END_OF_INTERVIEW.
/// </summary>
public sealed partial class MockInterviewPageViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;

    public List<FormatTileViewModel> Formats { get; }
    public ChatViewModel Chat { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup))]
    private bool _inInterview;

    [ObservableProperty] private string _selectedFormat = "behavioral";

    public bool ShowSetup => !InInterview;

    public string SelectedFormatLabel =>
        Formats.FirstOrDefault(f => f.Key == SelectedFormat)?.Label ?? "Behavioral";

    public string StartButtonText => $"Start {SelectedFormatLabel} Interview";

    public string ChatHeader => $"Mock Interview — {SelectedFormatLabel}";

    public MockInterviewPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        Formats = MockInterviewSession.Formats
            .Select(f => new FormatTileViewModel(f.Key, f.Icon, f.Label, SelectFormat))
            .ToList();
        Formats[0].IsSelected = true;

        Chat = new ChatViewModel(_ => Task.FromResult<ChatSessionBase>(BuildSession()),
            userCaption: "You", assistantCaption: "Interviewer");
        Chat.SessionCompleted += OnSessionCompleted;
    }

    private MockInterviewSession BuildSession()
    {
        var s = _shell.CurrentState!;
        return new MockInterviewSession(
            _shell.Config,
            SectionAgents.StripComment(s.CompanyName),
            s.JobPosting,
            SectionAgents.ResumeForAi(s),
            SectionAgents.StoriesAsText(s.Stories),
            SelectedFormat);
    }

    private void SelectFormat(FormatTileViewModel tile)
    {
        foreach (var f in Formats)
            f.IsSelected = f.Key == tile.Key;
        SelectedFormat = tile.Key;
        OnPropertyChanged(nameof(SelectedFormatLabel));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(ChatHeader));
    }

    private void OnSessionCompleted(ChatSessionBase session, string finalMessage)
    {
        if (session is not MockInterviewSession mock || _shell.CurrentState is not { } s)
            return;
        s.MockSessions.Add(mock.BuildRecord(finalMessage));
        if (!s.CompletedSteps.Contains("mock_interview"))
            s.CompletedSteps.Add("mock_interview");
        _shell.Store.SaveState(s);
        _shell.NotifyStateChanged();
    }

    [RelayCommand]
    private async Task StartInterviewAsync()
    {
        InInterview = true;
        await Chat.StartAsync();
    }

    [RelayCommand]
    private void NewInterview()
    {
        Chat.Cancel();
        InInterview = false;
    }

    [RelayCommand]
    private void Continue() => _shell.NavigateToStep("salary");

    public void Dispose()
    {
        Chat.SessionCompleted -= OnSessionCompleted;
        Chat.Cancel();
    }
}
