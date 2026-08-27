using InterviewFlow.App.ViewModels;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Headless shell-logic tests (no Avalonia UI instantiated): clone naming must
/// match main.py:1869 exactly, and workflow lifecycle must keep the sidebar
/// state consistent.
/// </summary>
public sealed class ShellLogicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-shell-" + Guid.NewGuid().ToString("N")[..8]);

    private MainViewModel NewShell()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, $"INTERVIEW_DATA_DIR={Path.Combine(_dir, "data")}\n");
        // Note: a real INTERVIEW_DATA_DIR process env var would override the file;
        // CI/test runs don't set it.
        return new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Clone_names_follow_the_original_copy_suffix_scheme()
    {
        var shell = NewShell();
        var source = new InterviewState { CompanyName = "Acme", Position = "Staff Engineer" };
        shell.Store.SaveState(source);

        var c1 = shell.CloneWorkflow(source);
        Assert.Equal("Acme | copy 1", c1.CompanyName);

        var c2 = shell.CloneWorkflow(source);
        Assert.Equal("Acme | copy 2", c2.CompanyName);

        // Cloning a copy strips its suffix back to the base first.
        var c3 = shell.CloneWorkflow(c1);
        Assert.Equal("Acme | copy 3", c3.CompanyName);

        // A different position gets its own numbering.
        var other = new InterviewState { CompanyName = "Acme", Position = "Manager" };
        shell.Store.SaveState(other);
        Assert.Equal("Acme | copy 1", shell.CloneWorkflow(other).CompanyName);
    }

    [Fact]
    public void Clone_is_a_deep_copy_with_fresh_identity()
    {
        var shell = NewShell();
        var source = new InterviewState { CompanyName = "Acme", Position = "SE" };
        source.Stories.Add(new Story { Title = "Original story" });
        shell.Store.SaveState(source);

        var clone = shell.CloneWorkflow(source);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Single(clone.Stories);
        clone.Stories[0].Title = "Mutated";
        Assert.Equal("Original story", source.Stories[0].Title); // deep, not shared
    }

    [Fact]
    public void Steps_unlock_when_a_workflow_is_selected()
    {
        var shell = NewShell();
        Assert.All(shell.Steps.Where(s => s.Key != "setup"), s => Assert.True(s.IsLocked));

        var state = new InterviewState { CompanyName = "Acme", Position = "software engineer", CurrentStep = "research" };
        state.CompletedSteps.AddRange(["setup", "resume"]);
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);

        Assert.All(shell.Steps, s => Assert.False(s.IsLocked));
        Assert.True(shell.Steps.First(s => s.Key == "research").IsActive);
        Assert.True(shell.Steps.First(s => s.Key == "setup").IsDone);
        Assert.True(shell.Steps.First(s => s.Key == "interview_intel").ShowTech); // "software engineer"
        Assert.Equal(2, shell.ProgressCompleted);
        Assert.Contains("Acme", shell.WindowTitle);
    }

    [Fact]
    public void Navigation_persists_current_step_like_the_original()
    {
        var shell = NewShell();
        var state = new InterviewState { CompanyName = "Acme" };
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);

        shell.NavigateToStep("pitch");
        Assert.Equal("pitch", shell.Store.LoadState(state.Id)!.CurrentStep);
    }

    [Fact]
    public void Deleting_the_current_workflow_resets_to_setup()
    {
        var shell = NewShell();
        var state = new InterviewState { CompanyName = "Acme" };
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);

        shell.DeleteWorkflow(state.Id);
        Assert.False(shell.HasWorkflow);
        Assert.Null(shell.Store.LoadState(state.Id));
        Assert.All(shell.Steps.Where(s => s.Key != "setup"), s => Assert.True(s.IsLocked));
    }
}
