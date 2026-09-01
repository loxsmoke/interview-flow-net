using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Resume screen (docs/03-ui-spec.md §3.3): upload/drag-drop → parse + tag,
/// Edit (tagged text) / Raw (diagnostic dump) / Preview tabs, tag insertion,
/// shared resume library (dedupe by description), Save &amp; Continue.
/// </summary>
public sealed partial class ResumePageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    [ObservableProperty] private string _taggedText = "";
    [ObservableProperty] private string _rawText = "";
    [ObservableProperty] private string _plainText = "";
    [ObservableProperty] private bool _showContact;
    [ObservableProperty] private string _uploadError = "";
    [ObservableProperty] private string _uploadedInfo = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _savedExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportPath))]
    private string _exportedPath = "";

    public bool HasExportPath => ExportedPath.Length > 0;

    public ObservableCollection<Resume> SavedResumes { get; } = [];

    public string SavedHeader => $"Saved resumes ({SavedResumes.Count})";
    public string ContactName => _shell.Config.ResumeName;
    public string ContactInfo => _shell.Config.ResumeContact;

    /// <summary>Insert-tag dropdown rows: tag + hint (§3.3).</summary>
    public IReadOnlyList<string> InsertTags { get; } =
    [
        "[Summary] — Professional summary paragraph",
        "[Section Heading] — Major section title",
        "[Job title] — Role | Company | Location | Dates",
        "[Job summary] — One-line role description",
        "[Job bullet] — Achievement bullet",
        "[Skill] — Category: skill1, skill2, …",
        "[Additional info] — Education, awards, extras",
    ];

    public event Action<string, string, Action>? ConfirmRequested;

    /// <summary>(title, watermark, onSubmit) — the view opens the input dialog.</summary>
    public event Action<string, string, Action<string>>? InputRequested;

    /// <summary>The view runs the save dialog + write, then reports the path.</summary>
    public event Func<string, Task<string?>>? ExportRequested;

    /// <summary>Shell access for the view's export call.</summary>
    public MainViewModel Shell => _shell;

    public ResumePageViewModel() : this(new MainViewModel()) { } // design-time

    public ResumePageViewModel(MainViewModel shell)
    {
        _shell = shell;
        var s = shell.CurrentState;
        if (s is not null)
        {
            _taggedText = s.ResumeTagged;
            _rawText = s.ResumeRaw;
            _plainText = s.ResumeText;
        }

        ReloadLibrary();
    }

    private void ReloadLibrary()
    {
        SavedResumes.Clear();
        foreach (var r in _shell.Store.ListResumeLibrary(_shell.CurrentState?.Id ?? ""))
            SavedResumes.Add(r);
        OnPropertyChanged(nameof(SavedHeader));
    }

    /// <summary>Shared by picker and drag-drop.</summary>
    public void LoadFile(string path)
    {
        UploadError = "";
        try
        {
            var result = ResumeIntake.Extract(Path.GetFileName(path), File.ReadAllBytes(path));
            PlainText = result.Text;
            RawText = result.Raw ?? "";
            TaggedText = result.Tagged;
            UploadedInfo = $"{result.Filename} · {result.Chars:N0} characters";
            SelectedTab = 0;
        }
        catch (ResumeIntakeException ex)
        {
            UploadError = ex.Message;
        }
        catch (Exception)
        {
            UploadError = "Could not extract text from file. Try a different format.";
        }
    }

    [RelayCommand]
    private void InsertTag(string entry)
    {
        var tag = entry.Split('—')[0].Trim();
        TaggedText = TaggedText.Length == 0 ? tag : TaggedText.TrimEnd() + "\n" + tag;
    }

    [RelayCommand]
    private void SaveToLibrary()
    {
        if (TaggedText.Trim().Length == 0)
            return;
        InputRequested?.Invoke("Save resume to library", "Short description (e.g. Backend focus)", description =>
        {
            if (description.Trim().Length == 0)
                return;
            var s = _shell.CurrentState;
            if (s is null)
                return;
            s.Resumes.Add(new Resume { Description = description.Trim(), Text = TaggedText });
            _shell.Store.SaveState(s);
            ReloadLibrary();
        });
    }

    [RelayCommand]
    private void SelectSaved(Resume resume)
    {
        TaggedText = resume.Text;
        // Library entries store the tagged text; derive the plain body for AI use.
        PlainText = System.Text.RegularExpressions.Regex
            .Replace(resume.Text, @"^\[[^\]]+\]\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Trim();
        RawText = "";
        UploadedInfo = $"From library: {resume.Description}";
        SavedExpanded = false;
    }

    [RelayCommand]
    private void DeleteSaved(Resume resume)
    {
        ConfirmRequested?.Invoke(
            "Delete saved resume?",
            $"\"{resume.Description}\" will be removed from the library.",
            () =>
            {
                foreach (var state in _shell.Store.LoadAll().Values)
                {
                    if (state.Resumes.RemoveAll(r => r.Id == resume.Id) > 0)
                        _shell.Store.SaveState(state);
                }

                ReloadLibrary();
            });
    }

    [RelayCommand]
    private async Task ExportDocxAsync()
    {
        if (ExportRequested is null || TaggedText.Trim().Length == 0)
            return;
        var path = await ExportRequested(TaggedText);
        if (path is not null)
            ExportedPath = path;
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (ExportedPath.Length > 0)
            Platform.ShellOpen.RevealInFileManager(ExportedPath);
    }

    [RelayCommand]
    private void SaveAndContinue()
    {
        var s = _shell.CurrentState;
        if (s is null)
            return;
        s.ResumeTagged = TaggedText;
        s.ResumeRaw = RawText;
        if (PlainText.Trim().Length > 0)
            s.ResumeText = PlainText;
        else if (TaggedText.Trim().Length > 0)
            s.ResumeText = System.Text.RegularExpressions.Regex
                .Replace(TaggedText, @"^\[[^\]]+\]\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Trim();
        if (s.ResumeText.Trim().Length > 0 && !s.CompletedSteps.Contains("resume"))
            s.CompletedSteps.Add("resume");
        _shell.Store.SaveState(s);
        _shell.NotifyStateChanged();
        _shell.NavigateToStep("research");
    }
}
