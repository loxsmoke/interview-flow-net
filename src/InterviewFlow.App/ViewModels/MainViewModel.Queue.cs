using Avalonia.Threading;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Queue;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// Queue wiring for the shell: owns the single-slot QueueManager + worker,
/// resolves section streams (research is live; unported sections fail with a
/// clear message), persists results, and drives the sidebar badges
/// (running spinner-state / queued ⌛ / failed !) from queue snapshots.
/// </summary>
public sealed partial class MainViewModel
{
    public QueueManager Queue { get; } = new();

    private QueueWorker? _worker;

    private void InitializeQueue()
    {
        _worker = new QueueWorker(Queue, SectionStream);
        Queue.Changed += snapshot =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                ApplyQueueSnapshot(snapshot);
            else
                Dispatcher.UIThread.Post(() => ApplyQueueSnapshot(snapshot));
        };
    }

    /// <summary>Enqueue a section for the current workflow (Run AI / Run AI Later).</summary>
    public QueueItem? EnqueueSection(string sectionKey, string title = "")
    {
        if (CurrentState is null)
            return null;
        var resolvedTitle = title.Length > 0
            ? title
            : QueueManager.SectionTitles.GetValueOrDefault(sectionKey, sectionKey);
        var item = Queue.Enqueue(CurrentState.Id, sectionKey, resolvedTitle);
        _worker?.EnsureRunning();
        return item;
    }

    public void UnqueueItem(string queueId)
    {
        try
        {
            Queue.Unqueue(queueId);
        }
        catch (KeyNotFoundException)
        {
            // Raced with promotion/settlement — snapshot update will correct the UI.
        }
    }

    public void CancelItem(string queueId)
    {
        try
        {
            Queue.Cancel(queueId);
        }
        catch (KeyNotFoundException)
        {
        }
    }

    private void ApplyQueueSnapshot(QueueSnapshot snapshot)
    {
        var stateId = CurrentState?.Id;
        foreach (var step in Steps)
        {
            step.IsRunning = snapshot.Running is { } r
                && r.StateId == stateId && r.SectionKey == step.Key
                && r.Status is QueueStatus.Running or QueueStatus.Canceling;
            step.IsQueued = stateId is not null
                && snapshot.Queued.Any(i => i.StateId == stateId && i.SectionKey == step.Key);
            step.IsFailed = stateId is not null
                && snapshot.Failed.Any(i => i.StateId == stateId && i.SectionKey == step.Key);
        }
    }

    private Core.Agents.SectionRunner? _runner;

    /// <summary>
    /// Section → agent stream: SectionRunner does prompt/stream/persist; this
    /// wrapper refreshes the shell's state copy on the UI thread after a save.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> SectionStream(
        QueueItemSnapshot item,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _runner ??= new Core.Agents.SectionRunner(Config, Store, ActionStore);
        await foreach (var evt in _runner.Stream(item.StateId, item.SectionKey, ct))
        {
            if (evt is CompleteEvent)
            {
                var stateId = item.StateId;
                Dispatcher.UIThread.Post(() =>
                {
                    if (CurrentState?.Id == stateId)
                    {
                        CurrentState = Store.LoadState(stateId);
                        NotifyStateChanged();
                    }
                });
            }

            yield return evt;
        }
    }
}
