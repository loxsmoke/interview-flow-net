using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterviewFlow.App.ViewModels.Pages;

namespace InterviewFlow.App.Views;

public sealed partial class ConfigPageView : UserControl
{
    /// <summary>Configured pill: green when a key is present, slate otherwise.</summary>
    public static readonly IValueConverter PillBackground =
        new FuncValueConverter<bool, IBrush>(ok => Brush.Parse(ok ? "#14532D" : "#1E293B"));

    public static readonly IValueConverter PillForeground =
        new FuncValueConverter<bool, IBrush>(ok => Brush.Parse(ok ? "#86EFAC" : "#94A3B8"));

    public static readonly IValueConverter PillText =
        new FuncValueConverter<bool, string>(ok => ok ? "Configured" : "Not set");

    /// <summary>Eye toggle: '\0' reveals the key, '•' masks it.</summary>
    public static readonly IValueConverter MaskChar =
        new FuncValueConverter<bool, char>(show => show ? '\0' : '•');

    private ConfigPageViewModel? _subscribed;

    public ConfigPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
            {
                _subscribed.ConfirmRequested -= OnConfirmRequested;
                _subscribed.FolderPickRequested -= OnFolderPickRequested;
                _subscribed.EnvFilePickRequested -= OnEnvFilePickRequested;
                _subscribed.PropertyChanged -= OnVmPropertyChanged;
            }

            _subscribed = DataContext as ConfigPageViewModel;
            if (_subscribed is not null)
            {
                _subscribed.ConfirmRequested += OnConfirmRequested;
                _subscribed.FolderPickRequested += OnFolderPickRequested;
                _subscribed.EnvFilePickRequested += OnEnvFilePickRequested;
                _subscribed.PropertyChanged += OnVmPropertyChanged;
                UpdateCtxLabel();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigPageViewModel.NumCtxIndex))
            UpdateCtxLabel();
    }

    private void UpdateCtxLabel()
    {
        if (_subscribed is null || this.FindControl<TextBlock>("CtxLabel") is not { } label)
            return;
        var index = Math.Clamp(_subscribed.NumCtxIndex, 0, _subscribed.NumCtxLabels.Count - 1);
        label.Text = _subscribed.NumCtxLabels[index];
    }

    private void OnProviderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string provider } && DataContext is ConfigPageViewModel vm)
            vm.ActiveProvider = provider;
    }

    private void OnToggleAnthropicKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigPageViewModel vm)
            vm.ShowAnthropicKey = !vm.ShowAnthropicKey;
    }

    private void OnToggleOpenAiKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigPageViewModel vm)
            vm.ShowOpenAiKey = !vm.ShowOpenAiKey;
    }

    private void OnToggleGeminiKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigPageViewModel vm)
            vm.ShowGeminiKey = !vm.ShowGeminiKey;
    }

    private async void OnConfirmRequested(string title, string message, Action onConfirm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var confirmText = title switch
        {
            _ when title.StartsWith("Import", StringComparison.Ordinal) => "Import",
            _ when title.StartsWith("Use the data", StringComparison.Ordinal) => "Use it",
            _ => "Move data",
        };
        if (await ConfirmWindow.ShowAsync(owner, title, message, confirmText))
            onConfirm();
    }

    private async Task<string?> OnEnvFilePickRequested()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings (.env)",
            AllowMultiple = false,
            // .env has no extension, so allow any file and let Core validate it.
            FileTypeFilter =
            [
                new FilePickerFileType("Settings files") { Patterns = ["*.env", ".env", "*.env.*"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> OnFolderPickRequested()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose data folder",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
