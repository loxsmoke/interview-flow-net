using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InterviewFlow.App.ViewModels;

/// <summary>
/// Dev harness VM (F12 window): golden-corpus markdown for the side-by-side
/// source/render view. Also serves as the mermaid debug harness — see TODO §2.
/// </summary>
public sealed partial class DevMarkdownViewModel : ObservableObject
{
    [ObservableProperty]
    private string _markdownSource;

    public DevMarkdownViewModel() => _markdownSource = LoadGoldenCorpus();

    private static string LoadGoldenCorpus()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://InterviewFlow.App/Assets/golden-corpus.md"));
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "# Golden corpus asset missing\nEdit this text to test the renderer.";
        }
    }
}
