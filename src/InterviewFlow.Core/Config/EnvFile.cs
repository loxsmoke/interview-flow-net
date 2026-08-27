using System.Text;

namespace InterviewFlow.Core.Config;

/// <summary>
/// The original's .env is both config input (python-dotenv) and output
/// (main.py's _update_env_file rewrites it in place). This codec ports both
/// behaviors exactly (ADR-002, Option A):
/// - Read: last KEY=value wins (dotenv), surrounding quotes stripped.
/// - Write: existing matching lines are replaced with bare KEY=value, every
///   other line (comments included) is kept verbatim, missing keys append at
///   the end, and the file is written with OS newlines plus a trailing newline
///   — byte-for-byte what the Python implementation produces.
/// </summary>
public sealed class EnvFile
{
    private readonly List<string> _lines;

    public string Path { get; }

    private EnvFile(string path, List<string> lines)
    {
        Path = path;
        _lines = lines;
    }

    /// <summary>Missing file loads as empty — first Save creates it.</summary>
    public static EnvFile Load(string path)
    {
        List<string> lines = [];
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            if (text.Length > 0)
                lines = [.. text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)];
            // splitlines() drops a trailing empty segment from the final newline.
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
        }

        return new EnvFile(path, lines);
    }

    /// <summary>dotenv-style lookup: last assignment wins; quotes stripped.</summary>
    public string? Get(string key)
    {
        string? value = null;
        foreach (var line in _lines)
        {
            var stripped = line.Trim();
            if (stripped.StartsWith('#'))
                continue;
            var eq = stripped.IndexOf('=');
            if (eq < 0)
                continue;
            if (stripped[..eq].Trim() != key)
                continue;
            value = Unquote(stripped[(eq + 1)..].Trim());
        }

        return value;
    }

    /// <summary>All keys currently present (for diagnostics/round-trip tests).</summary>
    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var line in _lines)
        {
            var stripped = line.Trim();
            if (stripped.StartsWith('#'))
                continue;
            var eq = stripped.IndexOf('=');
            if (eq < 0)
                continue;
            keys.Add(stripped[..eq].Trim());
        }

        return keys;
    }

    /// <summary>
    /// _update_env_file port: replace matching non-comment KEY= lines in place
    /// (the replaced line becomes bare KEY=value, as in the original), keep all
    /// other lines verbatim, append keys that weren't found.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string> updates)
    {
        var written = new HashSet<string>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var stripped = _lines[i].Trim();
            if (stripped.StartsWith('#') || !stripped.Contains('='))
                continue;
            var key = stripped[..stripped.IndexOf('=')].Trim();
            if (!updates.TryGetValue(key, out var value))
                continue;
            _lines[i] = $"{key}={value}";
            written.Add(key);
        }

        foreach (var (key, value) in updates)
        {
            if (!written.Contains(key))
                _lines.Add($"{key}={value}");
        }
    }

    /// <summary>
    /// Writes with OS newlines + trailing newline, matching Python's text-mode
    /// write ("\n".join(lines) + "\n" through newline translation).
    /// </summary>
    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var body = string.Join(Environment.NewLine, _lines) + Environment.NewLine;
        File.WriteAllText(Path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
