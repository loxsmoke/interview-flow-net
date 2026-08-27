using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// Shell view-model: sidebar (steps, custom actions, progress), page switching,
/// and the currently selected workflow. Mirrors the original SPA's App component
/// (index.html) — one persistent sidebar, content area swaps per step.
/// Screen-specific logic lives in the page view-models under Pages/.
/// Partials: MainViewModel.Queue.cs — queue manager/worker, section streams,
/// sidebar badge updates.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int StepCount = 12;

    private static readonly string[] TechnicalKeywords =
    [
        "engineer", "developer", "programmer", "software", "coding", "swe",
        "backend", "frontend", "fullstack", "full-stack", "full stack",
        "data scientist", "data engineer", "ml engineer", "machine learning",
        "devops", "sre",
    ];

    public AppConfig Config { get; }
    public StateStore Store { get; private set; }
    public CustomActionStore ActionStore { get; private set; }

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public ObservableCollection<StepItemViewModel> Steps { get; } = [];
    public ObservableCollection<CustomAction> CustomActions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasWorkflow), nameof(CompanyLabel),
        nameof(ProgressCompleted), nameof(ProgressLabel))]
    private InterviewState? _currentState;

    [ObservableProperty] private object? _currentPage;

    partial void OnCurrentPageChanged(object? oldValue, object? newValue) =>
        (oldValue as IDisposable)?.Dispose(); // pages own queue subscriptions

    public MainViewModel() : this(SafeConfig()) { } // design-time / fallback

    public MainViewModel(AppConfig config)
    {
        Config = config;
        Store = new StateStore(config.DataDir());
        ActionStore = new CustomActionStore(config.DataDir());
        BuildSteps();
        ReloadCustomActions();
        InitializeQueue();
        NavigateToStep("setup");
    }

    private static AppConfig SafeConfig()
    {
        try { return AppConfig.Load(); }
        catch { return new AppConfig(EnvFile.Load(Path.Combine(Path.GetTempPath(), ".env"))); }
    }

    // ── Sidebar data ─────────────────────────────────────────────────────────

    private void BuildSteps()
    {
        // The STEPS table (docs/00-overview.md), verbatim from index.html.
        (string Key, string Icon, string Label, string Desc, bool Web, bool NeedsResume)[] rows =
        [
            ("setup", "📋", "Setup", "Upload job posting", false, false),
            ("resume", "📄", "Resume", "Upload or select resume", false, true),
            ("research", "🔍", "Research", "Deep-dive company analysis", true, false),
            ("interview_intel", "🕵️", "Interview Intel", "Real questions & interview patterns", true, false),
            ("jd_decode", "🔬", "Job Decoder", "Six-lens deep read of the job posting", false, false),
            ("resume_tailor", "✏️", "Resume Tailor", "Tailor resume to the JD", false, true),
            ("stories", "📖", "Story Bank", "Mine & manage your stories", false, true),
            ("pitch", "🎯", "Pitch", "Build your positioning", false, true),
            ("concerns", "🛡️", "Concerns", "Anticipate objections", false, true),
            ("mock_interview", "🎙️", "Mock Interview", "Practice with AI interviewer", false, true),
            ("salary", "💰", "Salary", "Comp negotiation coaching", true, false),
            ("debrief", "📝", "Debrief", "Post-interview reflection", false, false),
        ];
        foreach (var r in rows)
            Steps.Add(new StepItemViewModel(r.Key, r.Icon, r.Label, r.Desc, r.Web, r.NeedsResume, s => NavigateToStep(s.Key)));
        RefreshStepStates("setup");
    }

    public void ReloadCustomActions()
    {
        CustomActions.Clear();
        foreach (var a in ActionStore.Load())
            CustomActions.Add(a);
    }

    private void RefreshStepStates(string activeKey)
    {
        var completed = CurrentState?.CompletedSteps ?? [];
        var isTech = IsTechnicalRole(CurrentState?.Position);
        foreach (var step in Steps)
        {
            step.IsActive = step.Key == activeKey;
            step.IsDone = completed.Contains(step.Key);
            step.IsLocked = step.Key != "setup" && CurrentState is null;
            step.ShowTech = step.Key == "interview_intel" && isTech;
        }
    }

    private static bool IsTechnicalRole(string? position)
    {
        if (string.IsNullOrEmpty(position))
            return false;
        var lower = position.ToLowerInvariant();
        return TechnicalKeywords.Any(lower.Contains);
    }

    // ── Derived shell state ──────────────────────────────────────────────────

    public bool HasWorkflow => CurrentState is not null;

    public string CompanyLabel => CurrentState?.CompanyName ?? "";

    public string WindowTitle => CurrentState is { CompanyName.Length: > 0 } s
        ? $"{s.CompanyName} — {s.Position} | Interview Flow v{Version}"
        : $"Interview Flow v{Version}";

    public int ProgressCompleted =>
        CurrentState?.CompletedSteps.Count(k => Steps.Any(s => s.Key == k)) ?? 0;

    public string ProgressLabel => $"Progress: {ProgressCompleted}/{StepCount} steps";

    /// <summary>Amber cog pulse condition: no provider usable at all (§3.1).</summary>
    public bool ProviderConfigured =>
        Config.AnthropicApiKey.Length > 0
        || Config.OpenAiApiKey.Length > 0
        || Config.GeminiApiKey.Length > 0
        || Config.ActiveProvider == "ollama";

    /// <summary>Setup-header chip, e.g. "Anthropic - claude-sonnet-4-6".</summary>
    public string ProviderChip
    {
        get
        {
            var provider = Config.ActiveProvider;
            if (provider.Length == 0)
            {
                // Original fallback rule: only an OpenAI key present → openai, else anthropic.
                provider = Config.OpenAiApiKey.Length > 0 && Config.AnthropicApiKey.Length == 0
                    ? "openai" : "anthropic";
            }

            return provider switch
            {
                "openai" => $"OpenAI - {Config.OpenAiModel}",
                "gemini" => $"Gemini - {Config.GeminiModel}",
                "ollama" => $"Ollama - {Config.OllamaModel}",
                _ => $"Anthropic - {Config.AnthropicModel}",
            };
        }
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void NavigateToStep(string key)
    {
        var step = Steps.FirstOrDefault(s => s.Key == key);
        // Lock is derived from live state, not the possibly-stale flag — right
        // after SelectWorkflow the flags haven't been refreshed yet.
        if (step is null || (step.Key != "setup" && CurrentState is null))
            return;

        CurrentPage = key switch
        {
            "setup" => new SetupPageViewModel(this),
            "research" or "interview_intel" or "jd_decode" or "pitch" or "concerns" or "salary" =>
                new AgentPageViewModel(this, step),
            "stories" => new StoryBankPageViewModel(this),
            "debrief" => new DebriefPageViewModel(this),
            "resume" => new ResumePageViewModel(this),
            "resume_tailor" => new ResumeTailorPageViewModel(this),
            "mock_interview" => new MockInterviewPageViewModel(this),
            _ => PlaceholderFor(step),
        };
        RefreshStepStates(key);

        // The original persists current_step per workflow on every navigation.
        if (CurrentState is not null && CurrentState.CurrentStep != key)
        {
            CurrentState.CurrentStep = key;
            Store.SaveState(CurrentState);
        }
    }

    private static PlaceholderPageViewModel PlaceholderFor(StepItemViewModel step) =>
        new(step.Icon, step.Label, step.Description, step.Key switch
        {
            "resume" => "M6 — resume pipeline",
            "resume_tailor" or "mock_interview" => "M7 — tailor & chats",
            _ => "M5 — agent screens",
        });

    [RelayCommand]
    private void OpenAbout()
    {
        CurrentPage = new AboutPageViewModel(Version);
        RefreshStepStates("");
    }

    [RelayCommand]
    private void OpenConfig()
    {
        CurrentPage = new ConfigPageViewModel(this);
        RefreshStepStates("");
    }

    [RelayCommand]
    private void OpenCustomAction(CustomAction action)
    {
        CurrentPage = new CustomActionPageViewModel(this, action);
        RefreshStepStates("");
    }

    [RelayCommand]
    private void AddCustomAction()
    {
        CurrentPage = new CustomActionPageViewModel(this, null);
        RefreshStepStates("");
    }

    // ── Workflow lifecycle (used by SetupPageViewModel) ──────────────────────

    public void SelectWorkflow(string stateId)
    {
        var state = Store.LoadState(stateId);
        if (state is null)
            return;
        CurrentState = state;
        NavigateToStep(state.CurrentStep.Length > 0 ? state.CurrentStep : "setup");
    }

    public void StartNewWorkflow()
    {
        CurrentState = null;
        NavigateToStep("setup");
    }

    /// <summary>Clone with the original's " | copy N" naming (main.py:1869).</summary>
    public InterviewState CloneWorkflow(InterviewState source)
    {
        // Deep copy via serializer round-trip (Pydantic model_copy(deep=True) parity).
        var clone = System.Text.Json.JsonSerializer.Deserialize<InterviewState>(
            System.Text.Json.JsonSerializer.Serialize(source, StateJson.Options), StateJson.Options)!;
        clone.Id = ModelDefaults.NewId();
        clone.CreatedAt = ModelDefaults.NowIso();
        clone.UpdatedAt = clone.CreatedAt;

        var copySuffix = new System.Text.RegularExpressions.Regex(
            @"^(.*?)\s*\| copy (\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var m0 = copySuffix.Match(source.CompanyName);
        var baseCompany = m0.Success ? m0.Groups[1].Value.TrimEnd() : source.CompanyName;

        var maxN = 0;
        foreach (var summary in Store.ListSummaries())
        {
            if (summary.Position != source.Position)
                continue;
            var m = copySuffix.Match(summary.CompanyName);
            if (m.Success && m.Groups[1].Value.TrimEnd() == baseCompany)
                maxN = Math.Max(maxN, int.Parse(m.Groups[2].Value));
        }

        clone.CompanyName = $"{baseCompany} | copy {maxN + 1}";
        Store.SaveState(clone);
        return clone;
    }

    public void DeleteWorkflow(string stateId)
    {
        Store.DeleteState(stateId);
        if (CurrentState?.Id == stateId)
        {
            CurrentState = null;
            NavigateToStep("setup");
        }
    }

    /// <summary>Config screen changed a setting — refresh derived shell state.</summary>
    public void NotifyConfigChanged()
    {
        OnPropertyChanged(nameof(ProviderConfigured));
        OnPropertyChanged(nameof(ProviderChip));
    }

    /// <summary>
    /// Re-point the stores after a successful data-folder migration, matching
    /// the original's apply-location (no restart required).
    /// </summary>
    public void SwitchDataDir(string newDir)
    {
        Store = new StateStore(newDir);
        ActionStore = new CustomActionStore(newDir);
        _runner = null; // rebuilt against the new stores on next run
        CurrentState = null;
        ReloadCustomActions();
        NavigateToStep("setup");
    }

    /// <summary>Called by pages after they mutate/persist CurrentState.</summary>
    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HasWorkflow));
        OnPropertyChanged(nameof(CompanyLabel));
        OnPropertyChanged(nameof(ProgressCompleted));
        OnPropertyChanged(nameof(ProgressLabel));
        RefreshStepStates(Steps.FirstOrDefault(s => s.IsActive)?.Key ?? "");
    }
}
