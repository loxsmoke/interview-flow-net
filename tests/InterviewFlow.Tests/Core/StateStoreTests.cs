using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.Core;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private StateStore StoreWithFixture()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(FixturePath("sample-data.json"), Path.Combine(_dir, StateStore.DataFileName));
        return new StateStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Loads_fixture_and_skips_corrupt_entry()
    {
        var store = StoreWithFixture();
        var states = store.LoadAll();

        // 3 entries in the file; "deadbeef0000" has stories as a string → skipped.
        Assert.Equal(2, states.Count);
        Assert.DoesNotContain("deadbeef0000", states.Keys);

        var s = states["740841cb24c9"];
        Assert.Equal("Acme | backend", s.CompanyName);
        Assert.Contains("Zürich", s.JobPosting);
        Assert.Contains("🚀", s.JobPosting);
        Assert.Equal(82, s.Research.FitScore);
        Assert.Equal("Strong Fit", s.Stories[0].FitScores["system_design"]);
        Assert.Equal(4, s.MockSessions[0].Questions[0].Scores["clarity"]);
        Assert.Equal(4.5, s.MockSessions[0].OverallScores["communication"]);
        Assert.Equal(0.0421, s.CustomActionResults["What question to ask"].CostUsd);
        Assert.Equal("Regulated healthcare experience", s.InterviewerConcerns[0]["counter_evidence"]);
        Assert.Equal(["setup", "resume", "research"], s.CompletedSteps);
    }

    [Fact]
    public void Round_trip_is_stable_and_keeps_unicode_literal()
    {
        var store = StoreWithFixture();
        var states = store.LoadAll();

        var outDir = Path.Combine(_dir, "out");
        var outStore = new StateStore(outDir);
        foreach (var s in states.Values)
            outStore.SaveState(s);

        var text = File.ReadAllText(outStore.DataFilePath);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("Zürich", text);       // ensure_ascii=False parity
        Assert.Contains("🛡️", text);
        Assert.DoesNotContain("\\u00fc", text);

        // Write → read → write must be byte-identical (serializer stability).
        var again = new StateStore(outDir).LoadAll();
        Assert.Equal(states.Count, again.Count);
        foreach (var s in again.Values)
            outStore.SaveState(s);
        // SaveState stamps updated_at, so compare a fresh double-dump instead.
        var dirA = Path.Combine(_dir, "a");
        var dirB = Path.Combine(_dir, "b");
        var a = new StateStore(dirA);
        var b = new StateStore(dirB);
        foreach (var s in states.Values) a.SaveState(s);
        var reloaded = new StateStore(dirA).LoadAll();
        foreach (var s in reloaded.Values) b.SaveState(s);
        static string Strip(string p) =>
            System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(p), "\"updated_at\": \"[^\"]*\"", "\"updated_at\": \"X\"");
        Assert.Equal(Strip(a.DataFilePath), Strip(b.DataFilePath));
    }

    [Fact]
    public void SaveState_stamps_updated_at_and_merges()
    {
        var store = StoreWithFixture();
        var s = store.LoadState("1112223334f5")!;
        var before = s.UpdatedAt;
        s.Position = "Principal Engineer";
        store.SaveState(s);

        var reloaded = store.LoadState("1112223334f5")!;
        Assert.Equal("Principal Engineer", reloaded.Position);
        Assert.NotEqual(before, reloaded.UpdatedAt);
        Assert.Equal(2, store.LoadAll().Count); // other state untouched
    }

    [Fact]
    public void Invalid_ids_are_rejected()
    {
        var store = StoreWithFixture();
        Assert.Null(store.LoadState("../../evil"));
        Assert.Null(store.LoadState("ABCDEF123456")); // uppercase not allowed
        Assert.False(store.DeleteState("nope"));
    }

    [Fact]
    public void DeleteState_removes_and_reports()
    {
        var store = StoreWithFixture();
        Assert.True(store.DeleteState("1112223334f5"));
        Assert.False(store.DeleteState("1112223334f5"));
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void Summaries_are_newest_first_with_unnamed_fallback()
    {
        var store = StoreWithFixture();
        var summaries = store.ListSummaries();
        Assert.Equal(2, summaries.Count);
        Assert.Equal("1112223334f5", summaries[0].Id); // updated 2026-06-21 > 2026-06-01
        Assert.Equal("740841cb24c9", summaries[1].Id);

        var s = store.LoadState("1112223334f5")!;
        s.CompanyName = "";
        store.SaveState(s);
        Assert.Equal("(unnamed)", store.ListSummaries()[0].CompanyName);
    }

    [Fact]
    public void Resume_library_dedupes_by_casefolded_description()
    {
        var store = StoreWithFixture();
        var resumes = store.ListResumeLibrary(preferredStateId: "740841cb24c9");

        // Preferred state's "Backend focus" wins; the other state's
        // "  backend FOCUS  " collapses onto it; "Frontend variant" survives.
        Assert.Equal(2, resumes.Count);
        Assert.Equal("Backend focus", resumes[0].Description);
        Assert.Equal("Frontend variant", resumes[1].Description);
    }

    [Fact]
    public void Newer_schema_version_refuses_to_load()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, StateStore.DataFileName),
            """{ "version": 2, "states": {} }""");
        var store = new StateStore(_dir);
        Assert.Throws<DataFileVersionException>(() => store.LoadAll());
    }

    [Fact]
    public void Missing_file_loads_empty()
    {
        var store = new StateStore(Path.Combine(_dir, "empty"));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void New_ids_are_valid_12_hex()
    {
        for (var i = 0; i < 20; i++)
            Assert.True(ModelDefaults.IsValidId(ModelDefaults.NewId()));
        Assert.False(ModelDefaults.IsValidId("12345678901")); // 11 chars
        Assert.False(ModelDefaults.IsValidId("g23456789012")); // non-hex
    }

    [Fact]
    public void Timestamps_match_python_isoformat_shape()
    {
        var ts = ModelDefaults.NowIso();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$", ts);
    }
}
