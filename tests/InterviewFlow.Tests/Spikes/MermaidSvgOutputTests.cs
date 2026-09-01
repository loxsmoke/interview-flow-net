using System.Xml.Linq;
using InterviewFlow.Core.Rendering;

namespace InterviewFlow.Tests.Spikes;

/// <summary>
/// Regression guards for the mermaid SVG post-processing (ADR-001b). Two bugs made
/// diagrams invisible in the app and must never return: HTML-in-foreignObject
/// labels (Svg.Skia can't draw them → htmlLabels:false) and stray
/// xmlns="…xhtml" attributes stamped during sanitize (a strict XML parser then
/// treats the diagram body as foreign, unrendered content).
/// </summary>
public sealed class MermaidSvgOutputTests(ITestOutputHelper output) : IDisposable
{
    private const string SvgNs = "http://www.w3.org/2000/svg";

    private readonly MermaidRenderer? _renderer = CreateRenderer();

    private static MermaidRenderer? CreateRenderer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spikes", "assets", "mermaid.min.js");
            if (File.Exists(candidate))
                return MermaidRenderer.TryCreateFromFile(candidate);
            dir = dir.Parent;
        }

        return null;
    }

    public void Dispose() => _renderer?.Dispose();

    [Fact]
    public void Output_is_strict_svg_with_visible_native_text_labels()
    {
        if (_renderer is null)
        {
            output.WriteLine("SKIPPED: spikes/assets/mermaid.min.js not found — run tools/get-mermaid.ps1.");
            return;
        }

        var svg = _renderer.TryRender("graph LR\nA-->B", out var error);
        Assert.NotNull(svg);
        output.WriteLine($"SVG {svg!.Length} chars — {error ?? "no error"}");

        // Must parse as strict XML with everything in the SVG namespace.
        var doc = XDocument.Parse(svg);
        Assert.Equal(SvgNs, doc.Root!.Name.NamespaceName);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.NamespaceName != SvgNs);

        // Labels are native <text> (no foreignObject) with an explicit fill so they
        // stay visible even if the renderer ignores the embedded stylesheet.
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "foreignObject");
        var texts = doc.Descendants().Where(e => e.Name.LocalName == "text").ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.NotNull(t.Attribute("fill")));
    }

    [Fact]
    public void Rasterized_diagram_actually_draws_label_text()
    {
        if (_renderer is null)
        {
            output.WriteLine("SKIPPED: spikes/assets/mermaid.min.js not found — run tools/get-mermaid.ps1.");
            return;
        }

        var svg = _renderer.TryRender("graph LR\nA-->B", out _);
        Assert.NotNull(svg);

        // No nested tspans may survive post-processing (Svg.Skia skips them),
        // and rendering with vs without <text> must differ — otherwise labels
        // are invisible (the paint-order / nesting bugs this guards against).
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(svg!, "<tspan[^>]*>\\s*<tspan"));
        var withText = CountLightPixels(svg!);
        var withoutText = CountLightPixels(
            System.Text.RegularExpressions.Regex.Replace(svg!, "<text[\\s\\S]*?</text>", ""));
        output.WriteLine($"light pixels with text: {withText}, without: {withoutText}");
        Assert.True(withText > withoutText,
            $"text elements added no pixels (with={withText}, without={withoutText}) — labels are invisible");
    }

    private static int CountLightPixels(string svgText)
    {
        var svg = new Svg.Skia.SKSvg();
        var picture = svg.FromSvg(svgText);
        if (picture is null)
            return 0;
        var bounds = picture.CullRect;
        var w = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var h = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(SkiaSharp.SKColors.Black);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
        var count = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red > 180 && c.Green > 180 && c.Blue > 180)
                count++;
        }

        return count;
    }

    [Fact]
    public void PostProcess_strips_xhtml_xmlns_and_fills_text()
    {
        // Pure post-processor check — runs even without the mermaid bundle.
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\"><g xmlns=\"http://www.w3.org/1999/xhtml\"><text y=\"1\">A</text></g></svg>";
        var clean = MermaidRenderer.PostProcessSvg(dirty);

        Assert.DoesNotContain("1999/xhtml", clean);
        Assert.Contains("<text fill=\"#E2E8F0\"", clean);
        var doc = XDocument.Parse(clean);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.NamespaceName != SvgNs);
    }
}
