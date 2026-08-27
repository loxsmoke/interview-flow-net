using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class PlaceholderPageView : UserControl
{
    public PlaceholderPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
