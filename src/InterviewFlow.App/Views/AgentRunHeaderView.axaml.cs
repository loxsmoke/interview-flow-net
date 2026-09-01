using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels;

namespace InterviewFlow.App.Views;

public sealed partial class AgentRunHeaderView : UserControl
{
    private Flyout? _queueFlyout;

    public AgentRunHeaderView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opening the dropdown reseeds the ticks from the live queue.</summary>
    private void OnQueueFlyoutOpened(object? sender, EventArgs e)
    {
        _queueFlyout = sender as Flyout;
        (DataContext as AgentRunViewModel)?.SeedSelection();
    }

    /// <summary>Apply commits the pending selection and closes the dropdown.</summary>
    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as AgentRunViewModel)?.ApplySelection();
        _queueFlyout?.Hide();
    }
}
