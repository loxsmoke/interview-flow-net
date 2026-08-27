using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// Importing an existing .env over the active settings file (docs/08 §8.2).
/// </summary>
public sealed class EnvImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-imp-" + Guid.NewGuid().ToString("N")[..8]);

    public EnvImportTests()
    {
        Directory.CreateDirectory(_dir);
        ClearEnv();
    }

    private static void ClearEnv()
    {
        foreach (var key in AppConfig.KnownKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    public void Dispose()
    {
        ClearEnv();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private AppConfig ActiveConfig(string content = "")
    {
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, content);
        return AppConfig.Load(path);
    }

    [Fact]
    public void Import_replaces_settings_and_keeps_a_backup()
    {
        var config = ActiveConfig("ACTIVE_PROVIDER=anthropic\nANTHROPIC_API_KEY=old-key\n");
        var source = Write("mine.env",
            "ACTIVE_PROVIDER=openai\nOPENAI_API_KEY=sk-mine\nRESUME_NAME=Jane\n");

        var result = config.ImportFrom(source);

        Assert.True(result.Ok);
        Assert.Equal(3, result.KeyCount);
        Assert.Equal("openai", config.ActiveProvider);
        Assert.Equal("sk-mine", config.OpenAiApiKey);
        Assert.Equal("Jane", config.ResumeName);
        Assert.Equal("", config.AnthropicApiKey); // the replaced file's key is gone

        Assert.NotNull(result.BackupPath);
        Assert.Contains("old-key", File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public void Imported_values_are_not_shadowed_by_earlier_in_session_edits()
    {
        // The Configuration screen also sets process env so edits apply live;
        // process env wins over the file, so import must re-sync it.
        var config = ActiveConfig("ANTHROPIC_API_KEY=old-key\n");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "edited-in-session");
        Environment.SetEnvironmentVariable("RESUME_NAME", "Stale Name");
        Assert.Equal("edited-in-session", config.AnthropicApiKey);

        var source = Write("mine.env", "ANTHROPIC_API_KEY=sk-imported\n");
        Assert.True(config.ImportFrom(source).Ok);

        Assert.Equal("sk-imported", config.AnthropicApiKey);   // file value wins now
        Assert.Equal("", config.ResumeName);                    // stale key cleared
    }

    [Fact]
    public void A_file_with_no_settings_is_refused_and_changes_nothing()
    {
        var config = ActiveConfig("ACTIVE_PROVIDER=anthropic\n");
        var source = Write("notes.txt", "just some prose\nno assignments here\n");

        var result = config.ImportFrom(source);

        Assert.False(result.Ok);
        Assert.Contains("no KEY=value", result.Error);
        Assert.Equal("anthropic", config.ActiveProvider);
    }

    [Fact]
    public void Missing_source_is_reported_without_touching_settings()
    {
        var config = ActiveConfig("ACTIVE_PROVIDER=gemini\n");
        var result = config.ImportFrom(Path.Combine(_dir, "nope.env"));

        Assert.False(result.Ok);
        Assert.Contains("no longer exists", result.Error);
        Assert.Equal("gemini", config.ActiveProvider);
    }

    [Fact]
    public void Importing_the_active_file_onto_itself_is_refused()
    {
        var config = ActiveConfig("ACTIVE_PROVIDER=ollama\n");
        var result = config.ImportFrom(config.Env.Path);

        Assert.False(result.Ok);
        Assert.Contains("already the active settings file", result.Error);
        Assert.Equal("ollama", config.ActiveProvider);
    }

    [Fact]
    public void Import_works_when_no_settings_file_exists_yet()
    {
        // First run: the resolved path has no file behind it.
        var target = Path.Combine(_dir, "fresh", ".env");
        var config = AppConfig.Load(target);
        var source = Write("mine.env", "ACTIVE_PROVIDER=openai\nOPENAI_API_KEY=sk-x\n");

        var result = config.ImportFrom(source);

        Assert.True(result.Ok);
        Assert.Null(result.BackupPath); // nothing to back up
        Assert.True(File.Exists(target));
        Assert.Equal("openai", config.ActiveProvider);
    }

    [Fact]
    public void Comments_and_unknown_keys_survive_the_import()
    {
        var config = ActiveConfig("ACTIVE_PROVIDER=anthropic\n");
        var source = Write("mine.env",
            "# my keys\nACTIVE_PROVIDER=openai\nSOME_OTHER_TOOL=keep-me\n");

        Assert.True(config.ImportFrom(source).Ok);

        var written = File.ReadAllText(config.Env.Path);
        Assert.Contains("# my keys", written);
        Assert.Contains("SOME_OTHER_TOOL=keep-me", written);
    }
}
