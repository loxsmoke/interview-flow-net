using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using InterviewFlow.App.Platform;

namespace InterviewFlow.App.Views;

public sealed partial class AboutPageView : UserControl
{
    public AboutPageView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.FindControl<StackPanel>("MacUpdatePanel")!.IsVisible = OperatingSystem.IsMacOS();
        this.FindControl<Button>("CheckUpdatesButton")!.IsEnabled = MacUpdater.IsAvailable;
        this.FindControl<TextBlock>("UpdateStatus")!.Text = MacUpdater.Status;
    }

    private void CheckUpdates_Click(object? sender, RoutedEventArgs e) => MacUpdater.CheckForUpdates();
}
