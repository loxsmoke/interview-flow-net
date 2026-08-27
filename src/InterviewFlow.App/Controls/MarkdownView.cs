using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InterviewFlow.Core.Markdown;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace InterviewFlow.App.Controls;

/// <summary>
/// Native markdown renderer implementing docs/04-markdown-rendering.md (ADR-001).
/// Pipeline: tag-emoji decoration → search-warning extraction → Markdig
/// (GFM tables + soft-break-as-hard-break, matching marked's breaks:true) →
/// Avalonia visual tree with the exact prose metrics from §4.2.
/// Mermaid blocks currently render as normalized source at 80% opacity —
/// the original's own failure mode — until ADR-001b lands a diagram renderer.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak() // marked { breaks: true }
        .Build();

    // §4.2 palette — mirrors Themes/Theme.axaml; kept as fields so the control
    // renders identically outside the app's resource scope (previews, tests).
    private static readonly IBrush ProseText = Brush.Parse("#CBD5E1");
    private static readonly IBrush MutedText = Brush.Parse("#94A3B8");
    private static readonly IBrush CodeBg = Brush.Parse("#1E293B");
    private static readonly IBrush CodeFg = Brush.Parse("#E2E8F0");
    private static readonly IBrush PreBg = Brush.Parse("#0F172A");
    private static readonly IBrush QuoteBar = Brush.Parse("#6366F1");
    private static readonly IBrush TableBorder = Brush.Parse("#334155");
    private static readonly IBrush TableHeaderBg = Brush.Parse("#1E293B");
    private static readonly IBrush WarnBg = Brush.Parse("#431407");
    private static readonly IBrush WarnBorder = Brush.Parse("#9A3412");
    private static readonly IBrush WarnFg = Brush.Parse("#FB923C");

    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Menlo,monospace");

    private const double BaseSize = 16.0;              // root font-size
    private const double BodyLineHeight = BaseSize * 1.7;

    public MarkdownView()
    {
        Orientation = Orientation.Vertical;
    }

    private DispatcherTimer? _rerenderTimer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            ScheduleRender();
    }

    /// <summary>
    /// Debounced re-render: streamed deltas and editor keystrokes update the
    /// Markdown property far faster than a full re-parse is worth. A short
    /// restartable delay coalesces bursts while staying invisible to a reader.
    /// </summary>
    private void ScheduleRender()
    {
        if (_rerenderTimer is null)
        {
            _rerenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _rerenderTimer.Tick += (_, _) =>
            {
                _rerenderTimer!.Stop();
                Render();
            };
        }

        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    private void Render()
    {
        Children.Clear();
        var source = Markdown;
        if (string.IsNullOrWhiteSpace(source))
            return;

        // §4.1 step 1 — emoji decoration, fence-aware (Core, unit-tested).
        source = TagEmojiDecorator.Decorate(source);

        // §4.5 — the one known raw-HTML block becomes a native banner.
        var (warning, markdown) = SearchWarning.Extract(source);
        if (warning is not null)
            Children.Add(BuildSearchWarning(warning));

        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in doc)
        {
            var control = RenderBlock(block, ProseText);
            if (control is not null)
                Children.Add(control);
        }
    }

    // ---------------------------------------------------------------- blocks

    private Control? RenderBlock(Block block, IBrush foreground) => block switch
    {
        HeadingBlock h => RenderHeading(h, foreground),
        ParagraphBlock p => RenderParagraph(p, foreground),
        ListBlock l => RenderList(l, foreground),
        QuoteBlock q => RenderQuote(q),
        FencedCodeBlock f when IsMermaid(f) => RenderMermaid(f),
        CodeBlock c => RenderCodeBlock(c),
        Table t => RenderTable(t, foreground),
        ThematicBreakBlock => new Border
        {
            Height = 1,
            Background = TableBorder,
            Margin = new Thickness(0, BaseSize, 0, BaseSize),
        },
        HtmlBlock html => RenderHtmlBlock(html),
        _ => null,
    };

    private Control RenderHeading(HeadingBlock h, IBrush foreground)
    {
        // §4.2: h1 1.5rem, h2 1.25rem, h3 1.1rem, h4+ 1rem; weight 600;
        // margins 1em top / 0.5em bottom in the element's own em.
        var size = h.Level switch
        {
            1 => 24.0,
            2 => 20.0,
            3 => 17.6,
            _ => BaseSize,
        };
        var tb = NewTextBlock(foreground);
        tb.FontSize = size;
        tb.FontWeight = FontWeight.SemiBold;
        tb.Margin = new Thickness(0, size, 0, size * 0.5);
        RenderInlines(h.Inline, tb, new InlineStyle(foreground));
        return tb;
    }

    private Control RenderParagraph(ParagraphBlock p, IBrush foreground)
    {
        var tb = NewTextBlock(foreground);
        tb.LineHeight = BodyLineHeight;
        tb.Margin = new Thickness(0, 0, 0, BaseSize * 0.75);
        RenderInlines(p.Inline, tb, new InlineStyle(foreground));
        return tb;
    }

    private Control RenderList(ListBlock list, IBrush foreground)
    {
        // §4.2: ul/ol margin-left 1.5em, margin-bottom 0.75em; li margin-bottom 0.25em.
        var panel = new StackPanel
        {
            Margin = new Thickness(BaseSize * 1.5, 0, 0, BaseSize * 0.75),
        };
        var index = 0;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
            index = start - 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            index++;
            var marker = list.IsOrdered ? $"{index}." : "•";
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(0, 0, 0, BaseSize * 0.25),
            };
            var markerText = new TextBlock
            {
                Text = marker,
                Foreground = foreground,
                FontSize = BaseSize,
                LineHeight = BodyLineHeight,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(markerText, 0);
            row.Children.Add(markerText);

            var content = new StackPanel();
            foreach (var child in item)
            {
                var rendered = RenderBlock(child, foreground);
                if (rendered is null)
                    continue;
                // A tight list item's paragraph should not add the full paragraph gap.
                if (child is ParagraphBlock && rendered is TextBlock inner)
                    inner.Margin = new Thickness(0);
                content.Children.Add(rendered);
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control RenderQuote(QuoteBlock quote)
    {
        // §4.2: 3px indigo left border, 1em left padding, muted foreground.
        var inner = new StackPanel();
        foreach (var child in quote)
        {
            var rendered = RenderBlock(child, MutedText);
            if (rendered is not null)
                inner.Children.Add(rendered);
        }

        return new Border
        {
            BorderBrush = QuoteBar,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(BaseSize, 0, 0, 0),
            Margin = new Thickness(0, BaseSize, 0, BaseSize),
            Child = inner,
        };
    }

    private Control RenderCodeBlock(CodeBlock code)
    {
        // §4.2: pre — slate-950 bg, 1em padding, 8px radius, horizontal scroll.
        var text = ExtractCode(code);
        var tb = new SelectableTextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = BaseSize * 0.9,
            Foreground = CodeFg,
        };
        return new Border
        {
            Background = PreBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(BaseSize),
            Margin = new Thickness(0, BaseSize, 0, BaseSize),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = tb,
            },
        };
    }

    private Control RenderMermaid(FencedCodeBlock fence)
    {
        var normalized = MermaidNormalizer.Normalize(ExtractCode(fence));

        // Cache hit renders synchronously; otherwise show the dimmed source as a
        // placeholder and swap the diagram in when the worker finishes — a warm
        // render costs ~0.4 s and must not stall the UI thread.
        if (Rendering.MermaidHost.TryGetCached(normalized, out var cachedSvg))
            return BuildMermaidControl(cachedSvg, normalized);

        var host = new ContentControl { Content = BuildMermaidPlaceholder(normalized) };
        _ = Task.Run(() =>
        {
            var svg = Rendering.MermaidHost.TryRender(normalized, out _);
            Dispatcher.UIThread.Post(() => host.Content = BuildMermaidControl(svg, normalized));
        });
        return host;
    }

    private Control BuildMermaidControl(string? svg, string normalized)
    {
        if (svg is not null)
        {
            var image = TryBuildSvgImage(svg);
            if (image is not null)
            {
                // §4.4 step 7: horizontally scrollable diagram container, 1em margins.
                return new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Margin = new Thickness(0, BaseSize, 0, BaseSize),
                    Content = image,
                };
            }
        }

        // §4.4 step 8 — failure mode: normalized source at 80% opacity.
        var block = RenderCodeBlock(new FencedCodeBlockShim(normalized));
        block.Opacity = 0.8;
        return block;
    }

    private Control BuildMermaidPlaceholder(string normalized)
    {
        var block = RenderCodeBlock(new FencedCodeBlockShim(normalized));
        block.Opacity = 0.5;
        return block;
    }

    private static Control? TryBuildSvgImage(string svg)
    {
        try
        {
            var source = Avalonia.Svg.Skia.SvgSource.LoadFromSvg(svg);
            var image = new Avalonia.Svg.Skia.SvgImage { Source = source };
            return new Image
            {
                Source = image,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        catch
        {
            // Malformed SVG from the approximate DOM — fall back to source display.
            return null;
        }
    }

    /// <summary>Carries pre-normalized mermaid text through RenderCodeBlock.</summary>
    private sealed class FencedCodeBlockShim(string text) : CodeBlock(null!)
    {
        public string Text { get; } = text;
    }

    private static string ExtractCode(LeafBlock code)
    {
        if (code is FencedCodeBlockShim shim)
            return shim.Text;
        var sb = new StringBuilder();
        var lines = code.Lines.Lines;
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i].Slice.ToString());
        }

        return sb.ToString();
    }

    private static bool IsMermaid(FencedCodeBlock f) =>
        string.Equals(f.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private Control RenderTable(Table table, IBrush foreground)
    {
        // §4.2: full-width, collapsed 1px slate-700 borders, header slate-800 semibold.
        // Collapse trick: outer border draws top/left, every cell draws right/bottom.
        var grid = new Grid();
        var columnCount = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var colIndex = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var tb = NewTextBlock(foreground);
                tb.FontSize = BaseSize;
                if (row.IsHeader)
                    tb.FontWeight = FontWeight.SemiBold;
                foreach (var para in cell.OfType<ParagraphBlock>())
                    RenderInlines(para.Inline, tb, new InlineStyle(foreground, Bold: row.IsHeader));

                var border = new Border
                {
                    BorderBrush = TableBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(BaseSize * 0.75, BaseSize * 0.5),
                    Background = row.IsHeader ? TableHeaderBg : null,
                    Child = tb,
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, colIndex);
                grid.Children.Add(border);
                colIndex++;
            }

            rowIndex++;
        }

        return new Border
        {
            BorderBrush = TableBorder,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, BaseSize, 0, BaseSize),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = grid,
            },
        };
    }

    private Control RenderHtmlBlock(HtmlBlock html)
    {
        // ADR-001: unknown raw HTML falls back to visible literal text.
        var raw = ExtractCode(html);
        var (warning, _) = SearchWarning.Extract(raw);
        if (warning is not null)
            return BuildSearchWarning(warning);

        var tb = NewTextBlock(MutedText);
        tb.Text = raw;
        tb.FontFamily = MonoFont;
        tb.FontSize = BaseSize * 0.9;
        tb.Margin = new Thickness(0, 0, 0, BaseSize * 0.75);
        return tb;
    }

    private Control BuildSearchWarning(SearchWarning warning)
    {
        // §4.5 metrics: #431407 bg, 1px #9a3412 border, 8px radius,
        // 0.75em/1em padding, #fb923c text, 0.95em size, 1.6 line-height.
        var tb = NewTextBlock(WarnFg);
        tb.FontSize = BaseSize * 0.95;
        tb.LineHeight = BaseSize * 0.95 * 1.6;
        tb.Inlines ??= [];
        tb.Inlines.Add(new Run("⚠️ "));
        tb.Inlines.Add(new Run(warning.Title) { FontWeight = FontWeight.SemiBold });
        tb.Inlines.Add(new Run(" " + warning.Body));
        return new Border
        {
            Background = WarnBg,
            BorderBrush = WarnBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(BaseSize, BaseSize * 0.75),
            Margin = new Thickness(0, 0, 0, BaseSize * 1.25),
            Child = tb,
        };
    }

    // --------------------------------------------------------------- inlines

    /// <summary>Inherited inline formatting while walking the Markdig inline tree.</summary>
    private readonly record struct InlineStyle(IBrush Foreground, bool Bold = false, bool Italic = false);

    private void RenderInlines(ContainerInline? container, SelectableTextBlock target, InlineStyle style)
    {
        if (container is null)
            return;
        target.Inlines ??= [];
        var links = new List<LinkRange>();
        var offset = 0;
        AppendInlines(container, target.Inlines, style, links, ref offset);
        if (links.Count > 0)
            AttachLinkHandling(target, links);
    }

    private sealed record LinkRange(int Start, int End, string Url);

    private void AppendInlines(ContainerInline container, InlineCollection sink, InlineStyle style,
        List<LinkRange> links, ref int offset)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    AddRun(sink, lit.Content.ToString(), style, ref offset);
                    break;

                case LineBreakInline:
                    // breaks:true — soft breaks were already promoted to hard breaks.
                    sink.Add(new LineBreak());
                    offset += 1;
                    break;

                case CodeInline code:
                    // §4.2 code span: slate chip with 0.15em/0.4em padding and 4px
                    // radius. A Run cannot carry padding, so the chip is an inline
                    // container. Trade-off: chip text is outside the surrounding
                    // SelectableTextBlock's selection (noted in docs).
                    var codeSize = BaseSize * 0.9;
                    var chip = new Border
                    {
                        Background = CodeBg,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(codeSize * 0.4, codeSize * 0.15),
                        Child = new TextBlock
                        {
                            Text = code.Content,
                            FontFamily = MonoFont,
                            FontSize = codeSize,
                            Foreground = CodeFg,
                        },
                    };
                    sink.Add(new InlineUIContainer
                    {
                        Child = chip,
                        BaselineAlignment = BaselineAlignment.Center,
                    });
                    offset += 1; // an embedded object occupies one text position
                    break;

                case EmphasisInline em:
                    var childStyle = em.DelimiterCount >= 2
                        ? style with { Bold = true }
                        : style with { Italic = true };
                    AppendInlines(em, sink, childStyle, links, ref offset);
                    break;

                case LinkInline { IsImage: true } img:
                    // Images don't occur in agent reports; render the alt text.
                    AppendInlines(img, sink, style, links, ref offset);
                    break;

                case LinkInline link:
                    var start = offset;
                    // Original prose has no explicit link styling (Tailwind preflight
                    // resets anchors to inherit) — links look like body text and are
                    // clickable. TODO(M3): verify against the running original.
                    AppendInlines(link, sink, style, links, ref offset);
                    if (!string.IsNullOrEmpty(link.Url))
                        links.Add(new LinkRange(start, offset, link.Url!));
                    break;

                case AutolinkInline auto:
                    var aStart = offset;
                    AddRun(sink, auto.Url, style, ref offset);
                    links.Add(new LinkRange(aStart, offset, auto.Url));
                    break;

                case HtmlInline htmlInline:
                    // <br> variants become line breaks; other inline HTML shows literally.
                    if (htmlInline.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                    {
                        sink.Add(new LineBreak());
                        offset += 1;
                    }
                    else
                    {
                        AddRun(sink, htmlInline.Tag, style, ref offset);
                    }

                    break;

                case ContainerInline nested:
                    AppendInlines(nested, sink, style, links, ref offset);
                    break;
            }
        }
    }

    private void AddRun(InlineCollection sink, string text, InlineStyle style, ref int offset)
    {
        if (text.Length == 0)
            return;
        var run = new Run(text) { Foreground = style.Foreground };
        if (style.Bold)
            run.FontWeight = FontWeight.SemiBold; // §4.2: strong is 600, not 700
        if (style.Italic)
            run.FontStyle = FontStyle.Italic;
        sink.Add(run);
        offset += text.Length;
    }

    // ----------------------------------------------------------------- links

    private static void AttachLinkHandling(SelectableTextBlock tb, List<LinkRange> links)
    {
        // §4.3: event delegation with character hit-testing — inlines have no
        // input events, so we map pointer position back to a text offset.
        tb.PointerMoved += (_, e) =>
        {
            var over = HitLink(tb, links, e.GetPosition(tb));
            tb.Cursor = over is not null
                ? new Cursor(StandardCursorType.Hand)
                : Cursor.Default;
        };
        tb.PointerReleased += (_, e) =>
        {
            var link = HitLink(tb, links, e.GetPosition(tb));
            if (link is not null)
                OpenExternal(link.Url);
        };
    }

    private static LinkRange? HitLink(SelectableTextBlock tb, List<LinkRange> links, Point point)
    {
        var hit = tb.TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
            return null;
        var pos = hit.TextPosition;
        return links.FirstOrDefault(l => pos >= l.Start && pos < l.End);
    }

    // §4.3: only http/https ever leave the app; scheme filtering + platform
    // branches live in ShellOpen.
    private static void OpenExternal(string url) => Platform.ShellOpen.OpenUrl(url);

    // --------------------------------------------------------------- helpers

    private static SelectableTextBlock NewTextBlock(IBrush foreground) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = foreground,
        FontSize = BaseSize,
    };
}
