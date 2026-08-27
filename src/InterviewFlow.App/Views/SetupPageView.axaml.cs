using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.State;

namespace InterviewFlow.App.Views;

public sealed partial class SetupPageView : UserControl
{
    private SetupPageViewModel? _subscribed;

    public SetupPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.ConfirmRequested -= OnConfirmRequested;
            _subscribed = DataContext as SetupPageViewModel;
            if (_subscribed is not null)
                _subscribed.ConfirmRequested += OnConfirmRequested;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnConfirmRequested(string title, string message, Action onConfirm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var confirmText = title.StartsWith("Delete", StringComparison.Ordinal) ? "Delete" : "Clone";
        if (await ConfirmWindow.ShowAsync(owner, title, message, confirmText))
            onConfirm();
    }
}

/// <summary>"N steps done · yyyy-MM-dd" row detail for saved applications.</summary>
public sealed class SummaryDetailConverter : IValueConverter
{
    public static readonly SummaryDetailConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StateSummary s ? SetupPageViewModel.SummaryDetail(s) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
