using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Providers;
using InterviewFlow.Core.State;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>A selectable model in a provider card's dropdown.</summary>
public sealed record ModelOption(string Id, string Label, string Note)
{
    public string Display => Note.Length > 0 ? $"{Label} — {Note}" : Label;
}

/// <summary>An Ollama model row with tool-calling capability (§3.11).</summary>
public sealed record OllamaModelOption(string Name, bool SupportsTools)
{
    public string Display => SupportsTools ? $"{Name} · tools ✓" : Name;
}

/// <summary>One file in the Data Storage list.</summary>
public sealed record DataFileRow(string Name, string Note, long SizeBytes)
{
    public string Display => Note.Length > 0
        ? $"{Name} ({Note}) — {Format(SizeBytes)}"
        : $"{Name} — {Format(SizeBytes)}";

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>
/// Configuration screen (docs/03-ui-spec.md §3.11): provider cards with keys
/// and models, live model fetching, resume info, data storage + the migration
/// wizard. Settings apply-on-change to the shared env file (ADR-002).
/// </summary>
public sealed partial class ConfigPageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private bool _loading = true;

    // ── Provider selection ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnthropic), nameof(IsOpenAi), nameof(IsGemini), nameof(IsOllama))]
    private string _activeProvider = "anthropic";

    public bool IsAnthropic => ActiveProvider == "anthropic";
    public bool IsOpenAi => ActiveProvider == "openai";
    public bool IsGemini => ActiveProvider == "gemini";
    public bool IsOllama => ActiveProvider == "ollama";

    // ── Keys & models ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnthropicConfigured))]
    private string _anthropicKey = "";
    [ObservableProperty] private string _anthropicModel = "";
    [ObservableProperty] private bool _showAnthropicKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenAiConfigured))]
    private string _openAiKey = "";
    [ObservableProperty] private string _openAiModel = "";
    [ObservableProperty] private bool _showOpenAiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeminiConfigured))]
    private string _geminiKey = "";
    [ObservableProperty] private string _geminiModel = "";
    [ObservableProperty] private bool _showGeminiKey;

    [ObservableProperty] private string _ollamaBaseUrl = "";
    [ObservableProperty] private string _ollamaModel = "";
    [ObservableProperty] private int _numCtxIndex;
    [ObservableProperty] private string _fetchStatus = "";
    [ObservableProperty] private bool _selectedOllamaLacksTools;

    public bool AnthropicConfigured => AnthropicKey.Length > 0;
    public bool OpenAiConfigured => OpenAiKey.Length > 0;
    public bool GeminiConfigured => GeminiKey.Length > 0;

    // ── Resume info ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _resumeName = "";
    [ObservableProperty] private string _resumeContact = "";

    // ── Data storage ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDataFolderCommand))]
    private string _dataDir = "";
    [ObservableProperty] private string _pendingDataDir = "";

    public ObservableCollection<DataFileRow> DataFiles { get; } = [];
    public string DefaultDataDir { get; }
    public string EnvPath { get; }
    public bool TelemetryEnabled => Core.Logging.Telemetry.IsExporting;

    // ── Static option lists (verbatim from index.html) ───────────────────────

    public IReadOnlyList<ModelOption> AnthropicModels { get; } =
    [
        new("claude-opus-5", "Claude Opus 5", "Most capable"),
        new("claude-opus-4-8", "Claude Opus 4.8", "Previous Opus"),
        new("claude-opus-4-7", "Claude Opus 4.7", "Previous Opus"),
        new("claude-sonnet-5", "Claude Sonnet 5", "Balanced, recommended"),
        new("claude-sonnet-4-6", "Claude Sonnet 4.6", "Previous balanced"),
        new("claude-haiku-4-5", "Claude Haiku 4.5", "Fast & affordable"),
    ];

    public IReadOnlyList<ModelOption> OpenAiModels { get; } =
    [
        new("gpt-5.6-sol", "GPT-5.6 Sol", "Flagship"),
        new("gpt-5.5", "GPT-5.5", "Previous flagship"),
        new("gpt-5.6-terra", "GPT-5.6 Terra", "Balanced, recommended"),
        new("gpt-5.4", "GPT-5.4", "Coding & agentic"),
        new("gpt-5", "GPT-5", "GPT-5 base"),
        new("gpt-5.4-mini", "GPT-5.4 mini", "Fast & affordable"),
        new("gpt-5.6-luna", "GPT-5.6 Luna", "Cost-optimized"),
        new("gpt-4.1", "GPT-4.1", "Latest GPT-4"),
        new("gpt-4o", "GPT-4o", "Multimodal · web search"),
        new("gpt-4.1-mini", "GPT-4.1 mini", "Affordable"),
        new("gpt-4o-mini", "GPT-4o mini", "Affordable"),
    ];

    public ObservableCollection<ModelOption> GeminiModels { get; } = [];
    public ObservableCollection<OllamaModelOption> OllamaModels { get; } = [];

    // Dropdowns bind SelectedItem to these nullable options rather than
    // SelectedValue to the model strings: an unresolvable selection then sets
    // null here (harmless) instead of clearing the configured model name.

    [ObservableProperty] private ModelOption? _selectedAnthropicModel;
    [ObservableProperty] private ModelOption? _selectedOpenAiModel;
    [ObservableProperty] private ModelOption? _selectedGeminiModel;
    [ObservableProperty] private OllamaModelOption? _selectedOllamaModel;

    partial void OnSelectedAnthropicModelChanged(ModelOption? value)
    {
        if (value is not null)
            AnthropicModel = value.Id;
    }

    partial void OnSelectedOpenAiModelChanged(ModelOption? value)
    {
        if (value is not null)
            OpenAiModel = value.Id;
    }

    partial void OnSelectedGeminiModelChanged(ModelOption? value)
    {
        if (value is not null)
            GeminiModel = value.Id;
    }

    partial void OnSelectedOllamaModelChanged(OllamaModelOption? value)
    {
        if (value is not null)
            OllamaModel = value.Name;
    }

    /// <summary>num_ctx slider stops (§3.11): Default, 4k … 256k.</summary>
    public IReadOnlyList<string> NumCtxLabels { get; } =
        ["Default", "4k", "8k", "16k", "32k", "64k", "128k", "256k"];

    private static readonly string[] NumCtxValues =
        ["", "4096", "8192", "16384", "32768", "65536", "131072", "262144"];

    public string AnthropicKeyUrl => "https://console.anthropic.com/settings/keys";
    public string OpenAiKeyUrl => "https://platform.openai.com/api-keys";
    public string GeminiKeyUrl => "https://aistudio.google.com/app/apikey";

    /// <summary>(title, message, onConfirm) for the migration wizard.</summary>
    public event Action<string, string, Action>? ConfirmRequested;

    /// <summary>Asks the view for a folder picker; returns null on cancel.</summary>
    public event Func<Task<string?>>? FolderPickRequested;

    /// <summary>Asks the view for a .env file picker; returns null on cancel.</summary>
    public event Func<Task<string?>>? EnvFilePickRequested;

    public ConfigPageViewModel() : this(new MainViewModel()) { } // design-time

    public ConfigPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        var config = shell.Config;
        EnvPath = config.Env.Path;
        DefaultDataDir = Core.Paths.DataDir("");

        _activeProvider = ProviderRouter.ResolveProvider(config);
        _anthropicKey = config.AnthropicApiKey;
        _anthropicModel = config.AnthropicModel;
        _openAiKey = config.OpenAiApiKey;
        _openAiModel = config.OpenAiModel;
        _geminiKey = config.GeminiApiKey;
        _geminiModel = config.GeminiModel;
        _ollamaBaseUrl = config.OllamaBaseUrl;
        _ollamaModel = config.OllamaModel;
        _numCtxIndex = Math.Max(0, Array.IndexOf(NumCtxValues, config.OllamaNumCtx));
        _resumeName = config.ResumeName;
        _resumeContact = config.ResumeContact;
        _dataDir = config.DataDir();
        _pendingDataDir = _dataDir;

        _selectedAnthropicModel = AnthropicModels.FirstOrDefault(m => m.Id == _anthropicModel);
        _selectedOpenAiModel = OpenAiModels.FirstOrDefault(m => m.Id == _openAiModel);

        RefreshDataFiles();
        _loading = false;
    }

    // ── Apply-on-change persistence (openlogi-net pattern) ───────────────────

    // Note on null: a ComboBox with a TwoWay selection binding pushes null into
    // the bound property whenever its selection can't resolve (an empty
    // ItemsSource — e.g. the Gemini/Ollama lists before a fetch). Every handler
    // and Save() therefore treats null as "no change" rather than trusting the
    // non-nullable declaration.

    partial void OnActiveProviderChanged(string value) => Save("ACTIVE_PROVIDER", value);
    partial void OnAnthropicKeyChanged(string value) => Save("ANTHROPIC_API_KEY", value);
    partial void OnAnthropicModelChanged(string value) => Save("ANTHROPIC_MODEL", value);
    partial void OnOpenAiKeyChanged(string value) => Save("OPENAI_API_KEY", value);
    partial void OnOpenAiModelChanged(string value) => Save("OPENAI_MODEL", value);
    partial void OnGeminiKeyChanged(string value) => Save("GEMINI_API_KEY", value);
    partial void OnGeminiModelChanged(string value) => Save("GEMINI_MODEL", value);
    partial void OnOllamaBaseUrlChanged(string value) => Save("OLLAMA_BASE_URL", value);
    partial void OnResumeNameChanged(string value) => Save("RESUME_NAME", value);
    partial void OnResumeContactChanged(string value) => Save("RESUME_CONTACT", value);

    partial void OnOllamaModelChanged(string value)
    {
        Save("OLLAMA_MODEL", value);
        var match = OllamaModels.FirstOrDefault(m => m.Name == value);
        SelectedOllamaLacksTools = match is { SupportsTools: false };
    }

    partial void OnNumCtxIndexChanged(int value)
    {
        if (value >= 0 && value < NumCtxValues.Length)
            Save("OLLAMA_NUM_CTX", NumCtxValues[value]);
    }

    private void Save(string? key, string? value)
    {
        // null value = a selection binding cleared itself; ignore rather than
        // wiping a good setting (and never let it throw into a binding callback).
        if (_loading || key is null || value is null)
            return;
        try
        {
            var trimmed = value.Trim();
            _shell.Config.Set(key, trimmed);
            _shell.Config.Save();
            // Process env wins on read — keep it in sync so the change takes hold now.
            Environment.SetEnvironmentVariable(key, trimmed);
            _shell.NotifyConfigChanged();
        }
        catch (Exception ex)
        {
            Core.Logging.DiagnosticLog.Warn("config", $"could not save {key}: {ex.Message}");
        }
    }

    // ── Live model fetching ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task FetchGeminiModelsAsync()
    {
        FetchStatus = "Fetching Gemini models…";
        try
        {
            var models = await new GeminiProvider(GeminiKey).ListModelsAsync();
            GeminiModels.Clear();
            foreach (var (id, display) in models)
                GeminiModels.Add(new ModelOption(id, display, ""));
            FetchStatus = $"{models.Count} Gemini models available";
        }
        catch (Exception ex)
        {
            FetchStatus = $"Could not fetch Gemini models: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task FetchOllamaModelsAsync()
    {
        FetchStatus = "Fetching local models…";
        try
        {
            var models = await new OllamaProvider(OllamaBaseUrl).ListModelsAsync();
            OllamaModels.Clear();
            foreach (var (name, tools) in models)
                OllamaModels.Add(new OllamaModelOption(name, tools));
            FetchStatus = models.Count > 0
                ? $"{models.Count} local models available"
                : "No local models found — is Ollama running?";
            var match = OllamaModels.FirstOrDefault(m => m.Name == OllamaModel);
            SelectedOllamaLacksTools = match is { SupportsTools: false };
        }
        catch (Exception ex)
        {
            FetchStatus = $"Could not reach Ollama: {ex.Message}";
        }
    }

    // ── Data storage & migration ─────────────────────────────────────────────

    private void RefreshDataFiles()
    {
        DataFiles.Clear();
        foreach (var name in DataMigration.ListDataFiles(DataDir).Take(8))
        {
            var note = name == StateStore.DataFileName ? "sessions"
                : name == CustomActionStore.FileName ? "custom actions"
                : name.Equals(Core.ResumePipeline.DocxExporter.TemplateFileName,
                    StringComparison.OrdinalIgnoreCase) ? "resume export template"
                : "";
            long size = 0;
            try
            {
                size = new FileInfo(Path.Combine(DataDir, name)).Length;
            }
            catch
            {
                // Unreadable — show it with size 0 rather than hiding it.
            }

            DataFiles.Add(new DataFileRow(name, note, size));
        }
    }

    /// <summary>
    /// Opens the folder the app is actually reading — <see cref="DataDir"/>,
    /// not the pending edit in the textbox, which may not exist yet.
    /// </summary>
    public bool CanOpenDataFolder => DataDir.Length > 0 && Directory.Exists(DataDir);

    [RelayCommand(CanExecute = nameof(CanOpenDataFolder))]
    private void OpenDataFolder() => Platform.ShellOpen.OpenFolder(DataDir);

    [RelayCommand]
    private void UseDefaultDataDir() => PendingDataDir = DefaultDataDir;

    [RelayCommand]
    private async Task BrowseDataDirAsync()
    {
        if (FolderPickRequested is null)
            return;
        var picked = await FolderPickRequested();
        if (picked is not null)
            PendingDataDir = picked;
    }

    /// <summary>
    /// Points the app at another data folder. A folder that already holds
    /// workflows is *adopted* (just switch to it) — migrating into it would
    /// overwrite that data with the current folder's. An empty target runs the
    /// 5-phase move wizard (docs/08 §8.5).
    /// </summary>
    [RelayCommand]
    private void SaveDataDir()
    {
        var target = PendingDataDir.Trim();
        if (target.Length == 0 || DataMigration.IsSameDirectory(target, DataDir))
        {
            FetchStatus = "That's already the current data folder.";
            return;
        }

        var targetFiles = DataMigration.ListDataFiles(target);
        if (targetFiles.Count > 0)
        {
            ConfirmRequested?.Invoke(
                "Use the data already in that folder?",
                $"{target}\nalready contains {targetFiles.Count} data file(s).\n\n" +
                "The app will switch to it and use those workflows. " +
                "Nothing is copied, moved, or deleted.",
                () =>
                {
                    if (SwitchTo(target))
                        FetchStatus = $"Now using the {targetFiles.Count} data file(s) in {target}.";
                });
            return;
        }

        var files = DataMigration.ListDataFiles(DataDir);
        if (files.Count == 0)
        {
            // Nothing to move on either side — just re-point.
            if (SwitchTo(target))
                FetchStatus = $"Data folder set to {target}.";
            return;
        }

        ConfirmRequested?.Invoke(
            "Move data folder?",
            $"{files.Count} file(s) will be copied from\n{DataDir}\nto\n{target}\n\n" +
            "Each copy is verified byte-for-byte before the originals are deleted.",
            () => RunMigration(DataDir, target, files));
    }

    /// <summary>Persists the new location and re-points the stores. No file moves.</summary>
    private bool SwitchTo(string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            _shell.Config.Set("INTERVIEW_DATA_DIR", target);
            _shell.Config.Save();
            Environment.SetEnvironmentVariable("INTERVIEW_DATA_DIR", target);
        }
        catch (Exception ex)
        {
            FetchStatus = $"Could not save the new location: {ex.Message}";
            return false;
        }

        DataDir = target;
        PendingDataDir = target;
        RefreshDataFiles();
        _shell.SwitchDataDir(target);
        return true;
    }

    private void RunMigration(string fromDir, string toDir, List<string> files)
    {
        var copy = DataMigration.Copy(fromDir, toDir, files);
        if (!copy.Ok)
        {
            FetchStatus = $"Copy failed: {copy.Error}";
            return;
        }

        var verify = DataMigration.Verify(fromDir, toDir, files);
        if (!verify.Ok)
        {
            // Config still points at the original folder — nothing is lost.
            FetchStatus = $"Verification failed: {verify.Error}. Your data is untouched.";
            return;
        }

        try
        {
            _shell.Config.Set("INTERVIEW_DATA_DIR", toDir);
            _shell.Config.Save();
            Environment.SetEnvironmentVariable("INTERVIEW_DATA_DIR", toDir);
        }
        catch (Exception ex)
        {
            FetchStatus = $"Could not save the new location: {ex.Message}. Your data is untouched.";
            return;
        }

        var delete = DataMigration.DeleteOriginals(fromDir, files);
        DataDir = toDir;
        PendingDataDir = toDir;
        RefreshDataFiles();
        _shell.SwitchDataDir(toDir);

        FetchStatus = delete.Ok
            ? $"Moved {files.Count} file(s) to {toDir}."
            : $"Moved to {toDir}, but some originals could not be deleted: {delete.Error}";
    }

    [RelayCommand]
    private void OpenKeyUrl(string url) => Platform.ShellOpen.OpenUrl(url);

    // ── Import an existing .env ──────────────────────────────────────────────

    /// <summary>
    /// Replaces the active settings file with one the user picks — the "I
    /// already have my keys" path. Confirmed first, since it overwrites; the
    /// previous file is kept as a .bak.
    /// </summary>
    [RelayCommand]
    private async Task ImportEnvAsync()
    {
        if (EnvFilePickRequested is null)
            return;
        var source = await EnvFilePickRequested();
        if (source is null)
            return;

        ConfirmRequested?.Invoke(
            "Import settings file?",
            $"Settings will be replaced with\n{source}\n\nThe current file is kept as a .bak alongside it.",
            () =>
            {
                var result = _shell.Config.ImportFrom(source);
                if (!result.Ok)
                {
                    FetchStatus = result.Error ?? "Import failed.";
                    return;
                }

                ReloadFromConfig();
                _shell.NotifyConfigChanged();

                // An imported INTERVIEW_DATA_DIR points the app at other data.
                var importedDataDir = _shell.Config.DataDir();
                if (!Core.State.DataMigration.IsSameDirectory(importedDataDir, DataDir))
                {
                    DataDir = importedDataDir;
                    PendingDataDir = importedDataDir;
                    _shell.SwitchDataDir(importedDataDir);
                }

                RefreshDataFiles();
                FetchStatus = result.BackupPath is null
                    ? $"Imported {result.KeyCount} setting(s)."
                    : $"Imported {result.KeyCount} setting(s). Previous file saved as {result.BackupPath}";
            });
    }

    /// <summary>Re-reads every field from the config after an external change.</summary>
    private void ReloadFromConfig()
    {
        var config = _shell.Config;
        _loading = true; // don't write each field straight back out
        try
        {
            ActiveProvider = ProviderRouter.ResolveProvider(config);
            AnthropicKey = config.AnthropicApiKey;
            AnthropicModel = config.AnthropicModel;
            OpenAiKey = config.OpenAiApiKey;
            OpenAiModel = config.OpenAiModel;
            GeminiKey = config.GeminiApiKey;
            GeminiModel = config.GeminiModel;
            OllamaBaseUrl = config.OllamaBaseUrl;
            OllamaModel = config.OllamaModel;
            NumCtxIndex = Math.Max(0, Array.IndexOf(NumCtxValues, config.OllamaNumCtx));
            ResumeName = config.ResumeName;
            ResumeContact = config.ResumeContact;
            SelectedAnthropicModel = AnthropicModels.FirstOrDefault(m => m.Id == AnthropicModel);
            SelectedOpenAiModel = OpenAiModels.FirstOrDefault(m => m.Id == OpenAiModel);
            SelectedGeminiModel = GeminiModels.FirstOrDefault(m => m.Id == GeminiModel);
            SelectedOllamaModel = OllamaModels.FirstOrDefault(m => m.Name == OllamaModel);
        }
        finally
        {
            _loading = false;
        }
    }
}
