using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Models;

namespace InterviewFlow.App.ViewModels.Pages;

/// <summary>
/// Custom action screen (docs/03-ui-spec.md §3.9): view mode (prompt preview +
/// markdown result + Run), edit mode (name/description/template/temperature +
/// tag insertion, unknown-tag confirm, unique-name check), and new mode.
/// </summary>
public sealed partial class CustomActionPageViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _shell;
    private CustomAction? _action;

    public AgentRunViewModel? Run { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewMode))]
    private bool _isEditing;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _promptTemplate = "";
    /// <summary>Empty = use the API default (temperature null).</summary>
    [ObservableProperty] private string _temperatureText = "";
    [ObservableProperty] private string _validationError = "";

    [ObservableProperty] private string _result = "";
    [ObservableProperty] private double _costUsd;
    [ObservableProperty] private string _costDetail = "";

    public bool IsNew => _action is null;
    public bool IsViewMode => !IsEditing;
    public string Title => IsNew ? "Add custom action" : _action!.Name;
    public IReadOnlyList<string> KnownTags { get; } =
        CustomActionAgent.KnownTags.Select(t => "{{" + t + "}}").ToList();

    /// <summary>(title, message, onConfirm) — the view shows the modal.</summary>
    public event Action<string, string, Action>? ConfirmRequested;

    public CustomActionPageViewModel(MainViewModel shell, CustomAction? action)
    {
        _shell = shell;
        _action = action;
        if (action is null)
        {
            _isEditing = true;
        }
        else
        {
            _name = action.Name;
            _description = action.Description;
            _promptTemplate = action.PromptTemplate;
            _temperatureText = action.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Run = new AgentRunViewModel(shell, $"custom:{action.Id}");
            Run.Completed += Load;
            Load();
        }
    }

    private void Load()
    {
        if (_action is null)
            return;
        var result = _shell.CurrentState?.CustomActionResults.GetValueOrDefault(_action.Name);
        Result = result?.Result ?? "";
        CostUsd = result?.CostUsd ?? 0;
        CostDetail = result is null
            ? ""
            : AgentPageViewModel.FormatCostDetail(result.ModelName, result.DurationMs, result.RanAt);
    }

    [RelayCommand]
    private void Edit() => IsEditing = true;

    [RelayCommand]
    private void CancelEdit()
    {
        if (IsNew)
        {
            _shell.NavigateToStep("setup");
            return;
        }

        IsEditing = false;
        Name = _action!.Name;
        Description = _action.Description;
        PromptTemplate = _action.PromptTemplate;
        TemperatureText = _action.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ValidationError = "";
    }

    [RelayCommand]
    private void Save()
    {
        ValidationError = "";
        var name = Name.Trim();
        if (name.Length == 0)
        {
            ValidationError = "Name is required.";
            return;
        }

        if (_shell.ActionStore.NameExists(name, _action?.Id ?? ""))
        {
            ValidationError = "A custom action with this name already exists.";
            return;
        }

        double? temperature = null;
        if (TemperatureText.Trim().Length > 0)
        {
            if (!double.TryParse(TemperatureText.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t)
                || t < 0 || t > 2)
            {
                ValidationError = "Temperature must be between 0 and 2, or empty for the API default.";
                return;
            }

            temperature = t;
        }

        var unknown = CustomActionAgent.FindUnknownTags(PromptTemplate);
        if (unknown.Count > 0)
        {
            ConfirmRequested?.Invoke(
                "Unknown tag(s)",
                $"The template contains unrecognized tags: {string.Join(", ", unknown.Select(t => "{{" + t + "}}"))}. " +
                "They will be sent to the AI as literal text. Save anyway?",
                () => Persist(name, temperature));
            return;
        }

        Persist(name, temperature);
    }

    private void Persist(string name, double? temperature)
    {
        var actions = _shell.ActionStore.Load();
        if (_action is null)
        {
            _action = new CustomAction();
            actions.Add(_action);
        }
        else
        {
            var index = actions.FindIndex(a => a.Id == _action.Id);
            if (index >= 0)
            {
                actions[index].Name = name;
                actions[index].Description = Description.Trim();
                actions[index].PromptTemplate = PromptTemplate;
                actions[index].Temperature = temperature;
                _action = actions[index];
                FinishPersist(actions);
                return;
            }

            actions.Add(_action);
        }

        _action.Name = name;
        _action.Description = Description.Trim();
        _action.PromptTemplate = PromptTemplate;
        _action.Temperature = temperature;
        FinishPersist(actions);
    }

    private void FinishPersist(List<CustomAction> actions)
    {
        _shell.ActionStore.Save(actions);
        _shell.ReloadCustomActions();
        IsEditing = false;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsNew));
        // Re-open through the shell so Run wiring exists for a newly created action.
        _shell.OpenCustomActionCommand.Execute(_action);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_action is null)
            return;
        ConfirmRequested?.Invoke(
            "Delete custom action?",
            $"\"{_action.Name}\" will be permanently removed. This cannot be undone.",
            () =>
            {
                var actions = _shell.ActionStore.Load();
                actions.RemoveAll(a => a.Id == _action.Id);
                _shell.ActionStore.Save(actions);
                _shell.Queue.CleanupCustomAction(_action.Id);
                _shell.ReloadCustomActions();
                _shell.NavigateToStep("setup");
            });
    }

    [RelayCommand]
    private void InsertTag(string tag) => PromptTemplate += tag;

    public void Dispose()
    {
        if (Run is not null)
        {
            Run.Completed -= Load;
            Run.Dispose();
        }
    }
}
