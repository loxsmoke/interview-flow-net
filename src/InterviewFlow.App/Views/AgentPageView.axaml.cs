using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class AgentPageView : UserControl
{
    public AgentPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
