using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

public sealed class EnvFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private string WriteEnv(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Sample =
        "# Provider selection\n" +
        "ACTIVE_PROVIDER=anthropic\n" +
        "ANTHROPIC_API_KEY=sk-old\n" +
        "\n" +
        "# unrelated tool config, must survive untouched\n" +
        "SOME_OTHER_TOOL=keep-me\n" +
        "QUOTED=\"hello world\"\n" +
        "DUPLICATE=first\n" +
        "DUPLICATE=second\n";

    [Fact]
    public void Get_uses_last_assignment_and_strips_quotes()
    {
        var env = EnvFile.Load(WriteEnv(Sample));
        Assert.Equal("anthropic", env.Get("ACTIVE_PROVIDER"));
        Assert.Equal("hello world", env.Get("QUOTED"));
        Assert.Equal("second", env.Get("DUPLICATE")); // dotenv: last wins
        Assert.Null(env.Get("MISSING"));
    }

    [Fact]
    public void Apply_replaces_in_place_preserves_comments_appends_missing()
    {
        var path = WriteEnv(Sample);
        var env = EnvFile.Load(path);
        env.Apply(new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "sk-new",
            ["BRAND_NEW_KEY"] = "value1",
        });
        env.Save();

        var lines = File.ReadAllLines(path);
        Assert.Equal("# Provider selection", lines[0]);          // comment kept
        Assert.Equal("ANTHROPIC_API_KEY=sk-new", lines[2]);      // replaced in place
        Assert.Equal("SOME_OTHER_TOOL=keep-me", lines[5]);       // unrelated kept
        Assert.Equal("BRAND_NEW_KEY=value1", lines[^1]);         // appended at end
        Assert.EndsWith(Environment.NewLine, File.ReadAllText(path)); // trailing newline
    }

    [Fact]
    public void Apply_replaces_every_duplicate_line_like_the_original()
    {
        // The original's loop rewrites EACH matching line (no early exit).
        var path = WriteEnv(Sample);
        var env = EnvFile.Load(path);
        env.Apply(new Dictionary<string, string> { ["DUPLICATE"] = "both" });
        env.Save();

        var text = File.ReadAllText(path);
        Assert.Equal(2, text.Split("DUPLICATE=both").Length - 1);
        Assert.DoesNotContain("DUPLICATE=first", text);
    }

    [Fact]
    public void Round_trip_without_changes_is_stable()
    {
        var path = WriteEnv(Sample);
        var env = EnvFile.Load(path);
        env.Save();
        var once = File.ReadAllText(path);
        EnvFile.Load(path).Save();
        Assert.Equal(once, File.ReadAllText(path));
    }

    [Fact]
    public void Missing_file_loads_empty_and_save_creates()
    {
        var path = Path.Combine(_dir, "sub", ".env");
        var env = EnvFile.Load(path);
        Assert.Empty(env.Keys());
        env.Apply(new Dictionary<string, string> { ["A"] = "1" });
        env.Save();
        Assert.Equal("1", EnvFile.Load(path).Get("A"));
    }

    [Fact]
    public void AppConfig_reads_typed_values_with_defaults()
    {
        var path = WriteEnv("ACTIVE_PROVIDER=ollama\nOLLAMA_MODEL=llama3.3\n");
        var config = new AppConfig(EnvFile.Load(path));
        Assert.Equal("ollama", config.ActiveProvider);
        Assert.Equal("llama3.3", config.OllamaModel);
        Assert.Equal("http://localhost:11434", config.OllamaBaseUrl); // default
        Assert.Equal("claude-sonnet-5", config.AnthropicModel);       // default
        Assert.Equal("", config.ResumeName);
    }
}
