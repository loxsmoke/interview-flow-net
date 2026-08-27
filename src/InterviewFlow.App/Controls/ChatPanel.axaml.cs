using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using InterviewFlow.App.ViewModels;

namespace InterviewFlow.App.Controls;

/// <summary>
/// Chat transcript + composer. Code-behind owns the view-scoped concerns:
/// Enter-sends / Shift+Enter-newline, and scrolling to the newest bubble.
/// </summary>
public sealed partial class ChatPanel : UserControl
{
    /// <summary>true (user) → right, false (assistant) → left.</summary>
    public static readonly IValueConverter AlignConverter =
        new FuncValueConverter<bool, HorizontalAlignment>(isUser =>
            isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    /// <summary>§3.6: user #1e293b, assistant #0f172a.</summary>
    public static readonly IValueConverter BubbleBackground =
        new FuncValueConverter<bool, IBrush>(isUser =>
            Brush.Parse(isUser ? "#1E293B" : "#0F172A"));

    /// <summary>Assistant bubbles carry a 1px border; user bubbles don't.</summary>
    public static readonly IValueConverter BubbleBorder =
        new FuncValueConverter<bool, Thickness>(isUser => new Thickness(isUser ? 0 : 1));

    /// <summary>§3.6 radii: user 12 12 4 12, assistant 12 12 12 4.</summary>
    public static readonly IValueConverter BubbleRadius =
        new FuncValueConverter<bool, CornerRadius>(isUser =>
            isUser ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(12, 12, 12, 4));

    private ChatViewModel? _subscribed;

    public ChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.BubbleAdded -= ScrollToEnd;
            _subscribed = DataContext as ChatViewModel;
            if (_subscribed is not null)
                _subscribed.BubbleAdded += ScrollToEnd;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("Transcript")?.ScrollToEnd(),
            DispatcherPriority.Background);

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return; // Shift+Enter falls through to the TextBox as a newline
        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }
}
