using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels;

namespace InterviewFlow.App.Views;

public sealed partial class DevMarkdownWindow : Window
{
    public DevMarkdownWindow()
    {
        InitializeComponent();
        DataContext = new DevMarkdownViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
