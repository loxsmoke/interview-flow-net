using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Setup screen (docs/03-ui-spec.md §3.2): application CRUD + company/position/
/// job-posting inputs. Confirm dialogs are requested via events so the view owns
/// the modal (openlogi-net convention). A pasted job-posting URL is resolved
/// either by the explicit "Fetch from URL" button — which stays on this page —
/// or implicitly by Save &amp; Continue.
/// </summary>
public sealed partial class SetupPageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    [ObservableProperty] private string _companyName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TechDetected))]
    private string _position = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchFromUrlCommand))]
    private string _jobPosting = "";

    /// <summary>
    /// Drives the "(tech)" hint next to the Position label — same heuristic the
    /// Interview Intel prompt uses, recomputed as the user types (index.html
    /// techDetected).
    /// </summary>
    public bool TechDetected => Core.Agents.SectionAgents.IsTechnicalRole(Position);

    // The sidebar badge on Interview Intel tracks the same typing, unsaved.
    partial void OnPositionChanged(string value) => _shell.SetDraftPosition(value);

    public ObservableCollection<StateSummary> SavedApplications { get; } = [];

    /// <summary>View shows a confirm dialog, then invokes the callback on OK.</summary>
    public event Action<string, string, Action>? ConfirmRequested;

    public SetupPageViewModel() : this(new MainViewModel()) { } // design-time

    public SetupPageViewModel(MainViewModel shell)
    {
        _shell = shell;
        if (shell.CurrentState is { } s)
        {
            _companyName = s.CompanyName;
            _position = s.Position;
            _jobPosting = s.JobPosting;
        }

        ReloadSaved();
    }

    public string ProviderChip => _shell.ProviderChip;
    public bool ProviderConfigured => _shell.ProviderConfigured;

    public string SavedHeader => $"Previous applications ({SavedApplications.Count})";

    private void ReloadSaved()
    {
        SavedApplications.Clear();
        foreach (var s in _shell.Store.ListSummaries())
            SavedApplications.Add(s);
        OnPropertyChanged(nameof(SavedHeader));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFetchStatus))]
    private string _fetchStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchFromUrlCommand))]
    private bool _isFetching;

    public bool HasFetchStatus => FetchStatus.Length > 0;

    /// <summary>Test seam: null means the shared client (docs/05 §5.7).</summary>
    internal HttpClient? FetchClient { get; set; }

    /// <summary>Gates the Fetch button: only a bare URL is fetchable.</summary>
    public bool CanFetchFromUrl =>
        !IsFetching && Core.Agents.JobPostingFetcher.LooksLikeUrl(JobPosting);

    /// <summary>
    /// The explicit button (§3.2). Deliberately does NOT save or navigate — the
    /// user stays on Setup to check the text and the filled-in names first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFetchFromUrl))]
    private Task FetchFromUrlAsync() => FetchAsync();

    /// <summary>
    /// Resolves the pasted URL into the posting box and fills Company/Position
    /// when the source named them. False means the fetch failed and the caller
    /// should stop. Shared by the button and Save &amp; Continue.
    /// </summary>
    private async Task<bool> FetchAsync()
    {
        IsFetching = true;
        FetchStatus = "Fetching the job posting…";
        try
        {
            var result = await Core.Agents.JobPostingFetcher.ResolveAsync(
                _shell.Config, JobPosting, http: FetchClient);
            if (result.Error is not null)
            {
                FetchStatus = result.Error;
                return false;
            }

            JobPosting = result.Text;
            FetchStatus = (result.UsedLlmFallback
                ? "Fetched via the AI provider (the page needed JavaScript) — a small query cost applies."
                : "Fetched the posting from the URL.") + ApplyNames(result);
            return true;
        }
        catch (Exception ex)
        {
            FetchStatus = $"Fetch failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>
    /// Fills Company/Position from the posting, but never overwrites what the
    /// user typed — a fetch is not a reason to discard their wording. Returns
    /// the sentence fragment appended to the status line.
    /// </summary>
    private string ApplyNames(Core.Agents.JobPostingResult result)
    {
        var filled = new List<string>();
        var kept = new List<string>();

        if (result.Company.Length > 0)
        {
            if (CompanyName.Trim().Length == 0)
            {
                CompanyName = result.Company;
                filled.Add("Company");
            }
            else if (CompanyName.Trim() != result.Company)
            {
                kept.Add("Company");
            }
        }

        if (result.Position.Length > 0)
        {
            if (Position.Trim().Length == 0)
            {
                Position = result.Position;
                filled.Add("Position");
            }
            else if (Position.Trim() != result.Position)
            {
                kept.Add("Position");
            }
        }

        var note = filled.Count > 0 ? $" Filled {string.Join(" and ", filled)}." : "";
        if (kept.Count > 0)
            note += $" Left your {string.Join(" and ", kept)} as typed.";
        return note;
    }

    [RelayCommand]
    private async Task SaveAndContinueAsync()
    {
        // A pasted URL is resolved to page text first (docs/05 §5.7).
        if (Core.Agents.JobPostingFetcher.LooksLikeUrl(JobPosting) && !await FetchAsync())
            return;

        var state = _shell.CurrentState ?? new InterviewState();
        state.CompanyName = CompanyName;
        state.Position = Position;
        state.JobPosting = JobPosting;
        if (!state.CompletedSteps.Contains("setup"))
            state.CompletedSteps.Add("setup");
        _shell.Store.SaveState(state);
        _shell.CurrentState = state;
        _shell.NotifyStateChanged();
        _shell.NavigateToStep("resume");
    }

    [RelayCommand]
    private void NewApplication()
    {
        _shell.StartNewWorkflow();
        CompanyName = "";
        Position = "";
        JobPosting = "";
        ReloadSaved();
    }

    [RelayCommand]
    private void Select(StateSummary summary) => _shell.SelectWorkflow(summary.Id);

    [RelayCommand]
    private void Clone(StateSummary summary)
    {
        ConfirmRequested?.Invoke(
            "Clone application?",
            $"\"{summary.CompanyName}\" will be duplicated with all its data.",
            () =>
            {
                var source = _shell.Store.LoadState(summary.Id);
                if (source is null)
                    return;
                _shell.CloneWorkflow(source);
                ReloadSaved();
            });
    }

    [RelayCommand]
    private void Delete(StateSummary summary)
    {
        ConfirmRequested?.Invoke(
            "Delete application?",
            $"\"{summary.CompanyName}\" will be permanently removed. This cannot be undone.",
            () =>
            {
                _shell.DeleteWorkflow(summary.Id);
                ReloadSaved();
            });
    }

    /// <summary>"N steps done · updated …" line under each saved row.</summary>
    public static string SummaryDetail(StateSummary s)
    {
        var updated = s.UpdatedAt.Length >= 10 ? s.UpdatedAt[..10] : s.UpdatedAt;
        return $"{s.CompletedSteps.Count} steps done · {updated}";
    }
}
