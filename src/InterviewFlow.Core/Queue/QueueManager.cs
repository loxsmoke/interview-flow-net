using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Core.Queue;

/// <summary>Queue item lifecycle states (queue_manager.py's QueueStatus literals).</summary>
public static class QueueStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Canceling = "canceling";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
    public const string Completed = "completed";
}

/// <summary>
/// One queued/running AI section job. Holds the full event history so a screen
/// (re)opened mid-run can replay everything the job emitted so far, and the
/// CancellationTokenSource that cooperative provider streams observe.
/// </summary>
public sealed class QueueItem
{
    public string Id { get; } = ModelDefaults.NewId();
    public required string StateId { get; init; }
    public required string SectionKey { get; init; }
    public required string Title { get; init; }
    public string Status { get; internal set; } = QueueStatus.Queued;
    public string QueuedAt { get; } = ModelDefaults.NowIso();
    public string RunningAt { get; internal set; } = "";
    public string CompletedAt { get; internal set; } = "";
    public string Error { get; internal set; } = "";
    public string ErrorDetail { get; internal set; } = "";

    internal CancellationTokenSource Cts { get; } = new();
    internal List<AgentEvent> Events { get; } = [];
    internal List<Action<AgentEvent>> Subscribers { get; } = [];

    public CancellationToken CancellationToken => Cts.Token;

    public QueueItemSnapshot Dump() => new(
        Id, StateId, SectionKey, Title, Status, QueuedAt, RunningAt, CompletedAt, Error, ErrorDetail);
}

/// <summary>Immutable row shape of the original's item.dump().</summary>
public sealed record QueueItemSnapshot(
    string Id, string StateId, string SectionKey, string Title, string Status,
    string QueuedAt, string RunningAt, string CompletedAt, string Error, string ErrorDetail);

/// <summary>{running, queued[], failed[]} — the queue event payload (07 §7.2).</summary>
public sealed record QueueSnapshot(
    QueueItemSnapshot? Running,
    IReadOnlyList<QueueItemSnapshot> Queued,
    IReadOnlyList<QueueItemSnapshot> Failed);

/// <summary>Queue-internal status event (mark_completed's queue_status publish).</summary>
public sealed record QueueStatusEvent(string Status) : AgentEvent;

/// <summary>
/// In-memory single-slot queue (port of queue_manager.py). State is
/// intentionally lost on exit. Fixed section ordering; custom actions sort last
/// as "custom:{id}". Thread-safe; subscriber/changed callbacks are invoked
/// OUTSIDE the lock (they typically marshal to the UI thread).
/// </summary>
public sealed class QueueManager
{
    /// <summary>Fixed run order (queue_manager.py:15-24).</summary>
    public static readonly IReadOnlyDictionary<string, int> SectionOrder = new Dictionary<string, int>
    {
        ["research"] = 0,
        ["interview_intel"] = 1,
        ["jd_decode"] = 2,
        ["resume_tailor"] = 3,
        ["stories"] = 4,
        ["pitch"] = 5,
        ["concerns"] = 6,
        ["salary"] = 7,
    };

    private const int CustomSectionOrder = 1000;

    /// <summary>Display titles used when enqueueing sections (main.py:760).</summary>
    public static readonly IReadOnlyDictionary<string, string> SectionTitles = new Dictionary<string, string>
    {
        ["research"] = "Company Research",
        ["interview_intel"] = "Interview Intel",
        ["jd_decode"] = "Job Decoder",
        ["resume_tailor"] = "Resume Tailor",
        ["stories"] = "Story Bank",
        ["pitch"] = "Pitch Builder",
        ["concerns"] = "Interviewer Concerns",
        ["salary"] = "Salary Coaching",
    };

    public static (int Order, string Key) SortKey(string sectionKey)
    {
        if (sectionKey.StartsWith("custom:", StringComparison.Ordinal))
            return (CustomSectionOrder, sectionKey);
        return (SectionOrder.GetValueOrDefault(sectionKey, CustomSectionOrder - 1), sectionKey);
    }

    private readonly Lock _sync = new();
    private QueueItem? _running;
    private readonly List<QueueItem> _waiting = [];
    private readonly Dictionary<(string StateId, string SectionKey), QueueItem> _failed = [];

    /// <summary>Fired after any queue-shape change, with the fresh snapshot.</summary>
    public event Action<QueueSnapshot>? Changed;

    public QueueSnapshot Snapshot()
    {
        lock (_sync)
            return SnapshotLocked();
    }

    public QueueItem? RunningItem()
    {
        lock (_sync)
            return _running;
    }

    /// <summary>Enqueue (dedupes on active state+section; clears a prior failure).</summary>
    public QueueItem Enqueue(string stateId, string sectionKey, string title)
    {
        QueueItem item;
        lock (_sync)
        {
            var existing = FindActiveLocked(stateId, sectionKey);
            if (existing is not null)
                return existing;
            item = new QueueItem { StateId = stateId, SectionKey = sectionKey, Title = title };
            _failed.Remove((stateId, sectionKey));
            _waiting.Add(item);
            _waiting.Sort(static (a, b) =>
            {
                var (oa, ka) = SortKey(a.SectionKey);
                var (ob, kb) = SortKey(b.SectionKey);
                return oa != ob ? oa.CompareTo(ob) : string.CompareOrdinal(ka, kb);
            });
            PromoteNextLocked();
        }

        NotifyChanged();
        return item;
    }

    /// <summary>Removes a waiting item ("Don't Run AI"). Throws KeyNotFound if absent.</summary>
    public QueueItem Unqueue(string queueId)
    {
        QueueItem? found;
        lock (_sync)
        {
            found = _waiting.FirstOrDefault(i => i.Id == queueId);
            if (found is null)
                throw new KeyNotFoundException(queueId);
            found.Status = QueueStatus.Canceled;
            found.CompletedAt = ModelDefaults.NowIso();
            _waiting.Remove(found);
        }

        NotifyChanged();
        return found;
    }

    /// <summary>Cancels running (→ canceling + token) or removes waiting.</summary>
    public QueueItem Cancel(string queueId)
    {
        QueueItem item;
        List<Action<AgentEvent>>? publishTo = null;
        lock (_sync)
        {
            if (_running is not null && _running.Id == queueId)
            {
                _running.Status = QueueStatus.Canceling;
                _running.Cts.Cancel();
                publishTo = AppendEventLocked(_running, new CanceledEvent());
                item = _running;
            }
            else
            {
                var waiting = _waiting.FirstOrDefault(i => i.Id == queueId)
                    ?? throw new KeyNotFoundException(queueId);
                waiting.Status = QueueStatus.Canceled;
                waiting.CompletedAt = ModelDefaults.NowIso();
                _waiting.Remove(waiting);
                item = waiting;
            }
        }

        Deliver(publishTo, new CanceledEvent());
        NotifyChanged();
        return item;
    }

    /// <summary>Drops everything belonging to a deleted workflow.</summary>
    public void CleanupState(string stateId) =>
        Cleanup(i => i.StateId == stateId, key => key.StateId == stateId);

    /// <summary>Drops everything for a deleted custom action.</summary>
    public void CleanupCustomAction(string actionId)
    {
        var sectionKey = $"custom:{actionId}";
        Cleanup(i => i.SectionKey == sectionKey, key => key.SectionKey == sectionKey);
    }

    private void Cleanup(
        Func<QueueItem, bool> itemMatch, Func<(string StateId, string SectionKey), bool> failedMatch)
    {
        bool changed;
        lock (_sync)
        {
            changed = false;
            if (_running is not null && itemMatch(_running))
            {
                _running.Status = QueueStatus.Canceling;
                _running.Cts.Cancel();
                changed = true;
            }

            var removed = _waiting.RemoveAll(i => itemMatch(i));
            changed |= removed > 0;
            foreach (var key in _failed.Keys.Where(failedMatch).ToList())
            {
                _failed.Remove(key);
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    public QueueItem MarkCompleted(string queueId) =>
        FinishRunning(queueId, QueueStatus.Completed, new QueueStatusEvent(QueueStatus.Completed));

    public QueueItem MarkCanceled(string queueId) =>
        FinishRunning(queueId, QueueStatus.Canceled, new CanceledEvent());

    public QueueItem MarkFailed(string queueId, string error, string detail = "")
    {
        var item = FinishRunning(queueId, QueueStatus.Failed, new ErrorEvent(error, detail), error, detail);
        return item;
    }

    private QueueItem FinishRunning(
        string queueId, string status, AgentEvent finalEvent, string error = "", string detail = "")
    {
        QueueItem item;
        List<Action<AgentEvent>>? publishTo;
        lock (_sync)
        {
            if (_running is null || _running.Id != queueId)
                throw new KeyNotFoundException(queueId);
            item = _running;
            item.Status = status;
            item.CompletedAt = ModelDefaults.NowIso();
            item.Error = error;
            item.ErrorDetail = detail;
            publishTo = AppendEventLocked(item, finalEvent);
            if (status == QueueStatus.Failed)
                _failed[(item.StateId, item.SectionKey)] = item;
            _running = null;
            PromoteNextLocked();
        }

        Deliver(publishTo, finalEvent);
        NotifyChanged();
        return item;
    }

    /// <summary>Appends to the item's history and fans out to live subscribers.</summary>
    public void PublishEvent(string queueId, AgentEvent evt)
    {
        List<Action<AgentEvent>>? publishTo;
        lock (_sync)
        {
            var item = FindByIdLocked(queueId);
            if (item is null)
                return;
            publishTo = AppendEventLocked(item, evt);
        }

        Deliver(publishTo, evt);
    }

    /// <summary>
    /// Subscribe to an item's events: returns the history so far (replay) and a
    /// disposable live subscription. Callbacks run on the publisher's thread —
    /// UI subscribers marshal to the dispatcher themselves.
    /// </summary>
    public (IReadOnlyList<AgentEvent> Existing, IDisposable Subscription) Subscribe(
        string queueId, Action<AgentEvent> onEvent)
    {
        lock (_sync)
        {
            var item = FindByIdLocked(queueId) ?? throw new KeyNotFoundException(queueId);
            item.Subscribers.Add(onEvent);
            return (item.Events.ToList(), new Unsubscriber(this, item, onEvent));
        }
    }

    private sealed class Unsubscriber(QueueManager owner, QueueItem item, Action<AgentEvent> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sync)
                item.Subscribers.Remove(handler);
        }
    }

    private QueueSnapshot SnapshotLocked() => new(
        _running?.Dump(),
        _waiting.Select(i => i.Dump()).ToList(),
        _failed.Values.Select(i => i.Dump()).ToList());

    private QueueItem? FindActiveLocked(string stateId, string sectionKey)
    {
        if (_running is not null && _running.StateId == stateId && _running.SectionKey == sectionKey)
            return _running;
        return _waiting.FirstOrDefault(i => i.StateId == stateId && i.SectionKey == sectionKey);
    }

    private void PromoteNextLocked()
    {
        if (_running is not null || _waiting.Count == 0)
            return;
        _running = _waiting[0];
        _waiting.RemoveAt(0);
        _running.Status = QueueStatus.Running;
        _running.RunningAt = ModelDefaults.NowIso();
    }

    private QueueItem? FindByIdLocked(string queueId)
    {
        if (_running?.Id == queueId)
            return _running;
        return _waiting.FirstOrDefault(i => i.Id == queueId)
            ?? _failed.Values.FirstOrDefault(i => i.Id == queueId);
    }

    private static List<Action<AgentEvent>> AppendEventLocked(QueueItem item, AgentEvent evt)
    {
        item.Events.Add(evt);
        return item.Subscribers.ToList();
    }

    private static void Deliver(List<Action<AgentEvent>>? subscribers, AgentEvent evt)
    {
        if (subscribers is null)
            return;
        foreach (var s in subscribers)
        {
            try
            {
                s(evt);
            }
            catch
            {
                // A broken subscriber must not take down the queue.
            }
        }
    }

    private void NotifyChanged()
    {
        var snapshot = Snapshot();
        Changed?.Invoke(snapshot);
    }
}
