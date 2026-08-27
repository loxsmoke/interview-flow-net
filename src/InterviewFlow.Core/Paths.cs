using System.Runtime.InteropServices;

namespace InterviewFlow.Core;

/// <summary>
/// Filesystem locations behind static methods (openlogi-net convention), resolved
/// per-platform for Windows and macOS. The data directory follows the original
/// app's precedence: INTERVIEW_DATA_DIR env/config override, then a "data" folder
/// beside the executable when writable (portable mode), then the per-user dir
/// (docs/08-configuration.md §8.3).
/// </summary>
public static class Paths
{
    private const string AppFolderName = "InterviewFlow";

    /// <summary>Per-user config directory (created on demand).</summary>
    public static string ConfigDir()
    {
        // %APPDATA%\InterviewFlow on Windows; ~/Library/Application Support/InterviewFlow
        // on macOS (SpecialFolder.ApplicationData maps there under .NET on mac).
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(root, AppFolderName);
    }

    /// <summary>Per-user local/log directory (created on demand).</summary>
    public static string LocalDataDir()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        // macOS has no LocalApplicationData distinction that matters here; .NET maps
        // it to ~/Library/Application Support as well, which is what we want.
        return Path.Combine(root, AppFolderName);
    }

    public static string LogsDir() => Path.Combine(LocalDataDir(), "logs");

    /// <summary>Directory containing the executable (portable-mode anchor).</summary>
    public static string ExecutableDir()
    {
        var dir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
    }

    /// <summary>
    /// Resolves the data directory. <paramref name="configuredDir"/> is the
    /// INTERVIEW_DATA_DIR value (env-file or environment); empty means unset.
    /// </summary>
    public static string DataDir(string? configuredDir = null)
    {
        configuredDir ??= Environment.GetEnvironmentVariable("INTERVIEW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDir))
            return Path.GetFullPath(configuredDir);

        // An EXISTING data folder wins, mirroring the env-file search: the
        // working directory (the repo root under run.cmd — the original's own
        // default of <repo>/data), then beside the exe (frozen/portable
        // installs). Requiring it to exist keeps a stray CWD from hijacking
        // where data lives.
        foreach (var candidate in ExistingDataCandidates())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(LocalDataDir(), "data");
    }

    private static IEnumerable<string> ExistingDataCandidates()
    {
        string? cwd = null;
        try
        {
            cwd = Environment.CurrentDirectory;
        }
        catch
        {
            // Inaccessible working directory — fall through to the exe dir.
        }

        if (!string.IsNullOrEmpty(cwd))
            yield return Path.Combine(cwd, "data");
        yield return Path.Combine(ExecutableDir(), "data");
    }

    /// <summary>Case-insensitive on Windows, exact elsewhere.</summary>
    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
