using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.App.ViewModels;

/// <summary>One rendered chat bubble (§3.6 styling).</summary>
public sealed class ChatBubbleViewModel(bool isUser, string text, string caption)
{
    public bool IsUser { get; } = isUser;
    public bool IsAssistant => !IsUser;
    public string Text { get; } = text;
    public string Caption { get; } = caption;
}

/// <summary>
/// Drives a multi-turn chat panel over a Core ChatSessionBase: send/receive,
/// busy state, error surfacing, and bubble construction. Shared by Mock
/// Interview (§3.6) and the Resume Coach (§3.7).
/// </summary>
public sealed partial class ChatViewModel(
    Func<CancellationToken, Task<ChatSessionBase>> sessionFactory,
    string userCaption,
    string assistantCaption) : ObservableObject
{
    private ChatSessionBase? _session;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChatBubbleViewModel> Bubbles { get; } = [];

    [ObservableProperty] private string _draft = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isComplete;
    [ObservableProperty] private bool _isStarted;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>Composer hidden once the session ends (§3.6 END_OF_INTERVIEW).</summary>
    public bool CanSend => !IsBusy && !IsComplete;

    /// <summary>Raised after each turn so the view can scroll to the newest bubble.</summary>
    public event Action? BubbleAdded;

    /// <summary>Raised when a session finishes (mock interview completion).</summary>
    public event Action<ChatSessionBase, string>? SessionCompleted;

    [RelayCommand]
    public async Task StartAsync()
    {
        Bubbles.Clear();
        ErrorMessage = "";
        IsComplete = false;
        IsBusy = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            _session = await sessionFactory(_cts.Token);
            var opening = await _session.StartAsync(_cts.Token);
            AddBubble(isUser: false, opening);
            IsStarted = true;
        }
        catch (OperationCanceledException)
        {
            // Page swapped away mid-start.
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        var message = Draft.Trim();
        if (message.Length == 0 || _session is null || !CanSend)
            return;

        Draft = "";
        AddBubble(isUser: true, message);
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var reply = await _session.RespondAsync(message, _cts?.Token ?? default);
            AddBubble(isUser: false, reply);
            if (_session is MockInterviewSession { IsComplete: true })
            {
                IsComplete = true;
                SessionCompleted?.Invoke(_session, reply);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddBubble(bool isUser, string text)
    {
        // The end token is a control signal, not content — hide it (§3.6).
        var display = text.Replace(MockInterviewSession.EndToken, "").TrimEnd();
        Bubbles.Add(new ChatBubbleViewModel(isUser, display, isUser ? userCaption : assistantCaption));
        BubbleAdded?.Invoke();
    }

    private static string Describe(Exception ex) => ex switch
    {
        Core.Providers.RateLimitException => "Rate limited by the AI provider. Wait a moment and try again.",
        HttpRequestException http => $"AI request failed: {http.Message}",
        _ => $"Chat failed: {ex.Message}",
    };

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }
}
