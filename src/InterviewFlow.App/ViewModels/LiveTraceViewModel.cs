using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Queue;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// State behind LiveTracePanel (docs/03-ui-spec.md §3.4): prompts, the growing
/// streamed response, web activity, and the rate-limit countdown. Pure event
/// consumer — feed it AgentEvents (from a queue subscription) via ApplyEvent;
/// the caller marshals to the UI thread.
/// </summary>
public sealed partial class LiveTraceViewModel : ObservableObject
{
    private readonly StringBuilder _response = new();

    [ObservableProperty] private string _systemPrompt = "";
    [ObservableProperty] private string _userPrompt = "";
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private int _rateLimitRemaining = -1;

    public ObservableCollection<WebActivityRow> WebActivity { get; } = [];

    public bool HasRateLimitCountdown => RateLimitRemaining >= 0;

    public string WebActivityHeader => $"Web Activity ({WebActivity.Count})";

    partial void OnRateLimitRemainingChanged(int value) => OnPropertyChanged(nameof(HasRateLimitCountdown));

    /// <summary>🔍 query rows and 🌐 fetch rows.</summary>
    public sealed record WebActivityRow(string Icon, string Text);

    public void ApplyEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case SendEvent send when send.Channel == "system":
                SystemPrompt = send.Text;
                break;
            case SendEvent send:
                UserPrompt = send.Text;
                break;
            case ReceiveEvent receive:
                _response.Append(receive.Text);
                ResponseText = _response.ToString();
                RateLimitRemaining = -1;
                break;
            case ToolUseEvent tool:
                WebActivity.Add(tool.Tool == "WebSearch"
                    ? new WebActivityRow("🔍", tool.Query)
                    : new WebActivityRow("🌐", tool.Title.Length > 0 ? tool.Title : tool.Url));
                OnPropertyChanged(nameof(WebActivityHeader));
                break;
            case RateLimitRetryEvent retry:
                RateLimitRemaining = retry.RemainingSeconds;
                break;
            case RateLimitResetEvent:
                // The provider restarts the stream from scratch — clear text so
                // the retried run doesn't append onto the aborted one.
                _response.Clear();
                ResponseText = "";
                RateLimitRemaining = -1;
                break;
            case QueueStatusEvent:
            case HeartbeatEvent:
                break;
        }
    }

    public void Reset()
    {
        _response.Clear();
        SystemPrompt = "";
        UserPrompt = "";
        ResponseText = "";
        RateLimitRemaining = -1;
        WebActivity.Clear();
        OnPropertyChanged(nameof(WebActivityHeader));
    }
}
