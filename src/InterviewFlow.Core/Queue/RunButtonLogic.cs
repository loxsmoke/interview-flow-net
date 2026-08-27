namespace InterviewFlow.Core.Queue;

/// <summary>What clicking the Run button should do in its current state.</summary>
public enum RunButtonAction
{
    /// <summary>Enqueue (runs immediately — the slot is free).</summary>
    Run,

    /// <summary>Enqueue behind whatever is running.</summary>
    RunLater,

    /// <summary>Remove this section's waiting item.</summary>
    Unqueue,

    /// <summary>Cancel this section's running item.</summary>
    Stop,

    /// <summary>Cancellation already requested — button disabled.</summary>
    Stopping,
}

/// <summary>Resolved label + action + target for the split Run button.</summary>
public sealed record RunButtonState(string Label, RunButtonAction Action, string? QueueItemId)
{
    public bool IsEnabled => Action != RunButtonAction.Stopping;
}

/// <summary>
/// The Run-button state machine (docs/03-ui-spec.md §3.4, docs/07 §7.5):
/// Run AI → Run AI Later (other work active) → Don't Run AI (this queued) →
/// Stop AI / Stopping AI... (this running/canceling).
/// </summary>
public static class RunButtonLogic
{
    public static RunButtonState Resolve(QueueSnapshot queue, string stateId, string sectionKey)
    {
        if (queue.Running is { } running
            && running.StateId == stateId && running.SectionKey == sectionKey)
        {
            return running.Status == QueueStatus.Canceling
                ? new RunButtonState("Stopping AI...", RunButtonAction.Stopping, running.Id)
                : new RunButtonState("Stop AI", RunButtonAction.Stop, running.Id);
        }

        var queued = queue.Queued.FirstOrDefault(i => i.StateId == stateId && i.SectionKey == sectionKey);
        if (queued is not null)
            return new RunButtonState("Don't Run AI", RunButtonAction.Unqueue, queued.Id);

        var somethingActive = queue.Running is not null || queue.Queued.Count > 0;
        return somethingActive
            ? new RunButtonState("Run AI Later", RunButtonAction.RunLater, null)
            : new RunButtonState("Run AI", RunButtonAction.Run, null);
    }
}
