namespace InterviewFlow.Core.Config;

/// <summary>Outcome of importing an external .env over the active settings file.</summary>
/// <param name="Ok">False leaves the existing settings untouched.</param>
/// <param name="KeyCount">Distinct settings found in the imported file.</param>
/// <param name="BackupPath">Where the replaced file was kept, if there was one.</param>
public sealed record EnvImportResult(bool Ok, int KeyCount, string? BackupPath, string? Error);

/// <summary>
/// Typed accessor over the env-file config (docs/08-configuration.md). Reads
/// follow python-dotenv precedence: a real process environment variable wins
/// over the file (load_dotenv's default override=False). Writes go through
/// <see cref="EnvFile.Apply"/> + <see cref="EnvFile.Save"/> so the file stays
/// shareable with the original app.
/// </summary>
public sealed class AppConfig(EnvFile env)
{
    /// <summary>The active settings file. Replaced by <see cref="Reload"/>/<see cref="ImportFrom"/>.</summary>
    public EnvFile Env { get; private set; } = env;

    /// <summary>
    /// Every setting the app owns. Used when importing to re-sync the process
    /// environment, which otherwise shadows the file (see <see cref="Get"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownKeys =
    [
        "ACTIVE_PROVIDER",
        "ANTHROPIC_API_KEY", "ANTHROPIC_MODEL",
        "OPENAI_API_KEY", "OPENAI_MODEL",
        "GEMINI_API_KEY", "GEMINI_MODEL",
        "OLLAMA_BASE_URL", "OLLAMA_MODEL", "OLLAMA_NUM_CTX",
        "RESUME_NAME", "RESUME_CONTACT",
        "INTERVIEW_DATA_DIR",
    ];

    public static AppConfig Load(string? envPath = null) =>
        new(EnvFile.Load(envPath ?? DefaultEnvPath()));

    /// <summary>
    /// Env-file location (ADR-002 / 08 §8.2). An existing file wins, searched in
    /// the original's own precedence: the working directory first — which is the
    /// repo root under run.cmd, and what python-dotenv's `Path(".env")` reads —
    /// then beside the executable (packaged/portable), then the per-user config
    /// dir. When no file exists yet it is created in the working directory,
    /// falling back to the per-user dir if that isn't writable (installed apps
    /// can start in a read-only location).
    /// </summary>
    public static string DefaultEnvPath() =>
        ResolveEnvPath(SafeCurrentDirectory(), Paths.ExecutableDir(), Paths.ConfigDir());

    /// <summary>The rule itself, with its search roots injected so it is testable.</summary>
    internal static string ResolveEnvPath(string? workingDir, string exeDir, string userDir)
    {
        foreach (var root in new[] { workingDir, exeDir, userDir })
        {
            if (root is null)
                continue;
            var candidate = Path.Combine(root, ".env");
            if (File.Exists(candidate))
                return candidate;
        }

        return workingDir is not null && IsWritableDirectory(workingDir)
            ? Path.Combine(workingDir, ".env")
            : Path.Combine(userDir, ".env");
    }

    private static string? SafeCurrentDirectory()
    {
        try
        {
            var dir = Environment.CurrentDirectory;
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWritableDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return false;
            var probe = Path.Combine(dir, $".if-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string Get(string key, string fallback = "")
    {
        var fromProcess = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(fromProcess))
            return fromProcess;
        var fromFile = Env.Get(key);
        return string.IsNullOrEmpty(fromFile) ? fallback : fromFile;
    }

    public void Set(string key, string value) => Env.Apply(new Dictionary<string, string> { [key] = value });

    public void Save() => Env.Save();

    /// <summary>Re-reads the active file from disk (after an external edit or import).</summary>
    public void Reload() => Env = EnvFile.Load(Env.Path);

    /// <summary>
    /// Replaces the active settings file with <paramref name="sourcePath"/>.
    /// The previous file is kept as "&lt;name&gt;.bak" next to it, and the process
    /// environment is re-synced so imported values take effect immediately
    /// rather than being shadowed by earlier in-session edits.
    /// </summary>
    public EnvImportResult ImportFrom(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new EnvImportResult(false, 0, null, "That file no longer exists.");

        EnvFile source;
        try
        {
            source = EnvFile.Load(sourcePath);
        }
        catch (Exception ex)
        {
            return new EnvImportResult(false, 0, null, $"Could not read the file: {ex.Message}");
        }

        var keys = source.Keys();
        if (keys.Count == 0)
        {
            // Almost certainly the wrong file — refuse rather than wipe settings.
            return new EnvImportResult(false, 0, null,
                "That file contains no KEY=value settings — nothing was changed.");
        }

        if (Paths.SamePath(sourcePath, Env.Path))
            return new EnvImportResult(false, 0, null, "That is already the active settings file.");

        string? backupPath = null;
        try
        {
            var target = Env.Path;
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            if (File.Exists(target))
            {
                backupPath = target + ".bak";
                File.Copy(target, backupPath, overwrite: true);
            }

            File.Copy(sourcePath, target, overwrite: true);
        }
        catch (Exception ex)
        {
            return new EnvImportResult(false, 0, backupPath, $"Could not write the settings file: {ex.Message}");
        }

        Reload();
        SyncProcessEnvironment();
        Logging.DiagnosticLog.Info("config",
            $"imported settings from {sourcePath} ({keys.Count} keys); backup={backupPath ?? "none"}");
        return new EnvImportResult(true, keys.Distinct().Count(), backupPath, null);
    }

    /// <summary>
    /// Aligns the process environment with the file: values present in the file
    /// are set, and previously-set app keys that the file omits are cleared.
    /// Without this, keys written during an earlier in-session save would keep
    /// winning over the freshly imported file.
    /// </summary>
    public void SyncProcessEnvironment()
    {
        foreach (var key in KnownKeys)
        {
            var value = Env.Get(key);
            Environment.SetEnvironmentVariable(key, string.IsNullOrEmpty(value) ? null : value);
        }
    }

    // ── Typed keys (defaults from .env.example / main.py) ────────────────────

    /// <summary>anthropic|openai|gemini|ollama; empty = provider-layer fallback rule.</summary>
    public string ActiveProvider => Get("ACTIVE_PROVIDER");

    public string AnthropicApiKey => Get("ANTHROPIC_API_KEY");
    public string AnthropicModel => Get("ANTHROPIC_MODEL", "claude-sonnet-4-6");
    public string OpenAiApiKey => Get("OPENAI_API_KEY");
    public string OpenAiModel => Get("OPENAI_MODEL", "gpt-4o");
    public string GeminiApiKey => Get("GEMINI_API_KEY");
    public string GeminiModel => Get("GEMINI_MODEL", "gemini-2.5-flash");
    public string OllamaBaseUrl => Get("OLLAMA_BASE_URL", "http://localhost:11434");
    public string OllamaModel => Get("OLLAMA_MODEL", "llama3.2");
    /// <summary>Empty = Ollama default context window.</summary>
    public string OllamaNumCtx => Get("OLLAMA_NUM_CTX");

    public string ResumeName => Get("RESUME_NAME");
    public string ResumeContact => Get("RESUME_CONTACT");

    /// <summary>Explicit data dir override; resolution in Paths.DataDir.</summary>
    public string InterviewDataDir => Get("INTERVIEW_DATA_DIR");

    public string DataDir() => Paths.DataDir(InterviewDataDir);
}
