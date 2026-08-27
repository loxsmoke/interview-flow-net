using InterviewFlow.Core.Rendering;
using SkiaSharp;

namespace InterviewFlow.App.Rendering;

/// <summary>
/// App-wide mermaid renderer singleton: locates the mermaid bundle, wires text
/// measurement into the Core renderer, and hands out SVG. Null renderer =
/// bundle not found — the MarkdownView falls back to showing diagram source.
///
/// Measurement deliberately uses SkiaSharp, NOT Avalonia's FormattedText:
/// the produced SVG is rasterized by Svg.Skia, which resolves fonts through
/// SkiaSharp's system font manager. Measuring with the same resolution
/// (including its fallback when Inter isn't installed) keeps layout metrics and
/// drawn glyph widths consistent — mismatches show up as overlapping boxes.
/// </summary>
public static class MermaidHost
{
    private static readonly Lazy<MermaidRenderer?> Instance = new(Create);

    // Rendering costs ~0.4 s per diagram; the spike editor re-parses on every
    // keystroke and streaming responses re-render repeatedly, so results are
    // memoized by normalized source.
    private static readonly Lock CacheSync = new();
    private static readonly Dictionary<string, string?> Cache = [];
    private const int CacheCap = 64;

    private static readonly SKTypeface Normal =
        SKTypeface.FromFamilyName("Inter") ?? SKTypeface.Default;

    private static readonly SKTypeface Bold =
        SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold) ?? SKTypeface.Default;

    /// <summary>Cache-only lookup — never triggers a render. Safe on the UI thread.</summary>
    public static bool TryGetCached(string normalizedSource, out string? svg)
    {
        lock (CacheSync)
            return Cache.TryGetValue(normalizedSource, out svg);
    }

    public static string? TryRender(string normalizedSource, out string? error)
    {
        lock (CacheSync)
        {
            if (Cache.TryGetValue(normalizedSource, out var cached))
            {
                error = cached is null ? "cached failure" : null;
                return cached;
            }
        }

        string? svg;
        var renderer = Instance.Value;
        if (renderer is null)
        {
            error = "mermaid bundle not found";
            svg = null;
        }
        else
        {
            svg = renderer.TryRender(normalizedSource, out error);
        }

        lock (CacheSync)
        {
            if (Cache.Count >= CacheCap)
                Cache.Clear(); // crude but sufficient: cap memory, keep the hot path
            Cache[normalizedSource] = svg;
        }

        return svg;
    }

    private static MermaidRenderer? Create()
    {
        var path = LocateBundle();
        return path is null ? null : MermaidRenderer.TryCreateFromFile(path, MeasureWidth);
    }

    private static string? LocateBundle()
    {
        // Packaged: beside the app. Dev: the spike download at the repo root.
        var beside = Path.Combine(AppContext.BaseDirectory, "Assets", "mermaid.min.js");
        if (File.Exists(beside))
            return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spikes", "assets", "mermaid.min.js");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static double MeasureWidth(string text, double fontSize, string fontFamily, bool bold)
    {
        using var font = new SKFont(bold ? Bold : Normal, (float)fontSize);
        return font.MeasureText(text);
    }
}
