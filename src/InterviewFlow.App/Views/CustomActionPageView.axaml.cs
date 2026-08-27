using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels.Pages;

namespace InterviewFlow.App.Views;

public sealed partial class CustomActionPageView : UserControl
{
    private CustomActionPageViewModel? _subscribed;

    public CustomActionPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.ConfirmRequested -= OnConfirmRequested;
            _subscribed = DataContext as CustomActionPageViewModel;
            if (_subscribed is not null)
                _subscribed.ConfirmRequested += OnConfirmRequested;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnConfirmRequested(string title, string message, Action onConfirm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var confirmText = title.StartsWith("Delete", StringComparison.Ordinal) ? "Delete" : "Save anyway";
        if (await ConfirmWindow.ShowAsync(owner, title, message, confirmText))
            onConfirm();
    }

    private void OnInsertTagSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not string tag)
            return;
        (DataContext as CustomActionPageViewModel)?.InsertTagCommand.Execute(tag);
        combo.SelectedItem = null;
    }
}
