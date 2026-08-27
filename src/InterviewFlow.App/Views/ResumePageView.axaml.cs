using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using InterviewFlow.App.ViewModels.Pages;

namespace InterviewFlow.App.Views;

public sealed partial class ResumePageView : UserControl
{
    private ResumePageViewModel? _subscribed;

    public ResumePageView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
            {
                _subscribed.ConfirmRequested -= OnConfirmRequested;
                _subscribed.InputRequested -= OnInputRequested;
                _subscribed.ExportRequested -= OnExportRequested;
            }

            _subscribed = DataContext as ResumePageViewModel;
            if (_subscribed is not null)
            {
                _subscribed.ConfirmRequested += OnConfirmRequested;
                _subscribed.InputRequested += OnInputRequested;
                _subscribed.ExportRequested += OnExportRequested;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnPickFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not ResumePageViewModel vm)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose resume file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Resume files")
                {
                    Patterns = ["*.pdf", "*.docx", "*.doc", "*.txt", "*.md", "*.rtf"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            vm.LoadFile(path);
    }

    // Avalonia 12 drag-drop: payload is an IDataTransfer, files via TryGetFiles().
    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ResumePageViewModel vm)
            return;
        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            vm.LoadFile(path);
    }

    private async void OnConfirmRequested(string title, string message, Action onConfirm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        if (await ConfirmWindow.ShowAsync(owner, title, message, "Delete"))
            onConfirm();
    }

    private async void OnInputRequested(string title, string watermark, Action<string> onSubmit)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var value = await InputWindow.ShowAsync(owner, title, watermark);
        if (value is not null)
            onSubmit(value);
    }

    private async Task<string?> OnExportRequested(string taggedText)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner
            || DataContext is not ResumePageViewModel vm
            || vm.Shell.CurrentState is not { } state)
        {
            return null;
        }

        return await Services.ResumeExportService.ExportAsync(owner, vm.Shell.Config, state, taggedText);
    }

    private void OnInsertTagSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not string entry)
            return;
        (DataContext as ResumePageViewModel)?.InsertTagCommand.Execute(entry);
        combo.SelectedItem = null;
    }
}
