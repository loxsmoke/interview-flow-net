using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>Story Bank screen (docs/03-ui-spec.md §3.5): collapsible STAR cards.</summary>
public sealed partial class StoryBankPageViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;

    public AgentRunViewModel Run { get; }
    public ObservableCollection<StoryCardViewModel> Stories { get; } = [];

    [ObservableProperty] private double _costUsd;
    [ObservableProperty] private string _costDetail = "";

    public bool HasStories => Stories.Count > 0;

    public StoryBankPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        Run = new AgentRunViewModel(shell, "stories");
        Run.Completed += Load;
        Load();
    }

    private void Load()
    {
        var s = _shell.CurrentState;
        if (s is null)
            return;
        Stories.Clear();
        foreach (var story in s.Stories)
            Stories.Add(new StoryCardViewModel(story));
        CostUsd = s.StoriesCostUsd;
        CostDetail = AgentPageViewModel.FormatCostDetail(s.StoriesModelName, s.StoriesDurationMs, s.StoriesRanAt);
        OnPropertyChanged(nameof(HasStories));
    }

    [RelayCommand]
    private void Continue() => _shell.NavigateToStep("pitch");

    public void Dispose()
    {
        Run.Completed -= Load;
        Run.Dispose();
    }
}

/// <summary>One collapsible story card with fit-score chips (§3.5).</summary>
public sealed partial class StoryCardViewModel(Story story) : ObservableObject
{
    public Story Story { get; } = story;
    public string Title => Story.Title;
    public IReadOnlyList<string> TagChips { get; } = story.Tags.Take(4).ToList();

    [ObservableProperty] private bool _isExpanded;

    public bool HasEarnedSecret => Story.EarnedSecret.Length > 0;

    public IReadOnlyList<FitScoreChip> FitChips { get; } =
        story.FitScores.Select(kv => new FitScoreChip(kv.Key, kv.Value)).ToList();

    /// <summary>Strong Fit → green, Workable → blue, Stretch → yellow, else red.</summary>
    public sealed record FitScoreChip(string Category, string Rating)
    {
        public string Color => Rating switch
        {
            "Strong Fit" => "#22C55E",
            "Workable" => "#3B82F6",
            "Stretch" => "#EAB308",
            _ => "#EF4444",
        };
    }
}
