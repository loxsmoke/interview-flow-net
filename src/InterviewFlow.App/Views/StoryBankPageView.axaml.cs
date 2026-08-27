using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InterviewFlow.App.Views;

public sealed partial class StoryBankPageView : UserControl
{
    public StoryBankPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
