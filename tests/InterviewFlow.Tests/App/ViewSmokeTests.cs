using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterviewFlow.App.ViewModels;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;

[assembly: AvaloniaTestApplication(typeof(InterviewFlow.Tests.App.TestAppBuilder))]

namespace InterviewFlow.Tests.App;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<InterviewFlow.App.App>()
        // Real Skia drawing so tests can capture rendered frames.
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Headless smoke: every page view instantiates + lays out with a real VM —
/// catches XAML/template/binding construction errors in lazily-loaded pages.
/// </summary>
public sealed class ViewSmokeTests
{
    private static MainViewModel Shell()
    {
        var dir = Path.Combine(Path.GetTempPath(), "if-views-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, ".env");
        File.WriteAllText(envPath, $"INTERVIEW_DATA_DIR={Path.Combine(dir, "data")}\n");
        var shell = new MainViewModel(new AppConfig(EnvFile.Load(envPath)));

        var state = new InterviewState
        {
            CompanyName = "Acme",
            Position = "Staff Software Engineer",
            JobPosting = "JD text",
            ResumeText = "resume",
        };
        state.Research.RawReport = "# Report\nWith **markdown** and a table.";
        state.Research.QueryCostUsd = 0.42;
        state.Stories.Add(new Story
        {
            Title = "Story",
            Situation = "S", Task = "T", Action = "A", Result = "R",
            EarnedSecret = "E",
            FitScores = new() { ["behavioral"] = "Strong Fit" },
        });
        shell.Store.SaveState(state);
        shell.SelectWorkflow(state.Id);
        return shell;
    }

    private static void Mount(MainViewModel shell)
    {
        var window = new InterviewFlow.App.Views.MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        window.Close();
    }

    [AvaloniaFact]
    public void Every_step_page_renders()
    {
        var shell = Shell();
        foreach (var step in shell.Steps.ToList())
        {
            shell.NavigateToStep(step.Key);
            Mount(shell);
        }
    }

    [AvaloniaFact]
    public void About_config_and_custom_action_pages_render()
    {
        var shell = Shell();
        shell.OpenAboutCommand.Execute(null);
        Mount(shell);
        shell.OpenConfigCommand.Execute(null);
        Mount(shell);
        shell.AddCustomActionCommand.Execute(null);
        Mount(shell);

        var action = new CustomAction { Name = "Ask things", PromptTemplate = "{{company_name}}" };
        shell.ActionStore.Save([action]);
        shell.ReloadCustomActions();
        shell.OpenCustomActionCommand.Execute(action);
        Mount(shell);
    }
}
