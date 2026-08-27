using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class MockInterviewPageView : UserControl
{
    public MockInterviewPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
