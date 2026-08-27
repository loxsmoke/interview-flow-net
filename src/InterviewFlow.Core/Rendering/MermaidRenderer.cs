using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using InterviewFlow.Core.Logging;
using Jint;

namespace InterviewFlow.Core.Rendering;

/// <summary>
/// Runs mermaid inside a Jint engine over the MermaidDom.js shim (ADR-001b
/// candidate 1) and returns SVG markup. The engine is built once and reused —
/// bundle evaluation costs ~700 ms, per-diagram renders are cheap.
/// Callers pass already-normalized source (MermaidNormalizer) and fall back to
/// showing the source when this returns null.
/// </summary>
public sealed partial class MermaidRenderer : IDisposable
{
    /// <summary>Measures a single line of text; wired to real font metrics by the UI host.</summary>
    public delegate double MeasureTextWidth(string text, double fontSize, string fontFamily, bool bold);

    private readonly Lock _sync = new();
    private readonly string _bundleSource;
    private readonly MeasureTextWidth? _measure;
    private Engine? _engine;
    private bool _engineBroken;
    private int _renderCounter;

    public MermaidRenderer(string bundleSource, MeasureTextWidth? measure = null)
    {
        _bundleSource = bundleSource;
        _measure = measure;
    }

    /// <summary>Loads the mermaid bundle from a file, or returns null when absent.</summary>
    public static MermaidRenderer? TryCreateFromFile(string bundlePath, MeasureTextWidth? measure = null)
    {
        try
        {
            return File.Exists(bundlePath)
                ? new MermaidRenderer(File.ReadAllText(bundlePath), measure)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders normalized mermaid source to SVG. Returns null (with an error
    /// message) on any failure — the caller shows the source instead, matching
    /// the original app's failure mode.
    /// </summary>
    public string? TryRender(string normalizedSource, out string? error)
    {
        lock (_sync)
        {
            error = null;
            if (_engineBroken)
            {
                error = "mermaid engine unavailable";
                return null;
            }

            try
            {
                var engine = _engine ??= CreateEngine();
                var id = $"if_mmd_{++_renderCounter}";
                engine.SetValue("__mmd_src", normalizedSource);
                engine.SetValue("__mmd_id", id);
                engine.Execute(
                    """
                    globalThis.__mmd_svg = null;
                    globalThis.__mmd_err = null;
                    globalThis.__mmd_state = 'pending';
                    mermaid.render(__mmd_id, __mmd_src).then(
                        function (r) {
                            globalThis.__mmd_state = 'resolved';
                            try { globalThis.__mmd_svg = r && r.svg ? String(r.svg) : ''; }
                            catch (e) { globalThis.__mmd_err = 'result read failed: ' + String(e); }
                        },
                        function (e) {
                            globalThis.__mmd_state = 'rejected';
                            globalThis.__mmd_err = String(e && e.message ? e.message : e);
                        });
                    """);
                engine.Advanced.ProcessTasks();

                var err = engine.Evaluate("__mmd_err === null ? '' : __mmd_err").AsString();
                if (err.Length > 0)
                {
                    error = err;
                    return null;
                }

                var svg = engine.Evaluate("__mmd_svg === null ? '' : __mmd_svg").AsString();
                if (svg.Length == 0)
                {
                    var state = engine.Evaluate("__mmd_state").AsString();
                    error = $"mermaid produced no output (promise {state})";
                    return null;
                }

                return PostProcessSvg(svg);
            }
            catch (Exception ex)
            {
                // A failed render can leave arbitrary state in the shared DOM;
                // rebuild the engine on next use rather than risk corruption.
                error = ex.Message;
                _engine?.Dispose();
                _engine = null;
                DiagnosticLog.Warn("mermaid", $"render failed: {ex.Message}");
                return null;
            }
        }
    }

    private Engine CreateEngine()
    {
        try
        {
            var engine = new Engine(options => options
                .LimitMemory(1024L * 1024 * 1024)
                .TimeoutInterval(TimeSpan.FromSeconds(30)));

            if (_measure is not null)
            {
                var measure = _measure;
                engine.SetValue("__host_measure_width",
                    (string text, double fontSize, string family, bool bold) =>
                        measure(text, fontSize, family, bold));
            }

            engine.Execute(LoadDomShim());
            engine.Execute(_bundleSource);
            // htmlLabels:false everywhere — the browser default (HTML in
            // <foreignObject>) is invisible to the Svg.Skia renderer; native
            // <text> labels are required for the port.
            // securityLevel 'loose' (the original uses 'antiscript'): DOMPurify's
            // final sweep dismantles whole diagrams under the shim DOM when labels
            // contain <br/>, and it defends against script execution — a surface
            // that does not exist here, since output goes to a static SVG
            // rasterizer, never a browser.
            engine.Execute(
                """
                mermaid.initialize({
                    startOnLoad: false,
                    theme: 'dark',
                    securityLevel: 'loose',
                    suppressErrorRendering: true,
                    fontFamily: 'Inter, sans-serif',
                    htmlLabels: false,
                    flowchart: { htmlLabels: false },
                    class: { htmlLabels: false },
                    state: { htmlLabels: false },
                    er: { htmlLabels: false }
                });
                """);
            engine.Advanced.ProcessTasks();
            return engine;
        }
        catch
        {
            // Bundle or shim failed to evaluate — don't retry every diagram.
            _engineBroken = true;
            throw;
        }
    }

    [GeneratedRegex("\\s+xmlns=\"http://www\\.w3\\.org/1999/xhtml\"")]
    private static partial Regex XhtmlXmlns();

    [GeneratedRegex("<(text|tspan)(?![^>]*\\bfill=)")]
    private static partial Regex TextWithoutFill();

    /// <summary>
    /// Fixes up mermaid's sanitized output for a strict SVG renderer:
    /// 1. The sanitize pass stamps xmlns="…xhtml" on inner elements, which makes an
    ///    XML parser treat the whole diagram body as foreign (invisible) content —
    ///    strip them (HTML labels are disabled, so XHTML never belongs here).
    /// 2. §4.4 step 9: force light text fill + Inter on text/tspan so labels don't
    ///    depend on the renderer honoring the embedded CSS stylesheet.
    /// </summary>
    internal static string PostProcessSvg(string svg)
    {
        svg = XhtmlXmlns().Replace(svg, "");
        svg = TextWithoutFill().Replace(svg, "<$1 fill=\"#E2E8F0\" font-family=\"Inter, sans-serif\"");
        svg = FlattenNestedTspans(svg);
        return svg;
    }

    /// <summary>
    /// Rewrites label text for Svg.Skia's limited SVG-text support:
    /// 1. Nested tspans are skipped entirely (labels vanish), and loose space
    ///    text-nodes between per-word tspans are dropped (words run together) —
    ///    so each line-tspan's content is merged into one plain string. Per-word
    ///    bold/italic runs inside a label are lost; acceptable for diagram labels.
    /// 2. CSS `text-anchor: middle` from mermaid's stylesheet is not applied —
    ///    text starts at the box center and overflows right. Set it as an explicit
    ///    attribute on label texts (cluster titles excluded: mermaid centers those
    ///    manually with a start anchor).
    /// </summary>
    private static string FlattenNestedTspans(string svg)
    {
        try
        {
            var doc = XDocument.Parse(svg);
            XNamespace ns = "http://www.w3.org/2000/svg";

            foreach (var text in doc.Descendants(ns + "text").ToList())
            {
                foreach (var line in text.Elements(ns + "tspan").ToList())
                {
                    if (!line.Elements().Any())
                        continue;
                    var content = line.Value;
                    line.RemoveNodes();
                    line.Add(new XText(content));
                }

                if (text.Attribute("text-anchor") is null)
                    text.SetAttributeValue("text-anchor", "middle");
            }

            PositionClusterTitles(doc, ns);

            return doc.Root!.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            // Unparseable output — return as-is; the caller's fallback handles it.
            return svg;
        }
    }

    /// <summary>
    /// Mermaid's own cluster-title placement doesn't run under the shim DOM (the
    /// cluster-label group ends up with no transform, so titles land at the
    /// diagram origin, overlapping other content). Position each title
    /// deterministically instead: horizontally centered over the cluster rect,
    /// just inside its top edge — where mermaid puts it in a browser.
    /// </summary>
    private static void PositionClusterTitles(XDocument doc, XNamespace ns)
    {
        // Mermaid emits TWO g.cluster elements per subgraph (same id, same
        // coordinate space): one carries the title text (never positioned under
        // the shim), the other carries the sized rect plus an empty label group.
        // Match them by id and put the transform on the label that has the text.
        var byId = doc.Descendants(ns + "g")
            .Where(g => (g.Attribute("class")?.Value ?? "").Split(' ').Contains("cluster"))
            .GroupBy(g => g.Attribute("id")?.Value ?? "");

        foreach (var group in byId)
        {
            var rect = group
                .SelectMany(g => g.Descendants(ns + "rect"))
                .FirstOrDefault(r => r.Attribute("width") is not null);
            var label = group
                .SelectMany(g => g.Descendants(ns + "g"))
                .FirstOrDefault(g => (g.Attribute("class")?.Value ?? "").Contains("cluster-label")
                    && g.Descendants(ns + "text").Any(t => !string.IsNullOrWhiteSpace(t.Value)));
            if (rect is null || label is null)
                continue;

            if (!double.TryParse(rect.Attribute("x")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(rect.Attribute("y")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(rect.Attribute("width")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var w))
            {
                continue;
            }

            var cx = (x + w / 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var ty = (y + 4).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            // Reparent the title next to (after) the rect: it originally lives in a
            // group painted BEFORE the rect, so the opaque cluster fill covers it.
            var rectCluster = rect.Ancestors(ns + "g")
                .First(g => (g.Attribute("class")?.Value ?? "").Split(' ').Contains("cluster"));
            label.Remove();
            rectCluster.Add(label);
            label.SetAttributeValue("transform", $"translate({cx}, {ty})");
        }
    }

    private static string LoadDomShim()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("InterviewFlow.Core.Rendering.MermaidDom.js")
            ?? throw new InvalidOperationException("MermaidDom.js embedded resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
