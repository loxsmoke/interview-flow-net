using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Core.State;

/// <summary>
/// Serialization settings matching the original's json.dumps(ensure_ascii=False,
/// indent=2): 2-space indent, LF newlines, non-ASCII (emoji, accents) written
/// literally. Source-generated for trim safety (openlogi-net convention).
/// </summary>
public static class StateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = StateJsonContext.Default,
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize with full ensure_ascii=False parity (emoji literal).</summary>
    public static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, typeof(T), Options);
        return UnescapeNonBmp(json);
    }

    /// <summary>
    /// .NET's text encoder always escapes characters outside the BMP — emoji
    /// become six-char surrogate-pair escapes (backslash-uD83D…) even in relaxed
    /// mode; Python's ensure_ascii=False writes them literally. Decode those back to
    /// literal characters so the files match the original's byte-level style.
    /// A backslash-parity scan distinguishes real escapes from literal
    /// backslash text; everything else passes through untouched.
    /// </summary>
    internal static string UnescapeNonBmp(string json)
    {
        if (!json.Contains("\\uD", StringComparison.OrdinalIgnoreCase))
            return json;

        var sb = new System.Text.StringBuilder(json.Length);
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Escaped backslash: copy both, so \\uD... is never misread.
            if (i + 1 < json.Length && json[i + 1] == '\\')
            {
                sb.Append("\\\\");
                i += 2;
                continue;
            }

            if (TryReadUnicodeEscape(json, i, out var high)
                && char.IsHighSurrogate(high)
                && TryReadUnicodeEscape(json, i + 6, out var low)
                && char.IsLowSurrogate(low))
            {
                sb.Append(high).Append(low);
                i += 12;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryReadUnicodeEscape(string s, int backslashIndex, out char value)
    {
        value = '\0';
        if (backslashIndex + 5 >= s.Length
            || s[backslashIndex] != '\\'
            || s[backslashIndex + 1] != 'u')
        {
            return false;
        }

        var hex = s.AsSpan(backslashIndex + 2, 4);
        if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
            return false;
        value = (char)code;
        return true;
    }
}

[JsonSerializable(typeof(InterviewState))]
[JsonSerializable(typeof(CustomAction))]
[JsonSerializable(typeof(StateFileEnvelope))]
[JsonSerializable(typeof(CustomActionsEnvelope))]
public sealed partial class StateJsonContext : JsonSerializerContext;

/// <summary>{ "version": 1, "states": { id: InterviewState } } wire envelope.</summary>
public sealed class StateFileEnvelope
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("states")] public Dictionary<string, InterviewState> States { get; set; } = [];
}

/// <summary>{ "version": 1, "actions": [CustomAction] } wire envelope.</summary>
public sealed class CustomActionsEnvelope
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("actions")] public List<CustomAction> Actions { get; set; } = [];
}

/// <summary>Thrown when a data file declares a schema newer than this app knows.</summary>
public sealed class DataFileVersionException(string path, int version)
    : Exception($"Data file '{path}' has schema version {version}, newer than this app supports (1). Refusing to load so nothing is overwritten.")
{
    public int Version { get; } = version;
}
