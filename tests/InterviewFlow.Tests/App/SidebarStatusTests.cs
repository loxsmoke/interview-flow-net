using InterviewFlow.App.ViewModels;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Sidebar run status (docs/03-ui-spec.md §3.1). The queue already drove
/// IsRunning/IsQueued/IsFailed, but nothing rendered them — these pin the
/// derived state the tile binds to: spinner replaces the glyph while running,
/// and the tooltip names the state like the original's title/aria-label.
/// </summary>
public sealed class SidebarStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-sidebar-" + Guid.NewGuid().ToString("N")[..8]);

    private (MainViewModel Shell, InterviewState State) NewShell()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, $"INTERVIEW_DATA_DIR={Path.Combine(_dir, "data")}\n");
        var shell = new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
        var state = new InterviewState { CompanyName = "Acme" };
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);
        return (shell, state);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static StepItemViewModel Step(MainViewModel shell, string key) =>
        shell.Steps.First(s => s.Key == key);

    [Fact]
    public void Running_section_swaps_the_glyph_for_the_spinner()
    {
        var (shell, state) = NewShell();
        var step = Step(shell, "research");
        Assert.True(step.ShowGlyph);
        Assert.Equal("", step.StatusLabel);
        Assert.False(step.HasStatus);

        // Enqueuing with an idle queue promotes straight to running.
        shell.Queue.Enqueue(state.Id, "research", "Research");

        Assert.True(step.IsRunning);
        Assert.False(step.ShowGlyph); // the spinner takes the tile
        Assert.Equal("Running", step.StatusLabel);
        Assert.True(step.HasStatus);
    }

    [Fact]
    public void Waiting_section_reads_as_queued_while_another_runs()
    {
        var (shell, state) = NewShell();
        shell.Queue.Enqueue(state.Id, "research", "Research");
        shell.Queue.Enqueue(state.Id, "pitch", "Pitch");

        var queued = Step(shell, "pitch");
        Assert.True(queued.IsQueued);
        Assert.False(queued.IsRunning);
        Assert.True(queued.ShowGlyph); // queued keeps its icon + the ⌛ badge
        Assert.Equal("Queued", queued.StatusLabel);
    }

    [Fact]
    public void Failure_shows_the_bang_glyph_and_outranks_the_other_states()
    {
        var (shell, state) = NewShell();
        var item = shell.Queue.Enqueue(state.Id, "research", "Research");
        shell.Queue.MarkFailed(item.Id, "provider exploded");

        var step = Step(shell, "research");
        Assert.False(step.IsRunning);
        Assert.True(step.IsFailed);
        Assert.Equal("!", step.TileGlyph);
        Assert.Equal("Failed", step.StatusLabel);
    }

    [Fact]
    public void Another_workflows_run_does_not_light_up_this_sidebar()
    {
        var (shell, _) = NewShell();
        shell.Queue.Enqueue("some-other-state-id", "research", "Research");

        var step = Step(shell, "research");
        Assert.False(step.IsRunning);
        Assert.True(step.ShowGlyph);
        Assert.Equal("", step.StatusLabel);
    }
}
