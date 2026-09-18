using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace InterviewFlow.App.Views;

/// <summary>
/// Keeps every scroll position where the user left it across a window
/// deactivation. Activating a window re-focuses the element that had focus
/// (<c>FocusManager.SetFocusScope</c>), and a scroll viewer brings a newly
/// focused descendant into view — so switching back from another app scrolled
/// a report to the paragraph the user had last clicked, usually the top.
/// <see cref="WindowBase.Activated"/> fires before that refocus, which makes
/// it the moment to snapshot the offsets; they are put back on the next
/// dispatcher turn, ahead of rendering, once the refocus has done its scroll.
/// </summary>
public static class ScrollOffsets
{
    /// <summary>Every scroll viewer under <paramref name="root"/> with its current offset.</summary>
    public static IReadOnlyList<(ScrollViewer Viewer, Vector Offset)> Capture(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().Select(v => (v, v.Offset)).ToList();

    /// <summary>Puts back the offsets that moved since <paramref name="snapshot"/> was taken.</summary>
    public static void Restore(IReadOnlyList<(ScrollViewer Viewer, Vector Offset)> snapshot)
    {
        foreach (var (viewer, offset) in snapshot)
        {
            if (viewer.Offset != offset)
                viewer.Offset = offset;
        }
    }

    /// <summary>Wires a window so activation leaves its scroll positions alone.</summary>
    public static void KeepAcrossActivation(Window window)
    {
        IReadOnlyList<(ScrollViewer, Vector)>? pending = null;
        window.Activated += (_, _) =>
        {
            // Windows raises Activated twice on a restore from the taskbar; a
            // second notice while the first restore is queued must not replace
            // the pre-refocus snapshot with one taken after the jump.
            if (pending is not null)
                return;
            pending = Capture(window);
            // Normal runs ahead of Render: the offsets are back before the
            // frame that would have shown the jump.
            Dispatcher.UIThread.Post(() =>
            {
                var snapshot = pending;
                pending = null;
                if (snapshot is not null)
                    Restore(snapshot);
            }, DispatcherPriority.Normal);
        };
    }
}
