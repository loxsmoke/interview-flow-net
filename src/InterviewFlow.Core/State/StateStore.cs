using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Logging;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Core.State;

/// <summary>
/// Persistent workflow store over &lt;dataDir&gt;/interview-flow-data.json, ported
/// from app/state.py. All workflows live in one file; every save is a full
/// read-merge-write behind a lock; writes are atomic. Individual corrupt
/// entries are skipped on load (matching the original), but a file declaring a
/// newer schema version refuses to load at all — never risk silently rewriting
/// data a newer app wrote.
/// </summary>
public sealed class StateStore(string dataDir)
{
    public const string DataFileName = "interview-flow-data.json";

    private readonly Lock _sync = new();

    public string DataDir { get; } = dataDir;

    public string DataFilePath => Path.Combine(DataDir, DataFileName);

    /// <summary>Loads every workflow; missing/unreadable file → empty (original behavior).</summary>
    public Dictionary<string, InterviewState> LoadAll()
    {
        lock (_sync)
            return LoadAllUnlocked();
    }

    public InterviewState? LoadState(string stateId)
    {
        if (!ModelDefaults.IsValidId(stateId))
            return null;
        return LoadAll().GetValueOrDefault(stateId);
    }

    /// <summary>Adds or updates a workflow; stamps updated_at like the original.</summary>
    public void SaveState(InterviewState state)
    {
        state.UpdatedAt = ModelDefaults.NowIso();
        lock (_sync)
        {
            var states = LoadAllUnlocked();
            states[state.Id] = state;
            WriteAllUnlocked(states);
        }
    }

    public bool DeleteState(string stateId)
    {
        if (!ModelDefaults.IsValidId(stateId))
            return false;
        lock (_sync)
        {
            var states = LoadAllUnlocked();
            if (!states.Remove(stateId))
                return false;
            WriteAllUnlocked(states);
            return true;
        }
    }

    /// <summary>Session summaries, newest updated first (original list_states()).</summary>
    public List<StateSummary> ListSummaries()
    {
        return LoadAll().Values
            .OrderByDescending(SortKey, StringComparer.Ordinal)
            .Select(s => new StateSummary(
                s.Id,
                string.IsNullOrEmpty(s.CompanyName) ? "(unnamed)" : s.CompanyName,
                s.Position,
                s.CurrentStep,
                s.CompletedSteps,
                s.CreatedAt,
                s.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Deduplicated resume library across all workflows (original
    /// list_resume_library): preferred workflow first, then newest-updated;
    /// within each workflow resumes are scanned newest-first (reversed); dedupe
    /// key is the trimmed, case-folded description, falling back to id.
    /// </summary>
    public List<Resume> ListResumeLibrary(string preferredStateId = "")
    {
        var states = LoadAll();
        var ordered = new List<InterviewState>();
        if (preferredStateId.Length > 0 && states.TryGetValue(preferredStateId, out var preferred))
            ordered.Add(preferred);
        ordered.AddRange(states.Values
            .Where(s => s.Id != preferredStateId)
            .OrderByDescending(SortKey, StringComparer.Ordinal));

        var seen = new HashSet<string>();
        var resumes = new List<Resume>();
        foreach (var state in ordered)
        {
            for (var i = state.Resumes.Count - 1; i >= 0; i--)
            {
                var resume = state.Resumes[i];
                var trimmed = resume.Description.Trim();
                // Python uses str.casefold(); ToLowerInvariant matches for the
                // ASCII descriptions this app produces.
                var key = trimmed.Length > 0 ? trimmed.ToLowerInvariant() : resume.Id;
                if (seen.Add(key))
                    resumes.Add(resume);
            }
        }

        return resumes;
    }

    private static string SortKey(InterviewState s) =>
        s.UpdatedAt.Length > 0 ? s.UpdatedAt : s.CreatedAt;

    private Dictionary<string, InterviewState> LoadAllUnlocked()
    {
        var path = DataFilePath;
        if (!File.Exists(path))
            return [];

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("state", $"could not read data file {path}: {ex.Message}");
            return [];
        }

        var version = root?["version"]?.GetValue<int>() ?? 1;
        if (version > 1)
            throw new DataFileVersionException(path, version);

        var result = new Dictionary<string, InterviewState>();
        if (root?["states"] is not JsonObject states)
            return result;

        foreach (var (sid, node) in states)
        {
            try
            {
                var state = node.Deserialize(
                    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<InterviewState>)
                        StateJson.Options.GetTypeInfo(typeof(InterviewState)));
                if (state is not null)
                    result[sid] = state;
            }
            catch (Exception)
            {
                // Matches the original: skip the corrupt entry, keep the rest.
                DiagnosticLog.Warn("state", $"skipping corrupt session entry: {sid}");
            }
        }

        return result;
    }

    private void WriteAllUnlocked(Dictionary<string, InterviewState> states)
    {
        var envelope = new StateFileEnvelope { Version = 1, States = states };
        var body = StateJson.Serialize(envelope);
        AtomicFile.WriteAllText(DataFilePath, body);
    }
}

/// <summary>Row shape of the original's list_states() summaries.</summary>
public sealed record StateSummary(
    string Id,
    string CompanyName,
    string Position,
    string CurrentStep,
    List<string> CompletedSteps,
    string CreatedAt,
    string UpdatedAt);
