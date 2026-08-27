using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace InterviewFlow.App.Controls;

/// <summary>
/// Tagged-resume preview (exact metrics of taggedToHtml, docs/04 §4.6): renders
/// the [Tag]content DSL with the original's inline styles, plus the optional
/// name/contact header when ShowContact is on.
/// </summary>
public sealed class TaggedResumePreview : StackPanel
{
    public static readonly StyledProperty<string?> TaggedTextProperty =
        AvaloniaProperty.Register<TaggedResumePreview, string?>(nameof(TaggedText));

    public static readonly StyledProperty<bool> ShowContactProperty =
        AvaloniaProperty.Register<TaggedResumePreview, bool>(nameof(ShowContact));

    public static readonly StyledProperty<string> ContactNameProperty =
        AvaloniaProperty.Register<TaggedResumePreview, string>(nameof(ContactName), "");

    public static readonly StyledProperty<string> ContactInfoProperty =
        AvaloniaProperty.Register<TaggedResumePreview, string>(nameof(ContactInfo), "");

    public string? TaggedText
    {
        get => GetValue(TaggedTextProperty);
        set => SetValue(TaggedTextProperty, value);
    }

    public bool ShowContact
    {
        get => GetValue(ShowContactProperty);
        set => SetValue(ShowContactProperty, value);
    }

    public string ContactName
    {
        get => GetValue(ContactNameProperty);
        set => SetValue(ContactNameProperty, value);
    }

    public string ContactInfo
    {
        get => GetValue(ContactInfoProperty);
        set => SetValue(ContactInfoProperty, value);
    }

    private const double Base = 16.0; // 1em

    private static readonly IBrush Text = Brush.Parse("#E2E8F0");
    private static readonly IBrush Rule = Brush.Parse("#94A3B8");
    private static readonly IBrush ContactFg = Brush.Parse("#CBD5E1");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TaggedTextProperty || change.Property == ShowContactProperty
            || change.Property == ContactNameProperty || change.Property == ContactInfoProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        Children.Clear();

        if (ShowContact)
        {
            // name 1.4em/700/lh1.2 · 1px #94a3b8 rule · contact 0.78em/700/#cbd5e1
            Children.Add(new SelectableTextBlock
            {
                Text = ContactName.Length > 0 ? ContactName : "[Name from Configuration]",
                FontSize = Base * 1.4,
                FontWeight = FontWeight.Bold,
                LineHeight = Base * 1.4 * 1.2,
                Foreground = Text,
            });
            Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 2) });
            Children.Add(new SelectableTextBlock
            {
                Text = ContactInfo.Length > 0 ? ContactInfo : "[Contact from Configuration]",
                FontSize = Base * 0.78,
                FontWeight = FontWeight.Bold,
                Foreground = ContactFg,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        foreach (var rawLine in (TaggedText ?? "").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0)
                continue;

            var (tag, content) = SplitTag(line);
            Children.Add(tag switch
            {
                "section heading" => Block(content, weight: FontWeight.Bold,
                    margin: new Thickness(0, 12, 0, 4), bottomRule: true),
                "summary" => Block(content, margin: new Thickness(0, 0, 0, 4)),
                "job title" => Block(content, weight: FontWeight.Bold, margin: new Thickness(0, 8, 0, 1)),
                "job summary" => Block(content, margin: new Thickness(0, 0, 0, 2)),
                "job bullet" => BulletRow(content),
                "skill" => SkillBlock(content),
                "additional info" => Block(content, margin: new Thickness(0, 0, 0, 1), centered: true),
                _ => Block(content, margin: new Thickness(0, 0, 0, 2)),
            });
        }
    }

    private static (string Tag, string Content) SplitTag(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close > 0)
                return (trimmed[1..close].Trim().ToLowerInvariant(), trimmed[(close + 1)..]);
        }

        return ("", trimmed);
    }

    private static Control Block(string text, FontWeight weight = FontWeight.Normal,
        Thickness margin = default, bool bottomRule = false, bool centered = false)
    {
        var tb = new SelectableTextBlock
        {
            Text = text,
            FontSize = Base,
            FontWeight = weight,
            Foreground = Text,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
        };
        if (!bottomRule)
        {
            tb.Margin = margin;
            return tb;
        }

        return new Border
        {
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 1),
            Margin = margin,
            Child = tb,
        };
    }

    private static Control BulletRow(string text)
    {
        // flex row: gap 6, margin 0 0 1, padding-left 12, literal • span.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(12, 0, 0, 1),
        };
        var bullet = new TextBlock { Text = "•", FontSize = Base, Foreground = Text, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(bullet, 0);
        var content = new SelectableTextBlock
        {
            Text = text,
            FontSize = Base,
            Foreground = Text,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(content, 1);
        row.Children.Add(bullet);
        row.Children.Add(content);
        return row;
    }

    private static Control SkillBlock(string content)
    {
        // Bold up to and including the first colon.
        var tb = new SelectableTextBlock
        {
            FontSize = Base,
            Foreground = Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 1),
        };
        tb.Inlines ??= [];
        var colon = content.IndexOf(':');
        if (colon >= 0)
        {
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(content[..(colon + 1)]) { FontWeight = FontWeight.Bold });
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(content[(colon + 1)..]));
        }
        else
        {
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(content));
        }

        return tb;
    }
}
