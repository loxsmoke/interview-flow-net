using InterviewFlow.Core;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// Env-file and data-folder location rules (docs/08 §8.2/§8.3): the working
/// directory — the repo root under run.cmd, matching the original's
/// `Path(".env")` and `&lt;repo&gt;/data` — wins when a file/folder is there.
/// </summary>
public sealed class ConfigLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-loc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _originalCwd = Environment.CurrentDirectory;

    public ConfigLocationTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("INTERVIEW_DATA_DIR", null);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string Work, string Exe, string User) Roots()
    {
        var roots = ("work", "exe", "user");
        foreach (var name in new[] { roots.Item1, roots.Item2, roots.Item3 })
            Directory.CreateDirectory(Path.Combine(_dir, name));
        return (Path.Combine(_dir, "work"), Path.Combine(_dir, "exe"), Path.Combine(_dir, "user"));
    }

    [Fact]
    public void Env_file_in_the_working_directory_wins_over_exe_and_user_copies()
    {
        var (work, exe, user) = Roots();
        File.WriteAllText(Path.Combine(work, ".env"), "ACTIVE_PROVIDER=openai\n");
        File.WriteAllText(Path.Combine(exe, ".env"), "ACTIVE_PROVIDER=gemini\n");
        File.WriteAllText(Path.Combine(user, ".env"), "ACTIVE_PROVIDER=ollama\n");

        Assert.Equal(Path.Combine(work, ".env"), AppConfig.ResolveEnvPath(work, exe, user));
    }

    [Fact]
    public void Falls_back_to_the_exe_then_user_copy_when_the_working_dir_has_none()
    {
        var (work, exe, user) = Roots();
        File.WriteAllText(Path.Combine(exe, ".env"), "");
        File.WriteAllText(Path.Combine(user, ".env"), "");
        Assert.Equal(Path.Combine(exe, ".env"), AppConfig.ResolveEnvPath(work, exe, user));

        File.Delete(Path.Combine(exe, ".env"));
        Assert.Equal(Path.Combine(user, ".env"), AppConfig.ResolveEnvPath(work, exe, user));
    }

    [Fact]
    public void With_no_file_anywhere_it_is_created_in_the_working_directory()
    {
        var (work, exe, user) = Roots();
        Assert.Equal(Path.Combine(work, ".env"), AppConfig.ResolveEnvPath(work, exe, user));
    }

    [Fact]
    public void Unwritable_or_unknown_working_dir_falls_back_to_the_user_dir()
    {
        var (_, exe, user) = Roots();
        var missing = Path.Combine(_dir, "does-not-exist");
        Assert.Equal(Path.Combine(user, ".env"), AppConfig.ResolveEnvPath(missing, exe, user));
        Assert.Equal(Path.Combine(user, ".env"), AppConfig.ResolveEnvPath(null, exe, user));
    }

    [Fact]
    public void The_filesystem_root_is_not_treated_as_a_working_directory()
    {
        // A .app bundle launched from Finder starts with "/" as its cwd; neither
        // "/.env" nor "/data" is ours to read or create.
        var (_, exe, user) = Roots();
        var root = Path.GetPathRoot(Path.GetFullPath(_dir))!;

        Assert.Equal(Path.Combine(user, ".env"), AppConfig.ResolveEnvPath(root, exe, user));

        Environment.CurrentDirectory = root;
        var data = Paths.DataDir("");
        Assert.NotEqual(Path.Combine(root, "data"), data);
    }

    [Fact]
    public void Saving_materializes_the_file_in_the_working_directory()
    {
        Environment.CurrentDirectory = _dir;
        var path = Path.Combine(_dir, ".env");

        var config = AppConfig.Load(path);
        config.Set("ACTIVE_PROVIDER", "gemini");
        config.Save();

        Assert.True(File.Exists(path));
        Assert.Equal("gemini", EnvFile.Load(path).Get("ACTIVE_PROVIDER"));
    }

    [Fact]
    public void Existing_data_folder_beside_the_env_file_is_used()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        Environment.CurrentDirectory = _dir;

        // Read the cwd back rather than using _dir: on macOS the temp dir lives
        // behind the /var -> /private/var symlink, which setting the cwd resolves.
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "data"), Paths.DataDir(""));
    }

    [Fact]
    public void Without_a_local_data_folder_the_per_user_location_is_used()
    {
        Environment.CurrentDirectory = _dir; // no ./data here

        var resolved = Paths.DataDir("");
        Assert.NotEqual(Path.Combine(_dir, "data"), resolved);
        Assert.EndsWith("data", resolved);
    }

    [Fact]
    public void An_explicit_data_dir_always_wins()
    {
        var custom = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(Path.Combine(_dir, "data")); // would otherwise win
        Environment.CurrentDirectory = _dir;

        Assert.Equal(custom, Paths.DataDir(custom));
    }
}
