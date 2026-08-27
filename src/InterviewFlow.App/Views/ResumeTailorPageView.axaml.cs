using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using InterviewFlow.App.ViewModels.Pages;
using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.App.Views;

public sealed partial class ResumeTailorPageView : UserControl
{
    /// <summary>§3.7 diff rows: deleted red-950/70, added green-950/70.</summary>
    public static readonly IValueConverter DiffBackground =
        new FuncValueConverter<DiffKind, IBrush?>(kind => kind switch
        {
            DiffKind.Deleted => Brush.Parse("#4C0519"),
            DiffKind.Added => Brush.Parse("#052E16"),
            _ => null,
        });

    /// <summary>Deleted red-300, added green-300, unchanged slate-400.</summary>
    public static readonly IValueConverter DiffForeground =
        new FuncValueConverter<DiffKind, IBrush>(kind => Brush.Parse(kind switch
        {
            DiffKind.Deleted => "#FCA5A5",
            DiffKind.Added => "#86EFAC",
            _ => "#94A3B8",
        }));

    private ResumeTailorPageViewModel? _subscribed;

    public ResumeTailorPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.ExportRequested -= OnExportRequested;
            _subscribed = DataContext as ResumeTailorPageViewModel;
            if (_subscribed is not null)
                _subscribed.ExportRequested += OnExportRequested;
        };
        // §3.7: stack vertically when the split area gets narrow.
        SizeChanged += (_, e) =>
        {
            if (this.FindControl<Grid>("SplitGrid") is not { } grid)
                return;
            var vertical = e.NewSize.Width < 640;
            if (vertical && grid.ColumnDefinitions.Count > 1)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions("*,8,*");
                Restack(grid, vertical: true);
            }
            else if (!vertical && grid.RowDefinitions.Count > 1)
            {
                grid.RowDefinitions = new RowDefinitions("*");
                grid.ColumnDefinitions = new ColumnDefinitions("*,8,*");
                Restack(grid, vertical: false);
            }
        };
    }

    private static void Restack(Grid grid, bool vertical)
    {
        var index = 0;
        foreach (var child in grid.Children)
        {
            if (vertical)
            {
                Grid.SetColumn(child, 0);
                Grid.SetRow(child, index);
                if (child is GridSplitter splitter)
                    splitter.ResizeDirection = GridResizeDirection.Rows;
            }
            else
            {
                Grid.SetRow(child, 0);
                Grid.SetColumn(child, index);
                if (child is GridSplitter splitter)
                    splitter.ResizeDirection = GridResizeDirection.Columns;
            }

            index++;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task<string?> OnExportRequested(string taggedText)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner
            || DataContext is not ResumeTailorPageViewModel vm
            || vm.Shell.CurrentState is not { } state)
        {
            return null;
        }

        return await Services.ResumeExportService.ExportAsync(owner, vm.Shell.Config, state, taggedText);
    }
}
