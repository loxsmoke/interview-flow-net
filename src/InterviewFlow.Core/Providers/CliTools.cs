using System.Text.RegularExpressions;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// Locates the installed agent CLIs (Claude Code, OpenAI Codex) and the
/// scratch directory they run in. An explicit configured path wins; otherwise
/// PATH, then the places the official installers put them. Detection runs
/// each time it is asked — the Codex desktop app replaces its bundled binary
/// under a new hashed folder on every update, so nothing here is cached.
/// </summary>
public static partial class CliTools
{
    public const string ClaudeProviderName = "Claude Code";
    public const string CodexProviderName = "Codex";

    [GeneratedRegex("""^\s*model\s*=\s*"([^"]*)"\s*(#.*)?$""")]
    private static partial Regex TomlModelLine();

    public static string? FindClaude(string configuredPath) =>
        Find(configuredPath, "claude", ClaudeCandidates());

    public static string? FindCodex(string configuredPath) =>
        Find(configuredPath, "codex", CodexCandidates());

    /// <summary>Path, or a user-facing error explaining how to fix the configuration.</summary>
    public static string RequireClaude(string configuredPath) =>
        FindClaude(configuredPath) ?? throw NotFound(ClaudeProviderName, "claude", configuredPath,
            "install Claude Code (https://claude.com/claude-code)");

    public static string RequireCodex(string configuredPath) =>
        FindCodex(configuredPath) ?? throw NotFound(CodexProviderName, "codex", configuredPath,
            "install the Codex CLI or desktop app (https://openai.com/codex)");

    private static ProviderResponseException NotFound(string provider, string command, string configured, string install)
    {
        var message = configured.Trim().Length > 0
            ? $"{command} was not found at {configured.Trim()}. Fix the path in Configuration."
            : $"The {command} command was not found on PATH — {install}, or set its path in Configuration.";
        return new ProviderResponseException(message, provider, "cli_not_found", message);
    }

    private static string? Find(string configuredPath, string command, IEnumerable<string> fallbacks)
    {
        var configured = configuredPath.Trim().Trim('"');
        if (configured.Length > 0)
        {
            // A bare command name is looked up on PATH; anything with a
            // separator is taken literally.
            var hasSeparator = configured.Contains(Path.DirectorySeparatorChar)
                || configured.Contains(Path.AltDirectorySeparatorChar);
            if (hasSeparator)
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;
            return OnPath(configured);
        }

        return OnPath(command) ?? fallbacks.FirstOrDefault(File.Exists);
    }

    /// <summary>First PATH entry holding the command (Windows also tries .exe/.cmd/.bat).</summary>
    internal static string? OnPath(string command, string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = dir.Trim().Trim('"');
            if (folder.Length == 0)
                continue;
            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder, command + ext);
                }
                catch (ArgumentException)
                {
                    continue; // junk PATH entry
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> ClaudeCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(home, ".local", "bin", "claude.exe");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "claude");
            yield return "/opt/homebrew/bin/claude";
            yield return "/usr/local/bin/claude";
        }
    }

    private static IEnumerable<string> CodexCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            // The desktop app ships the CLI under bin\ and bin\<hash>\; the
            // newest file is the one the app itself is currently using.
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            foreach (var exe in BundledCodexBinaries(root))
                yield return exe;
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
        }
        else
        {
            yield return "/opt/homebrew/bin/codex";
            yield return "/usr/local/bin/codex";
        }
    }

    internal static IReadOnlyList<string> BundledCodexBinaries(string root)
    {
        List<string> found = [];
        try
        {
            if (!Directory.Exists(root))
                return found;
            found.AddRange(Directory.EnumerateFiles(root, "codex.exe", SearchOption.TopDirectoryOnly));
            foreach (var dir in Directory.EnumerateDirectories(root))
                found.AddRange(Directory.EnumerateFiles(dir, "codex.exe", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable install folder — whatever was collected so far.
        }

        return found.OrderByDescending(f =>
        {
            try
            {
                return File.GetLastWriteTimeUtc(f);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }).ToList();
    }

    /// <summary>
    /// The model the Codex CLI would use on its own: the top-level
    /// <c>model = "…"</c> line of <c>~/.codex/config.toml</c>, or "" when
    /// unset or unreadable. Only the top-level table is read — a `model` under
    /// a `[profiles.x]` table belongs to that profile.
    /// </summary>
    public static string CodexDefaultModel(string? codexHome = null)
    {
        var home = codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var file = Path.Combine(home, "config.toml");
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith('['))
                    break;
                var match = TomlModelLine().Match(line);
                if (match.Success)
                    return match.Groups[1].Value.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No config — the CLI's own built-in default applies.
        }

        return "";
    }

    /// <summary>
    /// Where the CLIs run: an empty folder of our own, so neither picks up a
    /// CLAUDE.md / AGENTS.md or project settings from wherever the app was
    /// launched, and a read-only sandbox has nothing of the user's to read.
    /// </summary>
    public static string WorkingDirectory()
    {
        var dir = Path.Combine(Paths.LocalDataDir(), "cli-workdir");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetTempPath();
        }
    }
}
