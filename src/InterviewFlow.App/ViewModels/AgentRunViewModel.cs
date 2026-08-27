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

    public bool HasError => ErrorMessage.Length > 0 && !IsRunningHere;
    public bool ShowResult => !IsRunningHere && !HasError;

    /// <summary>Fires on the UI thread when this section's run completes.</summary>
    public event Action? Completed;

    public AgentRunViewModel(MainViewModel shell, string sectionKey)
    {
        _shell = shell;
        SectionKey = sectionKey;
        foreach (var (key, _) in QueueManager.SectionOrder.OrderBy(kv => kv.Value))
            DropdownItems.Add(new QueueDropdownItemViewModel(key, QueueManager.SectionTitles[key], Toggle));

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

        foreach (var item in DropdownItems)
        {
            var isRunning = snapshot.Running is { } run
                && run.StateId == stateId && run.SectionKey == item.Key;
            var isQueued = snapshot.Queued.Any(i => i.StateId == stateId && i.SectionKey == item.Key);
            item.IsChecked = isRunning || isQueued;
            item.IsEnabled = !isRunning;
        }

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

    private void Toggle(QueueDropdownItemViewModel item)
    {
        var snapshot = _shell.Queue.Snapshot();
        var queued = snapshot.Queued.FirstOrDefault(i => i.StateId == StateId && i.SectionKey == item.Key);
        if (queued is not null)
            _shell.UnqueueItem(queued.Id);
        else if (!item.IsChecked)
            _shell.EnqueueSection(item.Key);
    }

    public void Dispose()
    {
        _shell.Queue.Changed -= _queueHandler;
        _itemSubscription?.Dispose();
        _itemSubscription = null;
    }
}

/// <summary>One row of the queue dropdown (checkbox list of the 8 sections).</summary>
public sealed partial class QueueDropdownItemViewModel(
    string key, string title, Action<QueueDropdownItemViewModel> toggle) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isEnabled = true;

    [RelayCommand]
    private void Toggle() => toggle(this);
}
