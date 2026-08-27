using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
