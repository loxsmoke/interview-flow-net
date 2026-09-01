using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterviewFlow.App.ViewModels;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Queue dropdown parity with the original (index.html:2270-2385): ticks are a
/// pending selection that starts nothing, Select all / Clear all only flip the
/// ticks, and Apply commits the diff against the live queue.
/// </summary>
public sealed class QueueDropdownTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-dropdown-" + Guid.NewGuid().ToString("N")[..8]);

    private (MainViewModel Shell, AgentRunViewModel Run) NewRun()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env");
        // No API key: any run the worker does pick up fails instantly, offline.
        File.WriteAllText(envPath, $"INTERVIEW_DATA_DIR={Path.Combine(_dir, "data")}\n");
        var shell = new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
        var state = new InterviewState { CompanyName = "Acme", Position = "Staff Engineer" };
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);
        return (shell, new AgentRunViewModel(shell, "research"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void Ticking_checkboxes_queues_nothing_until_Apply()
    {
        var (shell, run) = NewRun();
        run.SeedSelection();

        foreach (var item in run.DropdownItems.Where(i => i.Key is "pitch" or "salary"))
            item.IsChecked = true;

        var snapshot = shell.Queue.Snapshot();
        Assert.Null(snapshot.Running);
        Assert.Empty(snapshot.Queued);

        run.ApplySelection();

        // The worker may already have promoted (and, keyless, failed) the head
        // item, so accept any resting place — nothing else may be there.
        var after = shell.Queue.Snapshot();
        var committed = after.Queued.Select(i => i.SectionKey)
            .Concat(after.Failed.Select(i => i.SectionKey))
            .Concat(after.Running is { } r ? [r.SectionKey] : Array.Empty<string>())
            .Order()
            .ToList();
        Assert.Equal(["pitch", "salary"], committed);
    }

    [AvaloniaFact]
    public void Select_all_flips_to_Clear_all_and_only_touches_the_ticks()
    {
        var (shell, run) = NewRun();
        run.SeedSelection();
        Assert.Equal("Select all", run.SelectAllLabel);

        run.ToggleSelectAllCommand.Execute(null);
        Assert.All(run.DropdownItems, i => Assert.True(i.IsChecked));
        Assert.Equal("Clear all", run.SelectAllLabel);
        Assert.Empty(shell.Queue.Snapshot().Queued);

        run.ToggleSelectAllCommand.Execute(null);
        Assert.All(run.DropdownItems, i => Assert.False(i.IsChecked));
        Assert.Equal("Select all", run.SelectAllLabel);
        Assert.Empty(shell.Queue.Snapshot().Queued);
    }

    [AvaloniaFact]
    public void Apply_unqueues_sections_whose_tick_was_cleared()
    {
        var (shell, run) = NewRun();
        // Enqueue on the manager directly so no worker drains the slot: pitch
        // promotes to running and salary stays waiting for the whole test.
        var stateId = shell.CurrentState!.Id;
        shell.Queue.Enqueue(stateId, "pitch", "Pitch Builder");
        shell.Queue.Enqueue(stateId, "salary", "Salary Coaching");

        // Re-opening the dropdown shows what is actually queued.
        run.SeedSelection();
        Assert.Equal(
            ["pitch", "salary"],
            run.DropdownItems.Where(i => i.IsChecked).Select(i => i.Key).ToList());

        run.DropdownItems.Single(i => i.Key == "salary").IsChecked = false;
        run.ApplySelection();

        Assert.DoesNotContain(
            shell.Queue.Snapshot().Queued,
            i => i.SectionKey == "salary");
    }

    [AvaloniaFact]
    public void Opening_the_dropdown_inflates_it_and_reseeds_from_the_queue()
    {
        var (shell, run) = NewRun();
        shell.Queue.Enqueue(shell.CurrentState!.Id, "pitch", "Pitch Builder");

        var header = new InterviewFlow.App.Views.AgentRunHeaderView { DataContext = run };
        var window = new Window { Content = header };
        window.Show();
        window.UpdateLayout();

        var caret = header.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is not null);
        var flyout = (Flyout)caret.Flyout!;
        flyout.ShowAt(caret);
        var content = (Control)flyout.Content!;
        content.UpdateLayout();

        // The whole popup template inflates: 8 section rows + the two buttons.
        Assert.Equal(8, content.GetVisualDescendants().OfType<CheckBox>().Count());
        var buttons = content.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Contains(buttons, b => (b.Content as string) == "Apply");
        Assert.Contains(buttons, b => (b.Content as string) == "Select all");
        // Opening seeded the ticks from the queue without touching it. Enqueue
        // promotes immediately, so pitch is the (locked) running section.
        var pitch = run.DropdownItems.Single(i => i.Key == "pitch");
        Assert.True(pitch.IsChecked);
        Assert.True(pitch.IsRunning);
        Assert.False(pitch.IsEnabled);
        Assert.Equal("pitch", shell.Queue.Snapshot().Running?.SectionKey);

        flyout.Hide();
        window.Close();
    }
}
