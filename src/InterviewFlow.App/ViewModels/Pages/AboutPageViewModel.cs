using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>About page — content mirrors the original's AboutStep verbatim.</summary>
public sealed partial class AboutPageViewModel(string version) : ObservableObject
{
    public string Version { get; } = version;

    public sealed record FeatureRow(string Icon, string Label, string Description);

    public IReadOnlyList<FeatureRow> Features { get; } =
    [
        new("📄", "Resume", "Upload or paste your resume; pick from a saved resume library across sessions"),
        new("🔍", "Research", "Deep-dives a company using live web search — culture, tech stack, red flags, fit score"),
        new("🕵️", "Interview Intel", "Mines Glassdoor, Blind, Reddit, and Levels.fyi for real interview questions and process details"),
        new("🔬", "Job Decoder", "Reads between the lines of a job posting across six analytical lenses"),
        new("✏️", "Resume Tailor", "Reviews your resume against the JD, produces a structured rewrite exportable as a .docx, and provides an interactive chat coach"),
        new("📖", "Story Bank", "Extracts STAR stories from your resume with earned-secret insights"),
        new("🎯", "Pitch", "Builds 10s / 30s / 60s / 90s pitch variants for the specific role"),
        new("🛡️", "Concerns", "Anticipates interviewer objections and prepares counter-evidence"),
        new("🎙️", "Mock Interview", "Runs a full interview simulation with scoring and a debrief"),
        new("💰", "Salary", "Researches comp ranges and generates negotiation scripts"),
        new("⚡", "Custom Actions", "User-defined AI steps with access to all application context via template tags"),
    ];

    [RelayCommand]
    private void OpenGitHub() => Platform.ShellOpen.OpenUrl("https://github.com/loxsmoke/interview-flow");
}
