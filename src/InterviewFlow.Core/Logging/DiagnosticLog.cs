using System.Text;

namespace InterviewFlow.Core.Logging;

/// <summary>
/// Static no-throw diagnostic log (openlogi-net pattern). Area-tagged lines go to
/// a session file under <see cref="Paths.LogsDir"/>. The file is created lazily on
/// first write, and <see cref="Suppressed"/> is checked before that lazy init so a
/// suppressed session (tests, design mode) never touches the filesystem.
/// </summary>
public static class DiagnosticLog
{
    private static readonly Lock Sync = new();
    private static StreamWriter? _writer;
    private static string? _path;

    /// <summary>Set true before any logging to keep the session file-free.</summary>
    public static bool Suppressed { get; set; }

    /// <summary>Full path of the current log file, or null if none was created.</summary>
    public static string? CurrentPath
    {
        get { lock (Sync) return _path; }
    }

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message, Exception? ex = null) =>
        Write("ERROR", area, ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");

    /// <summary>Flushes and closes the file. Safe to call multiple times.</summary>
    public static void Shutdown(string reason)
    {
        lock (Sync)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine($"{Timestamp()} [INFO] [app] shutdown: {reason}");
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // Logging must never take the app down.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private static void Write(string level, string area, string message)
    {
        if (Suppressed)
            return;
        lock (Sync)
        {
            try
            {
                _writer ??= Open();
                _writer?.WriteLine($"{Timestamp()} [{level}] [{area}] {message}");
                _writer?.Flush();
            }
            catch
            {
                // Disk full / locked / permissions — swallow; see class doc.
            }
        }
    }

    private static StreamWriter? Open()
    {
        try
        {
            var dir = Paths.LogsDir();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"interview-flow-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            return new StreamWriter(_path, append: false, Encoding.UTF8);
        }
        catch
        {
            return null;
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
