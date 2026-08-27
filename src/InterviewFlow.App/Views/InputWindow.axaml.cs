using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class InputWindow : Window
{
    public InputWindow() => InitializeComponent();

    public InputWindow(string title, string watermark) : this()
    {
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        var box = this.FindControl<TextBox>("ValueBox")!;
        box.Watermark = watermark;
        Opened += (_, _) => box.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSubmit(object? sender, RoutedEventArgs e) =>
        Close(this.FindControl<TextBox>("ValueBox")!.Text ?? "");

    /// <summary>Returns the entered text, or null on cancel.</summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string watermark = "")
        => await new InputWindow(title, watermark).ShowDialog<string?>(owner);
}
