using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterviewFlow.App.Views;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Switching to another app and back scrolled a report to the top: window
/// activation re-focuses the last focused element, and the scroll viewer
/// brings it into view. The snapshot taken on activation is what puts the
/// offset back.
/// </summary>
public sealed class ScrollOffsetsTests
{
    private static (Window Window, ScrollViewer Viewer, SelectableTextBlock First) ScrolledReport()
    {
        var panel = new StackPanel();
        for (var i = 0; i < 80; i++)
        {
            panel.Children.Add(new SelectableTextBlock
            {
                Text = $"Paragraph {i}: the quick brown fox jumps over the lazy dog.",
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var viewer = new ScrollViewer { Content = panel };
        // Something focusable outside the viewer, standing in for the sidebar.
        var elsewhere = new Button { Content = "Elsewhere", Name = "Elsewhere" };
        DockPanel.SetDock(elsewhere, Dock.Top);
        var window = new Window { Content = new DockPanel { Children = { elsewhere, viewer } }, Width = 600, Height = 400 };
        window.Show();
        window.UpdateLayout();

        // The user clicked the second paragraph, then wheeled well past it.
        var first = (SelectableTextBlock)panel.Children[1];
        first.Focus();
        viewer.Offset = new Vector(0, 600);
        window.UpdateLayout();
        Assert.Equal(600, viewer.Offset.Y);
        return (window, viewer, first);
    }

    /// <summary>The mechanism itself: re-focusing the clicked paragraph scrolls back to it.</summary>
    [AvaloniaFact]
    public void Refocusing_the_last_focused_paragraph_scrolls_it_into_view()
    {
        var (window, viewer, first) = ScrolledReport();

        Refocus(window, first);

        Assert.True(viewer.Offset.Y < 100, $"expected the jump to the top, offset is {viewer.Offset.Y}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_snapshot_taken_before_the_refocus_puts_the_offset_back()
    {
        var (window, viewer, first) = ScrolledReport();

        var snapshot = ScrollOffsets.Capture(window);
        Refocus(window, first);
        Assert.NotEqual(600, viewer.Offset.Y);

        ScrollOffsets.Restore(snapshot);
        window.UpdateLayout();

        Assert.Equal(600, viewer.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void Restore_leaves_untouched_viewers_alone()
    {
        var (window, viewer, _) = ScrolledReport();
        var snapshot = ScrollOffsets.Capture(window);

        viewer.Offset = new Vector(0, 300); // the user scrolled after the snapshot; nothing refocused
        ScrollOffsets.Restore(ScrollOffsets.Capture(window));
        Assert.Equal(300, viewer.Offset.Y);

        ScrollOffsets.Restore(snapshot);
        Assert.Equal(600, viewer.Offset.Y);
        window.Close();
    }

    /// <summary>What FocusManager.SetFocusScope does on activation: focus the remembered element again.</summary>
    private static void Refocus(Window window, IInputElement element)
    {
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Elsewhere").Focus();
        element.Focus();
        window.UpdateLayout();
    }
}
