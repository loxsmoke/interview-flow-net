using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Queue;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// Shared "Run AI" component for agent screens: the run-button state machine,
/// the queue dropdown, the live trace (with replay when re-opening a screen
/// mid-run), and error surfacing from failed queue items. Pages subscribe to
/// Completed to refresh their body. Dispose on page swap.
///
/// The dropdown is a *pending* selection (index.html:2270-2385): opening seeds
/// the checkboxes from the live queue, ticking one changes nothing, and only
/// Apply commits the diff (enqueue newly ticked / unqueue newly cleared).
/// </summary>
public sealed partial class AgentRunViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;
    private readonly Action<QueueSnapshot> _queueHandler;
    private IDisposable? _itemSubscription;
    private string _subscribedItemId = "";

    public string SectionKey { get; }
    public LiveTraceViewModel Trace { get; } = new();
    public ObservableCollection<QueueDropdownItemViewModel> DropdownItems { get; } = [];

    [ObservableProperty] private RunButtonState _runState = new("Run AI", RunButtonAction.Run, null);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowResult))]
    private bool _isRunningHere;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowResult))]
    private string _errorMessage = "";
    [ObservableProperty] private string _errorDetail = "";

    /// <summary>Label of the dropdown's left button — mirrors the original's flip.</summary>
    public string SelectAllLabel => AllSelected ? "Clear all" : "Select all";

    public bool HasError => ErrorMessage.Length > 0 && !IsRunningHere;
    public bool ShowResult => !IsRunningHere && !HasError;

    /// <summary>Fires on the UI thread when this section's run completes.</summary>
    public event Action? Completed;

    public AgentRunViewModel(MainViewModel shell, string sectionKey)
    {
        _shell = shell;
        SectionKey = sectionKey;
        foreach (var (key, _) in QueueManager.SectionOrder.OrderBy(kv => kv.Value))
            DropdownItems.Add(new QueueDropdownItemViewModel(
                key, QueueManager.SectionTitles[key], OnSelectionChanged));

        _queueHandler = snapshot =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                Apply(snapshot);
            else
                Dispatcher.UIThread.Post(() => Apply(snapshot));
        };
        _shell.Queue.Changed += _queueHandler;
        Apply(_shell.Queue.Snapshot());
    }

    private string StateId => _shell.CurrentState?.Id ?? "";

    private void Apply(QueueSnapshot snapshot)
    {
        var stateId = StateId;
        RunState = RunButtonLogic.Resolve(snapshot, stateId, SectionKey);
        IsRunningHere = snapshot.Running is { } r
            && r.StateId == stateId && r.SectionKey == SectionKey;

        var failed = snapshot.Failed.FirstOrDefault(i => i.StateId == stateId && i.SectionKey == SectionKey);
        ErrorMessage = failed?.Error ?? "";
        ErrorDetail = failed?.ErrorDetail ?? "";

        // Only the running lock tracks the queue live; checkbox state is the
        // user's pending selection, seeded on open and committed by Apply.
        foreach (var item in DropdownItems)
        {
            item.IsRunning = snapshot.Running is { } run
                && run.StateId == stateId && run.SectionKey == item.Key;
            item.IsEnabled = !item.IsRunning;
        }

        OnPropertyChanged(nameof(SelectAllLabel));
        ManageSubscription(snapshot);
    }

    private void ManageSubscription(QueueSnapshot snapshot)
    {
        var running = snapshot.Running;
        var shouldSubscribe = running is not null
            && running.StateId == StateId && running.SectionKey == SectionKey;

        if (!shouldSubscribe)
        {
            // Keep the trace visible until the next run starts; just stop listening.
            _itemSubscription?.Dispose();
            _itemSubscription = null;
            _subscribedItemId = "";
            return;
        }

        if (running!.Id == _subscribedItemId)
            return;

        _itemSubscription?.Dispose();
        _subscribedItemId = running.Id;
        Trace.Reset();
        try
        {
            var (existing, subscription) = _shell.Queue.Subscribe(running.Id, evt =>
                Dispatcher.UIThread.Post(() => OnItemEvent(evt)));
            _itemSubscription = subscription;
            foreach (var evt in existing)
                OnItemEvent(evt);
        }
        catch (KeyNotFoundException)
        {
            // The item settled between snapshot and subscribe — snapshot update follows.
            _subscribedItemId = "";
        }
    }

    private void OnItemEvent(AgentEvent evt)
    {
        Trace.ApplyEvent(evt);
        if (evt is CompleteEvent)
            Completed?.Invoke();
    }

    [RelayCommand]
    private void Run()
    {
        switch (RunState.Action)
        {
            case RunButtonAction.Run or RunButtonAction.RunLater:
                _shell.EnqueueSection(SectionKey);
                break;
            case RunButtonAction.Unqueue when RunState.QueueItemId is { } queuedId:
                _shell.UnqueueItem(queuedId);
                break;
            case RunButtonAction.Stop when RunState.QueueItemId is { } runningId:
                _shell.CancelItem(runningId);
                break;
        }
    }

    /// <summary>
    /// Seeds the checkboxes from the live queue (running + waiting) — call when
    /// the dropdown opens, so a re-open never shows stale ticks.
    /// </summary>
    public void SeedSelection()
    {
        var snapshot = _shell.Queue.Snapshot();
        var stateId = StateId;
        foreach (var item in DropdownItems)
        {
            item.IsRunning = snapshot.Running is { } run
                && run.StateId == stateId && run.SectionKey == item.Key;
            item.IsEnabled = !item.IsRunning;
            item.IsChecked = item.IsRunning
                || snapshot.Queued.Any(i => i.StateId == stateId && i.SectionKey == item.Key);
        }

        OnPropertyChanged(nameof(SelectAllLabel));
    }

    /// <summary>Select all / Clear all — the running section keeps its lock.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var select = !AllSelected;
        foreach (var item in DropdownItems.Where(i => !i.IsRunning))
            item.IsChecked = select;

        OnPropertyChanged(nameof(SelectAllLabel));
    }

    /// <summary>
    /// Commits the pending selection: newly ticked sections are enqueued (this
    /// is the only path that starts AI work from the dropdown), cleared ones are
    /// unqueued. The running section is left alone.
    /// </summary>
    public void ApplySelection()
    {
        var stateId = StateId;
        foreach (var item in DropdownItems)
        {
            if (item.IsRunning)
                continue;

            // Re-snapshot per item: enqueueing promotes and reshapes the queue.
            var queued = _shell.Queue.Snapshot().Queued
                .FirstOrDefault(i => i.StateId == stateId && i.SectionKey == item.Key);
            if (item.IsChecked && queued is null)
                _shell.EnqueueSection(item.Key);
            else if (!item.IsChecked && queued is not null)
                _shell.UnqueueItem(queued.Id);
        }
    }

    private bool AllSelected
    {
        get
        {
            var selectable = DropdownItems.Where(i => !i.IsRunning).ToList();
            return selectable.Count > 0 && selectable.All(i => i.IsChecked);
        }
    }

    private void OnSelectionChanged(QueueDropdownItemViewModel _)
        => OnPropertyChanged(nameof(SelectAllLabel));

    public void Dispose()
    {
        _shell.Queue.Changed -= _queueHandler;
        _itemSubscription?.Dispose();
        _itemSubscription = null;
    }
}

/// <summary>
/// One row of the queue dropdown (checkbox list of the 8 sections). Ticking is
/// local state only — <see cref="AgentRunViewModel.ApplySelection"/> commits it.
/// </summary>
public sealed partial class QueueDropdownItemViewModel(
    string key, string title, Action<QueueDropdownItemViewModel> selectionChanged) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isRunning;

    partial void OnIsCheckedChanged(bool value) => selectionChanged(this);
}
