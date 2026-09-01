using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ClampToScreen();
    }

    /// <summary>
    /// The 1400x900 default is wider than a 1280x800 MacBook (and than plenty of
    /// laptop screens generally), which would push the right-hand content and the
    /// window controls off-screen. Shrink to the work area — never below the
    /// window's own minimum — and re-centre.
    /// </summary>
    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        // WorkingArea is in physical pixels; Width/Height are logical units.
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var availableWidth = screen.WorkingArea.Width / scaling;
        var availableHeight = screen.WorkingArea.Height / scaling;

        var width = Math.Max(MinWidth, Math.Min(Width, availableWidth));
        var height = Math.Max(MinHeight, Math.Min(Height, availableHeight));
        if (width == Width && height == Height)
            return;

        Width = width;
        Height = height;
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)((availableWidth - width) * scaling / 2),
            screen.WorkingArea.Y + (int)((availableHeight - height) * scaling / 2));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // F12: markdown/mermaid dev harness (kept per TODO §2 — replaces the
        // original's /mermaid-debug page).
        if (e.Key == Key.F12)
        {
            new DevMarkdownWindow().Show(this);
            e.Handled = true;
        }
    }
}
