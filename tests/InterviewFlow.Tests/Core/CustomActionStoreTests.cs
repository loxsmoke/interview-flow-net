using InterviewFlow.Core.Models;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.Core;

public sealed class CustomActionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private CustomActionStore StoreWithFixture()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-custom-actions.json"),
            Path.Combine(_dir, CustomActionStore.FileName));
        return new CustomActionStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Loads_actions_and_skips_corrupt_entry()
    {
        var actions = StoreWithFixture().Load();

        Assert.Equal(2, actions.Count); // third has temperature: "not-a-number"
        Assert.Equal("What question to ask", actions[0].Name);
        Assert.Null(actions[0].Temperature);
        Assert.Equal(0.7, actions[1].Temperature);
        Assert.Contains("🚀", actions[1].PromptTemplate);
    }

    [Fact]
    public void Save_round_trips_and_serializes_null_temperature()
    {
        var store = StoreWithFixture();
        var actions = store.Load();
        store.Save(actions);

        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"temperature\": null", text);
        Assert.Contains("Résumé", text); // unicode literal

        var reloaded = store.Load();
        Assert.Equal(2, reloaded.Count);
        Assert.Null(reloaded[0].Temperature);
    }

    [Fact]
    public void NameExists_detects_conflicts_excluding_self()
    {
        var store = StoreWithFixture();
        Assert.True(store.NameExists("Follow-up email"));
        Assert.False(store.NameExists("Follow-up email", excludeId: "47272cb29026"));
        Assert.False(store.NameExists("brand new name"));
    }

    [Fact]
    public void Missing_file_loads_empty_and_save_creates_it()
    {
        var store = new CustomActionStore(Path.Combine(_dir, "fresh"));
        Assert.Empty(store.Load());
        store.Save([new CustomAction { Name = "First" }]);
        Assert.Single(store.Load());
    }
}
