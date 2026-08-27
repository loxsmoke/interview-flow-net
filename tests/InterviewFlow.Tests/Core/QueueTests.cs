using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Queue;

namespace InterviewFlow.Tests.Core;

public sealed class QueueManagerTests
{
    private readonly QueueManager _queue = new();

    [Fact]
    public void First_enqueue_promotes_immediately()
    {
        var item = _queue.Enqueue("s1", "research", "Company Research");
        Assert.Equal(QueueStatus.Running, item.Status);
        Assert.NotEmpty(item.RunningAt);
        var snapshot = _queue.Snapshot();
        Assert.Equal(item.Id, snapshot.Running!.Id);
        Assert.Empty(snapshot.Queued);
    }

    [Fact]
    public void Waiting_items_sort_by_fixed_section_order()
    {
        _queue.Enqueue("s1", "research", "R");        // takes the slot
        _queue.Enqueue("s1", "salary", "Sa");
        _queue.Enqueue("s1", "custom:abc123", "Custom");
        _queue.Enqueue("s1", "interview_intel", "I");
        _queue.Enqueue("s1", "stories", "St");

        var queued = _queue.Snapshot().Queued.Select(i => i.SectionKey).ToList();
        Assert.Equal(["interview_intel", "stories", "salary", "custom:abc123"], queued);
    }

    [Fact]
    public void Enqueue_dedupes_active_state_section_pairs()
    {
        var first = _queue.Enqueue("s1", "research", "R");
        var again = _queue.Enqueue("s1", "research", "R");
        Assert.Equal(first.Id, again.Id);

        // A different workflow's research is a separate item.
        var other = _queue.Enqueue("s2", "research", "R");
        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public void Completion_promotes_the_next_item()
    {
        var a = _queue.Enqueue("s1", "research", "R");
        _queue.Enqueue("s1", "pitch", "P");

        _queue.MarkCompleted(a.Id);
        var snapshot = _queue.Snapshot();
        Assert.Equal("pitch", snapshot.Running!.SectionKey);
        Assert.Empty(snapshot.Queued);
    }

    [Fact]
    public void Failure_records_and_reenqueue_clears_it()
    {
        var a = _queue.Enqueue("s1", "research", "R");
        _queue.MarkFailed(a.Id, "boom", "detail");

        var snapshot = _queue.Snapshot();
        Assert.Null(snapshot.Running);
        var failed = Assert.Single(snapshot.Failed);
        Assert.Equal("boom", failed.Error);
        Assert.Equal("detail", failed.ErrorDetail);

        _queue.Enqueue("s1", "research", "R");
        Assert.Empty(_queue.Snapshot().Failed); // failure cleared on retry
    }

    [Fact]
    public void Cancel_running_sets_canceling_and_cancels_token()
    {
        var a = _queue.Enqueue("s1", "research", "R");
        var canceled = _queue.Cancel(a.Id);
        Assert.Equal(QueueStatus.Canceling, canceled.Status);
        Assert.True(a.CancellationToken.IsCancellationRequested);
        // Still occupies the slot until the runner acknowledges with MarkCanceled.
        Assert.NotNull(_queue.Snapshot().Running);

        _queue.MarkCanceled(a.Id);
        Assert.Null(_queue.Snapshot().Running);
    }

    [Fact]
    public void Unqueue_removes_only_waiting_items()
    {
        var a = _queue.Enqueue("s1", "research", "R");
        var b = _queue.Enqueue("s1", "pitch", "P");

        _queue.Unqueue(b.Id);
        Assert.Empty(_queue.Snapshot().Queued);
        Assert.Throws<KeyNotFoundException>(() => _queue.Unqueue(a.Id)); // running, not waiting
    }

    [Fact]
    public void Cleanup_state_drops_everything_for_that_workflow()
    {
        var run = _queue.Enqueue("s1", "research", "R");
        _queue.Enqueue("s1", "pitch", "P");
        _queue.Enqueue("s2", "stories", "St");

        _queue.CleanupState("s1");
        var snapshot = _queue.Snapshot();
        Assert.True(run.CancellationToken.IsCancellationRequested);
        Assert.Equal(QueueStatus.Canceling, snapshot.Running!.Status);
        var remaining = Assert.Single(snapshot.Queued);
        Assert.Equal("s2", remaining.StateId);
    }

    [Fact]
    public void Subscribe_replays_history_then_streams_live()
    {
        var a = _queue.Enqueue("s1", "research", "R");
        _queue.PublishEvent(a.Id, new SendEvent("system", "sys"));
        _queue.PublishEvent(a.Id, new ReceiveEvent("Hel"));

        var live = new List<AgentEvent>();
        var (existing, subscription) = _queue.Subscribe(a.Id, live.Add);
        Assert.Equal(2, existing.Count);

        _queue.PublishEvent(a.Id, new ReceiveEvent("lo"));
        Assert.Single(live);
        subscription.Dispose();
        _queue.PublishEvent(a.Id, new ReceiveEvent("!"));
        Assert.Single(live); // unsubscribed
    }

    [Fact]
    public void Changed_event_carries_snapshots()
    {
        var snapshots = new List<QueueSnapshot>();
        _queue.Changed += snapshots.Add;
        var a = _queue.Enqueue("s1", "research", "R");
        _queue.MarkCompleted(a.Id);
        Assert.Equal(2, snapshots.Count);
        Assert.NotNull(snapshots[0].Running);
        Assert.Null(snapshots[1].Running);
    }
}

public sealed class QueueWorkerTests
{
    private static async Task WaitForDrainAsync(QueueWorker worker)
    {
        var loop = worker.CurrentLoop;
        if (loop is not null)
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Runs_items_to_completion_and_replays_events()
    {
        var queue = new QueueManager();
        var worker = new QueueWorker(queue, (_, _) => Stream(
            new SendEvent("system", "s"),
            new ReceiveEvent("hi"),
            new CompleteEvent("hi", 0.1, "m", 5, [])));

        var item = queue.Enqueue("s1", "research", "R");
        // Subscribe while active — completed items are unreachable, matching the original.
        var live = new List<AgentEvent>();
        var (existing, sub) = queue.Subscribe(item.Id, e => { lock (live) live.Add(e); });
        Assert.Empty(existing);

        worker.EnsureRunning();
        await WaitForDrainAsync(worker);
        sub.Dispose();

        Assert.Null(queue.Snapshot().Running);
        // send, receive, complete + the queue_status terminal marker.
        Assert.Equal(4, live.Count);
        Assert.IsType<QueueStatusEvent>(live[^1]);
        Assert.Equal(QueueStatus.Completed, item.Status);
        Assert.Throws<KeyNotFoundException>(() => queue.Subscribe(item.Id, _ => { }));
    }

    [Fact]
    public async Task Error_event_fails_the_item()
    {
        var queue = new QueueManager();
        var worker = new QueueWorker(queue, (_, _) => Stream(
            new ErrorEvent("provider exploded", "stack")));

        var item = queue.Enqueue("s1", "research", "R");
        worker.EnsureRunning();
        await WaitForDrainAsync(worker);

        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal("provider exploded", item.Error);
        var failed = Assert.Single(queue.Snapshot().Failed);
        Assert.Equal(item.Id, failed.Id);
    }

    [Fact]
    public async Task Stream_ending_without_terminal_event_fails()
    {
        var queue = new QueueManager();
        var worker = new QueueWorker(queue, (_, _) => Stream(new ReceiveEvent("partial")));

        var item = queue.Enqueue("s1", "research", "R");
        worker.EnsureRunning();
        await WaitForDrainAsync(worker);

        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal("Queued agent ended before completion.", item.Error);
    }

    [Fact]
    public async Task Cancellation_mid_stream_marks_canceled_and_promotes_next()
    {
        var queue = new QueueManager();
        var started = new TaskCompletionSource();
        var worker = new QueueWorker(queue, (item, ct) => item.SectionKey == "research"
            ? SlowStream(started, ct)
            : Stream(new CompleteEvent("ok", 0, "m", 1, [])));

        var research = queue.Enqueue("s1", "research", "R");
        queue.Enqueue("s1", "pitch", "P");
        worker.EnsureRunning();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Cancel(research.Id);
        await WaitForDrainAsync(worker);

        Assert.Equal(QueueStatus.Canceled, research.Status);
        Assert.Null(queue.Snapshot().Running); // pitch ran to completion afterwards
        Assert.Empty(queue.Snapshot().Queued);
    }

    [Fact]
    public async Task Factory_exception_fails_cleanly()
    {
        var queue = new QueueManager();
        var worker = new QueueWorker(queue, (_, _) => throw new InvalidOperationException("no such section"));

        var item = queue.Enqueue("s1", "research", "R");
        worker.EnsureRunning();
        await WaitForDrainAsync(worker);

        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal("no such section", item.Error);
    }

    private static async IAsyncEnumerable<AgentEvent> Stream(params AgentEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> SlowStream(
        TaskCompletionSource started,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ReceiveEvent("working…");
        started.TrySetResult();
        await Task.Delay(TimeSpan.FromSeconds(30), ct); // canceled long before
        yield return new CompleteEvent("never", 0, "m", 1, []);
    }
}

public sealed class RunButtonLogicTests
{
    private static QueueItemSnapshot Item(string stateId, string section, string status) =>
        new("id-" + section, stateId, section, section, status, "", "", "", "", "");

    [Fact]
    public void Idle_queue_offers_run() =>
        Assert.Equal(("Run AI", RunButtonAction.Run),
            Select(RunButtonLogic.Resolve(new QueueSnapshot(null, [], []), "s1", "research")));

    [Fact]
    public void Other_work_running_offers_run_later()
    {
        var snapshot = new QueueSnapshot(Item("s1", "pitch", QueueStatus.Running), [], []);
        Assert.Equal(("Run AI Later", RunButtonAction.RunLater),
            Select(RunButtonLogic.Resolve(snapshot, "s1", "research")));
    }

    [Fact]
    public void Own_queued_item_offers_unqueue()
    {
        var snapshot = new QueueSnapshot(
            Item("s1", "pitch", QueueStatus.Running),
            [Item("s1", "research", QueueStatus.Queued)], []);
        var state = RunButtonLogic.Resolve(snapshot, "s1", "research");
        Assert.Equal(("Don't Run AI", RunButtonAction.Unqueue), Select(state));
        Assert.Equal("id-research", state.QueueItemId);
    }

    [Fact]
    public void Own_running_item_offers_stop_then_stopping()
    {
        var running = new QueueSnapshot(Item("s1", "research", QueueStatus.Running), [], []);
        Assert.Equal(("Stop AI", RunButtonAction.Stop), Select(RunButtonLogic.Resolve(running, "s1", "research")));

        var canceling = new QueueSnapshot(Item("s1", "research", QueueStatus.Canceling), [], []);
        var state = RunButtonLogic.Resolve(canceling, "s1", "research");
        Assert.Equal(("Stopping AI...", RunButtonAction.Stopping), Select(state));
        Assert.False(state.IsEnabled);
    }

    [Fact]
    public void Another_workflows_run_still_means_later()
    {
        var snapshot = new QueueSnapshot(Item("s2", "research", QueueStatus.Running), [], []);
        Assert.Equal(("Run AI Later", RunButtonAction.RunLater),
            Select(RunButtonLogic.Resolve(snapshot, "s1", "research")));
    }

    private static (string, RunButtonAction) Select(RunButtonState s) => (s.Label, s.Action);
}

public sealed class LiveTraceViewModelTests
{
    [Fact]
    public void Applies_the_event_vocabulary()
    {
        var vm = new InterviewFlow.App.ViewModels.LiveTraceViewModel();
        vm.ApplyEvent(new SendEvent("system", "SYS"));
        vm.ApplyEvent(new SendEvent("user", "USER"));
        vm.ApplyEvent(new ReceiveEvent("Hel"));
        vm.ApplyEvent(new ReceiveEvent("lo"));
        vm.ApplyEvent(new ToolUseEvent("WebSearch", Query: "acme reviews"));
        vm.ApplyEvent(new ToolUseEvent("WebFetch", Url: "https://x", Title: "X site"));

        Assert.Equal("SYS", vm.SystemPrompt);
        Assert.Equal("USER", vm.UserPrompt);
        Assert.Equal("Hello", vm.ResponseText);
        Assert.Equal(2, vm.WebActivity.Count);
        Assert.Equal("🔍", vm.WebActivity[0].Icon);
        Assert.Equal("X site", vm.WebActivity[1].Text);
        Assert.False(vm.HasRateLimitCountdown);
    }

    [Fact]
    public void Rate_limit_countdown_shows_then_clears_on_receive()
    {
        var vm = new InterviewFlow.App.ViewModels.LiveTraceViewModel();
        vm.ApplyEvent(new RateLimitRetryEvent(45));
        Assert.True(vm.HasRateLimitCountdown);
        Assert.Equal(45, vm.RateLimitRemaining);

        vm.ApplyEvent(new ReceiveEvent("resumed"));
        Assert.False(vm.HasRateLimitCountdown);
    }

    [Fact]
    public void Reset_event_clears_accumulated_response()
    {
        var vm = new InterviewFlow.App.ViewModels.LiveTraceViewModel();
        vm.ApplyEvent(new ReceiveEvent("partial answer"));
        vm.ApplyEvent(new RateLimitResetEvent());
        Assert.Equal("", vm.ResponseText);

        vm.ApplyEvent(new ReceiveEvent("fresh"));
        Assert.Equal("fresh", vm.ResponseText);
    }
}
