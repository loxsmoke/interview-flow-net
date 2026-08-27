using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewFlow.Core.Logging;
using InterviewFlow.Core.Models;

namespace InterviewFlow.Core.State;

/// <summary>
/// Global custom actions over &lt;dataDir&gt;/custom-actions.json (app/state.py's
/// load/save_custom_actions): same envelope discipline as the state store —
/// corrupt entries skipped, atomic writes. Names must be unique (the original
/// returns 409 on conflict).
/// </summary>
public sealed class CustomActionStore(string dataDir)
{
    public const string FileName = "custom-actions.json";

    private readonly Lock _sync = new();

    public string FilePath => Path.Combine(dataDir, FileName);

    public List<CustomAction> Load()
    {
        lock (_sync)
            return LoadUnlocked();
    }

    public void Save(List<CustomAction> actions)
    {
        lock (_sync)
        {
            var envelope = new CustomActionsEnvelope { Version = 1, Actions = actions };
            var body = StateJson.Serialize(envelope);
            AtomicFile.WriteAllText(FilePath, body);
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> collides with an existing action other
    /// than <paramref name="excludeId"/> (case-sensitive, like the original).
    /// </summary>
    public bool NameExists(string name, string excludeId = "") =>
        Load().Any(a => a.Name == name && a.Id != excludeId);

    private List<CustomAction> LoadUnlocked()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return [];

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("state", $"could not read custom actions file {path}: {ex.Message}");
            return [];
        }

        var result = new List<CustomAction>();
        if (root?["actions"] is not JsonArray actions)
            return result;

        foreach (var node in actions)
        {
            try
            {
                var action = node.Deserialize(
                    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<CustomAction>)
                        StateJson.Options.GetTypeInfo(typeof(CustomAction)));
                if (action is not null)
                    result.Add(action);
            }
            catch (Exception)
            {
                DiagnosticLog.Warn("state", "skipping corrupt custom action entry");
            }
        }

        return result;
    }
}
