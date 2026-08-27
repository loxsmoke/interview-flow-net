using System.Text.Json.Nodes;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// M1 exit-gate proxy: load the REAL data folder written by the original Python
/// app (read-only — these tests never write into it), round-trip through the
/// port's store, and verify nothing is lost. Skips silently on machines without
/// the original checkout. The full cross-check (original app loading the port's
/// output) still needs a Python run — tracked in TODO §3.
/// </summary>
public sealed class RealDataCompatTests(ITestOutputHelper output) : IDisposable
{
    private const string OriginalDataDir = @"C:\dev\interview-flow\data";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "if-real-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static bool Available => File.Exists(Path.Combine(OriginalDataDir, StateStore.DataFileName));

    [Fact]
    public void Every_state_in_the_real_file_loads_without_being_skipped()
    {
        if (!Available)
        {
            output.WriteLine("SKIPPED: original data folder not present.");
            return;
        }

        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(OriginalDataDir, StateStore.DataFileName)));
        var rawCount = ((JsonObject)raw!["states"]!).Count;

        var states = new StateStore(OriginalDataDir).LoadAll();
        output.WriteLine($"real file: {rawCount} states, loaded {states.Count}");
        Assert.Equal(rawCount, states.Count); // zero corrupt-skips on real data
    }

    [Fact]
    public void Real_data_round_trips_through_the_port_without_loss()
    {
        if (!Available)
        {
            output.WriteLine("SKIPPED: original data folder not present.");
            return;
        }

        var source = new StateStore(OriginalDataDir);
        var states = source.LoadAll();

        // Dump with the port, reload, dump again — the two dumps must be
        // byte-identical (updated_at excluded: SaveState stamps it).
        var dirA = Path.Combine(_tmp, "a");
        var dirB = Path.Combine(_tmp, "b");
        var a = new StateStore(dirA);
        foreach (var s in states.Values)
            a.SaveState(s);
        var reloaded = new StateStore(dirA).LoadAll();
        Assert.Equal(states.Count, reloaded.Count);
        var b = new StateStore(dirB);
        foreach (var s in reloaded.Values)
            b.SaveState(s);

        static string Strip(string p) =>
            System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(p), "\"updated_at\": \"[^\"]*\"", "\"updated_at\": \"X\"");
        Assert.Equal(Strip(a.DataFilePath), Strip(b.DataFilePath));

        // Field-level spot check against the raw JSON for every state: the big
        // markdown payloads must survive char-for-char.
        var raw = (JsonObject)JsonNode.Parse(File.ReadAllText(source.DataFilePath))!["states"]!;
        foreach (var (sid, node) in raw)
        {
            var port = reloaded[sid];
            Assert.Equal((string?)node!["company_name"], port.CompanyName);
            Assert.Equal((string?)node["research"]?["raw_report"], port.Research.RawReport);
            Assert.Equal((string?)node["jd_analysis"]?["raw_analysis"], port.JdAnalysis.RawAnalysis);
            Assert.Equal((string?)node["resume_tagged"], port.ResumeTagged);
            Assert.Equal(((JsonArray?)node["stories"])?.Count ?? 0, port.Stories.Count);
        }

        output.WriteLine($"round-tripped {states.Count} real states losslessly");
    }

    [Fact]
    public void Real_custom_actions_load_and_round_trip()
    {
        var path = Path.Combine(OriginalDataDir, CustomActionStore.FileName);
        if (!File.Exists(path))
        {
            output.WriteLine("SKIPPED: original custom-actions file not present.");
            return;
        }

        var raw = JsonNode.Parse(File.ReadAllText(path));
        var rawCount = ((JsonArray)raw!["actions"]!).Count;

        var actions = new CustomActionStore(OriginalDataDir).Load();
        Assert.Equal(rawCount, actions.Count);

        Directory.CreateDirectory(_tmp);
        var outStore = new CustomActionStore(_tmp);
        outStore.Save(actions);
        var reloaded = outStore.Load();
        Assert.Equal(actions.Count, reloaded.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            Assert.Equal(actions[i].Name, reloaded[i].Name);
            Assert.Equal(actions[i].PromptTemplate, reloaded[i].PromptTemplate);
            Assert.Equal(actions[i].Temperature, reloaded[i].Temperature);
        }
    }
}
