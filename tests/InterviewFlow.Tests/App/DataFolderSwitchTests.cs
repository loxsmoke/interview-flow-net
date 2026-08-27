using InterviewFlow.App.ViewModels;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Data-folder switching from Configuration. The critical case: a target that
/// already holds workflows must be ADOPTED, never migrated into — copying the
/// current folder over it would destroy that data.
/// </summary>
public sealed class DataFolderSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "if-switch-" + Guid.NewGuid().ToString("N")[..8]);

    public DataFolderSwitchTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var key in AppConfig.KnownKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    public void Dispose()
    {
        foreach (var key in AppConfig.KnownKeys)
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A data folder holding one workflow with the given company name.</summary>
    private string SeedDataDir(string name, string companyName)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        new StateStore(dir).SaveState(new InterviewState { CompanyName = companyName });
        return dir;
    }

    private (MainViewModel Shell, ConfigPageViewModel Vm) Open(string dataDir)
    {
        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, $"INTERVIEW_DATA_DIR={dataDir}\n");
        var shell = new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
        var vm = new ConfigPageViewModel(shell);
        string? title = null;
        vm.ConfirmRequested += (t, _, onConfirm) =>
        {
            title = t;
            onConfirm(); // the view's dialog says yes
        };
        vm.PropertyChanged += (_, _) => { };
        LastConfirmTitle = () => title;
        return (shell, vm);
    }

    private Func<string?> LastConfirmTitle { get; set; } = () => null;

    [Fact]
    public void Pointing_at_a_folder_that_already_has_data_adopts_it_without_copying()
    {
        var current = SeedDataDir("current", "Only Local");
        var existing = SeedDataDir("existing", "Real Workflows");
        var existingBefore = File.ReadAllBytes(Path.Combine(existing, StateStore.DataFileName));

        var (shell, vm) = Open(current);
        vm.PendingDataDir = existing;
        vm.SaveDataDirCommand.Execute(null);

        Assert.StartsWith("Use the data", LastConfirmTitle());
        // The target's data is untouched — this is the data-loss guard.
        Assert.Equal(existingBefore, File.ReadAllBytes(Path.Combine(existing, StateStore.DataFileName)));
        // And the source is left alone too (nothing moved or deleted).
        Assert.True(File.Exists(Path.Combine(current, StateStore.DataFileName)));

        Assert.Equal(existing, vm.DataDir);
        Assert.Equal(existing, shell.Store.DataDir);
        var summary = Assert.Single(shell.Store.ListSummaries());
        Assert.Equal("Real Workflows", summary.CompanyName);
    }

    [Fact]
    public void Pointing_at_an_empty_folder_still_migrates()
    {
        var current = SeedDataDir("current", "Mine");
        var empty = Path.Combine(_root, "empty");

        var (shell, vm) = Open(current);
        vm.PendingDataDir = empty;
        vm.SaveDataDirCommand.Execute(null);

        Assert.StartsWith("Move data", LastConfirmTitle());
        Assert.True(File.Exists(Path.Combine(empty, StateStore.DataFileName)));
        Assert.False(File.Exists(Path.Combine(current, StateStore.DataFileName))); // originals removed
        Assert.Equal("Mine", Assert.Single(shell.Store.ListSummaries()).CompanyName);
    }

    [Fact]
    public void Switching_from_an_empty_folder_needs_no_migration()
    {
        var current = Path.Combine(_root, "fresh");
        var existing = SeedDataDir("existing", "Real Workflows");

        var (shell, vm) = Open(current);
        vm.PendingDataDir = existing;
        vm.SaveDataDirCommand.Execute(null);

        Assert.Equal(existing, shell.Store.DataDir);
        Assert.Equal("Real Workflows", Assert.Single(shell.Store.ListSummaries()).CompanyName);
    }

    [Fact]
    public void The_new_location_is_persisted_to_the_settings_file()
    {
        var current = SeedDataDir("current", "Mine");
        var existing = SeedDataDir("existing", "Theirs");

        var (shell, vm) = Open(current);
        vm.PendingDataDir = existing;
        vm.SaveDataDirCommand.Execute(null);

        Assert.Equal(existing, EnvFile.Load(shell.Config.Env.Path).Get("INTERVIEW_DATA_DIR"));
    }

    [Fact]
    public void Selecting_the_current_folder_is_a_no_op()
    {
        var current = SeedDataDir("current", "Mine");
        var (_, vm) = Open(current);

        vm.PendingDataDir = current;
        vm.SaveDataDirCommand.Execute(null);

        Assert.Null(LastConfirmTitle());
        Assert.Contains("already the current data folder", vm.FetchStatus);
    }
}
