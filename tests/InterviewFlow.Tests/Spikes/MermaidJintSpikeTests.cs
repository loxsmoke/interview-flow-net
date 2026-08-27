using InterviewFlow.Core.Markdown;
using InterviewFlow.Core.Rendering;

namespace InterviewFlow.Tests.Spikes;

/// <summary>
/// ADR-001b spike harness: drives <see cref="MermaidRenderer"/> (Jint + the
/// MermaidDom.js shim) against real mermaid input shapes. Run tools/get-mermaid.ps1
/// first to fetch the bundle; without it the facts no-op and report SKIPPED.
/// These record findings for the ADR — geometry is approximate until the UI host
/// wires real text measurement.
/// </summary>
public sealed class MermaidJintSpikeTests(ITestOutputHelper output) : IDisposable
{
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

    private bool Skip()
    {
        if (_renderer is not null)
            return false;
        output.WriteLine("SKIPPED: spikes/assets/mermaid.min.js not found — run tools/get-mermaid.ps1.");
        return true;
    }

    [Fact]
    public void Renders_minimal_flowchart_to_svg()
    {
        if (Skip())
            return;

        var svg = _renderer!.TryRender("graph LR\nA-->B", out var error);
        output.WriteLine(svg is null
            ? $"FINDING: no SVG — {error}"
            : $"FINDING: SVG produced, {svg.Length} chars. Head: {svg[..Math.Min(160, svg.Length)]}");

        Assert.NotNull(svg);
        Assert.Contains("<svg", svg);
    }

    [Fact]
    public void Renders_golden_corpus_diagram_after_normalization()
    {
        if (Skip())
            return;

        // The golden corpus diagram exercises all five normalizations:
        // graph TD → LR, backtick label with bullets, unclosed subgraph.
        var raw = "graph TD\n    CEO --> CTO\n    CTO --> Platform[\"`- Runs the core API\n- Owns reliability`\"]\n    CTO --> DevEx\n    subgraph Product Org\n    PM --> Design\n";
        var normalized = MermaidNormalizer.Normalize(raw);
        var svg = _renderer!.TryRender(normalized, out var error);
        output.WriteLine(svg is null
            ? $"FINDING: no SVG — {error}"
            : $"FINDING: SVG produced, {svg.Length} chars");

        Assert.NotNull(svg);
    }

    [Fact]
    public void Second_render_reuses_engine()
    {
        if (Skip())
            return;

        var first = _renderer!.TryRender("graph LR\nA-->B", out _);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = _renderer.TryRender("graph LR\nX-->Y\nY-->Z", out var error);
        sw.Stop();
        output.WriteLine($"FINDING: second render {(second is null ? $"failed — {error}" : $"took {sw.ElapsedMilliseconds} ms")}");

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void Invalid_source_reports_error_not_crash()
    {
        if (Skip())
            return;

        var svg = _renderer!.TryRender("this is not mermaid at all {{{", out var error);
        output.WriteLine($"FINDING: invalid input → svg={(svg is null ? "null" : "produced?!")}, error={error}");
        Assert.Null(svg);
        Assert.False(string.IsNullOrEmpty(error));

        // The engine must survive a failure and render the next diagram.
        var recovered = _renderer.TryRender("graph LR\nA-->B", out var error2);
        output.WriteLine($"FINDING: post-failure render {(recovered is null ? $"failed — {error2}" : "recovered OK")}");
        Assert.NotNull(recovered);
    }
}
