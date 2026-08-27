using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels;

namespace InterviewFlow.App.Controls;

/// <summary>
/// Code-behind owns the view-scoped concerns: clipboard copy and the
/// pin-to-bottom auto-scroll (pinned while at the bottom; scrolling up unpins,
/// returning to the bottom re-pins — §3.4).
/// </summary>
public sealed partial class LiveTracePanel : UserControl
{
    private bool _pinned = true;
    private double _lastExtentHeight;

    public LiveTracePanel() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnCopyUserPrompt(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LiveTraceViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.UserPrompt);
    }

    private void OnResponseScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;

        var extentGrew = scroll.Extent.Height > _lastExtentHeight;
        _lastExtentHeight = scroll.Extent.Height;

        if (extentGrew)
        {
            // Content growth: honor the pin.
            if (_pinned)
                scroll.ScrollToEnd();
            return;
        }

        // User-driven scroll: pin state follows whether they're at the bottom.
        var atBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        _pinned = atBottom;
    }
}
