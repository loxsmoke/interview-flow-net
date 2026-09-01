using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterviewFlow.App.ViewModels.Pages;

namespace InterviewFlow.Tests.App;

/// <summary>
/// The SOURCE link on About (§3.10). The label and the URL are one value now:
/// they had drifted, with the text naming this port and the click opening the
/// Python original.
/// </summary>
public sealed class AboutLinkTests
{
    [Fact]
    public void Link_points_at_this_repository()
    {
        var vm = new AboutPageViewModel("1.2.3");

        Assert.Equal("https://github.com/loxsmoke/interview-flow-net", vm.GitHubUrl);
        Assert.Equal("loxsmoke/interview-flow-net", vm.GitHubLabel);
    }

    [Fact]
    public void The_label_is_the_url_so_the_two_cannot_drift()
    {
        var vm = new AboutPageViewModel("1.2.3");
        Assert.EndsWith(vm.GitHubLabel, vm.GitHubUrl);
    }

    /// <summary>
    /// The text is wrapped in a Button; if that binding ever breaks, the label
    /// still renders and the click silently does nothing — so assert the
    /// command actually resolved.
    /// </summary>
    [AvaloniaFact]
    public void The_rendered_label_is_a_working_button()
    {
        var view = new InterviewFlow.App.Views.AboutPageView
        {
            DataContext = new AboutPageViewModel("1.2.3"),
        };
        var window = new Window { Content = view, Width = 900, Height = 820 };
        window.Show();
        window.UpdateLayout();

        var link = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => b.Content is TextBlock t && t.Text == "loxsmoke/interview-flow-net");

        Assert.NotNull(link.Command);
        Assert.True(link.Command!.CanExecute(null));

        window.Close();
    }
}
