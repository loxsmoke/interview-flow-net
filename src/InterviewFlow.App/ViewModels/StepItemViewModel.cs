using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// One sidebar step row (docs/03-ui-spec.md §3.1). Static identity (key, icon,
/// label, flags) comes from the STEPS table; dynamic state (active/done/locked,
/// later running/failed/queued from the queue) is set by MainViewModel.
/// </summary>
public sealed partial class StepItemViewModel(
    string key, string icon, string label, string description,
    bool webSearch, bool needsResume, Action<StepItemViewModel> navigate) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public bool WebSearch { get; } = webSearch;
    public bool NeedsResume { get; } = needsResume;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private bool _showTech;

    /// <summary>Tile glyph: ! (failed) and ✓ (done) replace the emoji, like the original.</summary>
    public string TileGlyph => IsFailed ? "!" : IsDone ? "✓" : Icon;

    /// <summary>
    /// While the section runs, the spinner replaces the glyph in the tile —
    /// the original swaps the same way (index.html renderStepIcon).
    /// </summary>
    public bool ShowGlyph => !IsRunning;

    /// <summary>Tile tooltip: the original's title/aria-label on the icon.</summary>
    public string StatusLabel =>
        IsFailed ? "Failed" : IsRunning ? "Running" : IsQueued ? "Queued" : "";

    public bool HasStatus => StatusLabel.Length > 0;

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(TileGlyph));

    partial void OnIsFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(TileGlyph));
        NotifyStatus();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGlyph));
        NotifyStatus();
    }

    partial void OnIsQueuedChanged(bool value) => NotifyStatus();

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasStatus));
    }

    [RelayCommand]
    private void Navigate() => navigate(this);
}
