using System.Collections.Concurrent;
using System.Reflection;

namespace InterviewFlow.Core.Prompts;

/// <summary>
/// Prompt templates, embedded verbatim from the original's app/prompts/*.md
/// (port of prompt_loader.py). Each file holds "## Prompt" / "## System Prompt"
/// sections whose text lives between 4-backtick fences.
/// </summary>
public static class PromptLoader
{
    private const string Fence = "````";
    private static readonly ConcurrentDictionary<string, string> Cache = [];

    /// <summary>Text of the ## Prompt section of Templates/&lt;name&gt;.md.</summary>
    public static string LoadPrompt(string name) => ExtractFence(Read(name), "Prompt");

    /// <summary>Text of the ## System Prompt section of Templates/&lt;name&gt;.md.</summary>
    public static string LoadSystemPrompt(string name) => ExtractFence(Read(name), "System Prompt");

    private static string Read(string name) => Cache.GetOrAdd(name, static n =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"InterviewFlow.Core.Prompts.Templates.{n}.md")
            ?? throw new InvalidOperationException($"Prompt template '{n}' is not embedded");
        using var reader = new StreamReader(stream);
        // Templates are shipped from a CRLF checkout but the fence scanner keys
        // on "\n"-adjacent fences, exactly like the Python original reading
        // LF files — normalize.
        return reader.ReadToEnd().Replace("\r\n", "\n");
    });

    /// <summary>Text between the 4-backtick fence after the ## section heading.</summary>
    internal static string ExtractFence(string content, string section)
    {
        var heading = $"## {section}";
        var sectionStart = content.IndexOf(heading, StringComparison.Ordinal);
        if (sectionStart < 0)
            throw new InvalidOperationException($"Section '{heading}' not found");
        var fenceOpen = content.IndexOf(Fence + "\n", sectionStart, StringComparison.Ordinal);
        if (fenceOpen < 0)
            throw new InvalidOperationException($"Opening fence after '{heading}' not found");
        var start = fenceOpen + Fence.Length + 1;
        var end = content.IndexOf("\n" + Fence, start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"Closing fence after '{heading}' not found");
        return content[start..end];
    }
}
