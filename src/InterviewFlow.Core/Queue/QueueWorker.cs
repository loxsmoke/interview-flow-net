using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Logging;

namespace InterviewFlow.Core.Queue;

/// <summary>
/// Drives the single queue slot (port of main.py's _queue_worker_loop +
/// _run_queue_item): pulls the running item, streams its section, republishes
/// every event into the item's history, and settles the item on the terminal
/// event — error → failed, complete → completed, cooperative cancellation →
/// canceled. A stream that ends without a terminal event is a failure.
/// </summary>
public sealed class QueueWorker(
    QueueManager queue,
    Func<QueueItemSnapshot, CancellationToken, IAsyncEnumerable<AgentEvent>> sectionStreamFactory)
{
    private readonly Lock _sync = new();
    private Task? _task;

    /// <summary>Starts the worker if it isn't already draining the queue.</summary>
    public void EnsureRunning()
    {
        lock (_sync)
        {
            if (_task is null || _task.IsCompleted)
                _task = Task.Run(LoopAsync);
        }
    }

    /// <summary>Exposed for tests: completes when the queue drains.</summary>
    public Task? CurrentLoop
    {
        get { lock (_sync) return _task; }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            var item = queue.RunningItem();
            if (item is null)
                return;
            try
            {
                await RunItemAsync(item);
            }
            catch (Exception exc)
            {
                DiagnosticLog.Error("queue", "queued agent worker error", exc);
                try
                {
                    queue.MarkFailed(item.Id,
                        "Queued agent encountered an error. Please try again.",
                        exc.ToString());
                }
                catch (KeyNotFoundException)
                {
                    // Already settled elsewhere.
                }
            }
        }
    }

    private async Task RunItemAsync(QueueItem item)
    {
        IAsyncEnumerable<AgentEvent> stream;
        try
        {
            stream = sectionStreamFactory(item.Dump(), item.CancellationToken);
        }
        catch (Exception exc)
        {
            queue.MarkFailed(item.Id, exc.Message);
            return;
        }

        try
        {
            await foreach (var evt in stream.WithCancellation(item.CancellationToken))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    queue.MarkCanceled(item.Id);
                    return;
                }

                queue.PublishEvent(item.Id, evt);

                switch (evt)
                {
                    case ErrorEvent error:
                        queue.MarkFailed(item.Id,
                            error.Message.Length > 0
                                ? error.Message
                                : "Queued agent encountered an error. Please try again.",
                            error.Detail);
                        return;
                    case CompleteEvent:
                        queue.MarkCompleted(item.Id);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested)
        {
            queue.MarkCanceled(item.Id);
            return;
        }

        queue.MarkFailed(item.Id, "Queued agent ended before completion.");
    }
}
