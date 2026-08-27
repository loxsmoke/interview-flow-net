using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class AboutPageView : UserControl
{
    public AboutPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
