using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Setup screen (docs/03-ui-spec.md §3.2): application CRUD + company/position/
/// job-posting inputs. Confirm dialogs are requested via events so the view owns
/// the modal (openlogi-net convention); URL fetch of pasted job-posting links
/// arrives in M8.
/// </summary>
public sealed partial class SetupPageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private string _position = "";
    [ObservableProperty] private string _jobPosting = "";

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
    [ObservableProperty] private bool _isFetching;

    public bool HasFetchStatus => FetchStatus.Length > 0;

    [RelayCommand]
    private async Task SaveAndContinueAsync()
    {
        // A pasted URL is resolved to page text first (docs/05 §5.7).
        if (Core.Agents.JobPostingFetcher.LooksLikeUrl(JobPosting))
        {
            IsFetching = true;
            FetchStatus = "Fetching the job posting…";
            try
            {
                var result = await Core.Agents.JobPostingFetcher.ResolveAsync(_shell.Config, JobPosting);
                if (result.Error is not null)
                {
                    FetchStatus = result.Error;
                    IsFetching = false;
                    return;
                }

                JobPosting = result.Text;
                FetchStatus = result.UsedLlmFallback
                    ? "Fetched via the AI provider (the page needed JavaScript) — a small query cost applies."
                    : "Fetched the posting from the URL.";
            }
            catch (Exception ex)
            {
                FetchStatus = $"Fetch failed: {ex.Message}";
                IsFetching = false;
                return;
            }
            finally
            {
                IsFetching = false;
            }
        }

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
