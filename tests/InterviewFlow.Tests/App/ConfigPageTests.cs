using Avalonia.Headless.XUnit;
using InterviewFlow.App.ViewModels;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.App;

/// <summary>
/// First-startup and null-safety guards for Configuration. A selection binding
/// with an empty ItemsSource pushes null into its bound property; that must
/// never throw or wipe a configured value.
/// </summary>
public sealed class ConfigPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-cfg-" + Guid.NewGuid().ToString("N")[..8]);

    // Saving config also sets the PROCESS environment (so edits take effect
    // without a restart), which would otherwise leak into other tests.
    private static readonly string[] TouchedEnvKeys =
    [
        "ACTIVE_PROVIDER", "ANTHROPIC_API_KEY", "ANTHROPIC_MODEL",
        "OPENAI_API_KEY", "OPENAI_MODEL", "GEMINI_API_KEY", "GEMINI_MODEL",
        "OLLAMA_BASE_URL", "OLLAMA_MODEL", "OLLAMA_NUM_CTX",
        "RESUME_NAME", "RESUME_CONTACT", "INTERVIEW_DATA_DIR",
    ];

    public ConfigPageTests() => ClearTouchedEnv();

    private static void ClearTouchedEnv()
    {
        foreach (var key in TouchedEnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private MainViewModel FreshShell(string envContent = "")
    {
        var envPath = Path.Combine(_dir, ".env");
        if (envContent.Length > 0)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(envPath, envContent);
        }

        return new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
    }

    public void Dispose()
    {
        ClearTouchedEnv();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void First_startup_builds_without_env_or_data_dir()
    {
        // Nothing on disk: no .env, no data folder — exactly a fresh install.
        var vm = new ConfigPageViewModel(FreshShell());

        Assert.Equal("anthropic", vm.ActiveProvider);
        Assert.False(vm.AnthropicConfigured);
        Assert.Empty(vm.DataFiles);
        Assert.NotEmpty(vm.DefaultDataDir);
        Assert.NotEmpty(vm.EnvPath);
        // Lists that are only populated by an explicit fetch start empty.
        Assert.Empty(vm.GeminiModels);
        Assert.Empty(vm.OllamaModels);
    }

    [Fact]
    public void Null_selection_writeback_neither_throws_nor_clears_settings()
    {
        var vm = new ConfigPageViewModel(FreshShell(
            "ACTIVE_PROVIDER=gemini\nGEMINI_API_KEY=k\nGEMINI_MODEL=gemini-3.6-flash\nOLLAMA_MODEL=llama3.2\n"));

        // What an empty ComboBox does to a TwoWay selection binding.
        vm.SelectedGeminiModel = null;
        vm.SelectedOllamaModel = null;
        vm.SelectedAnthropicModel = null;
        vm.SelectedOpenAiModel = null;

        // The configured names survive — a cleared dropdown is not an edit.
        Assert.Equal("gemini-3.6-flash", vm.GeminiModel);
        Assert.Equal("llama3.2", vm.OllamaModel);
    }

    [Fact]
    public void Selecting_a_model_option_updates_and_persists_the_name()
    {
        var shell = FreshShell("ACTIVE_PROVIDER=anthropic\n");
        var vm = new ConfigPageViewModel(shell);

        vm.SelectedAnthropicModel = vm.AnthropicModels.First(m => m.Id == "claude-opus-4-7");

        Assert.Equal("claude-opus-4-7", vm.AnthropicModel);
        Assert.Equal("claude-opus-4-7", EnvFile.Load(shell.Config.Env.Path).Get("ANTHROPIC_MODEL"));
    }

    [Fact]
    public void Preselects_the_configured_model_in_the_dropdown()
    {
        var vm = new ConfigPageViewModel(FreshShell("ANTHROPIC_MODEL=claude-haiku-4-5\n"));
        Assert.NotNull(vm.SelectedAnthropicModel);
        Assert.Equal("claude-haiku-4-5", vm.SelectedAnthropicModel!.Id);
    }

    /// <summary>
    /// A model id that predates the current list (here the dated Haiku 4.5 id,
    /// dropped when the picker moved to the bare ids) leaves the dropdown
    /// unselected but must not rewrite what the user configured.
    /// </summary>
    [Fact]
    public void An_id_no_longer_listed_keeps_working()
    {
        var shell = FreshShell("ANTHROPIC_MODEL=claude-haiku-4-5-20251001\n");
        var vm = new ConfigPageViewModel(shell);

        Assert.Null(vm.SelectedAnthropicModel);
        Assert.Equal("claude-haiku-4-5-20251001", vm.AnthropicModel);
        Assert.Equal("claude-haiku-4-5-20251001", shell.Config.AnthropicModel);
    }

    /// <summary>
    /// "📂 Open folder" targets the folder the app actually reads, and stays
    /// disabled when that folder isn't on disk (a hand-edited path, a removed
    /// drive) rather than launching a file browser at nothing.
    /// </summary>
    [Fact]
    public void Open_folder_is_enabled_only_when_the_data_folder_exists()
    {
        var shell = FreshShell("");
        var vm = new ConfigPageViewModel(shell);
        Directory.CreateDirectory(vm.DataDir);

        Assert.True(vm.CanOpenDataFolder);
        Assert.True(vm.OpenDataFolderCommand.CanExecute(null));

        vm.DataDir = Path.Combine(Path.GetTempPath(), "if-not-here-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(vm.CanOpenDataFolder);
        Assert.False(vm.OpenDataFolderCommand.CanExecute(null));
    }

    [Fact]
    public void Editing_a_key_persists_and_updates_the_shell_chip()
    {
        var shell = FreshShell("ACTIVE_PROVIDER=anthropic\n");
        var vm = new ConfigPageViewModel(shell);
        Assert.False(shell.ProviderConfigured);

        vm.AnthropicKey = "sk-ant-test";

        Assert.True(vm.AnthropicConfigured);
        Assert.True(shell.ProviderConfigured);
        Assert.Equal("sk-ant-test", EnvFile.Load(shell.Config.Env.Path).Get("ANTHROPIC_API_KEY"));
    }

    [AvaloniaFact]
    public void Config_page_renders_on_a_first_startup_shell()
    {
        var shell = FreshShell();
        shell.OpenConfigCommand.Execute(null);
        var window = new InterviewFlow.App.Views.MainWindow { DataContext = shell, Width = 1200, Height = 900 };
        window.Show();
        window.UpdateLayout();
        window.Close();
    }
}
