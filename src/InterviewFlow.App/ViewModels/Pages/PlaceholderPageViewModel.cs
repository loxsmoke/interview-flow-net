using CommunityToolkit.Mvvm.ComponentModel;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Stand-in for screens that land in later milestones (agent screens M5,
/// resume M6, tailor/chats M7, config M8) so navigation is complete now.
/// </summary>
public sealed class PlaceholderPageViewModel(string icon, string title, string description, string milestone)
    : ObservableObject
{
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Milestone { get; } = milestone;
}
