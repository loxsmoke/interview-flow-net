using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace InterviewFlow.App.Controls;

/// <summary>
/// Per-section query cost chip (docs/03-ui-spec.md §3.4): "$0.42 query cost"
/// or "No query cost", with model/duration/run-time detail in the tooltip.
/// Agent screens (M5) bind CostUsd/Detail per section; Debrief reuses it for
/// the total with its own text.
/// </summary>
public sealed class CostBadge : Border
{
    public static readonly StyledProperty<double> CostUsdProperty =
        AvaloniaProperty.Register<CostBadge, double>(nameof(CostUsd));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<CostBadge, string?>(nameof(Detail));

    private readonly TextBlock _text;

    public double CostUsd
    {
        get => GetValue(CostUsdProperty);
        set => SetValue(CostUsdProperty, value);
    }

    /// <summary>Tooltip body: model, duration, local run time.</summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public CostBadge()
    {
        Background = Brush.Parse("#1E293B");
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(8, 3);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _text = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#94A3B8") };
        Child = _text;
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CostUsdProperty || change.Property == DetailProperty)
            Render();
    }

    private void Render()
    {
        _text.Text = CostUsd > 0
            ? $"${CostUsd:0.00} query cost"
            : "No query cost";
        ToolTip.SetTip(this, string.IsNullOrEmpty(Detail) ? null : Detail);
    }
}
