using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.State;

namespace InterviewFlow.Tests.Core;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "if-mig-" + Guid.NewGuid().ToString("N")[..8]);

    private (string From, string To) MakeDirs(params (string Name, string Content)[] files)
    {
        var from = Path.Combine(_root, "from");
        var to = Path.Combine(_root, "to");
        Directory.CreateDirectory(from);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(from, name), content);
        return (from, to);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Lists_only_json_files_sorted()
    {
        var (from, _) = MakeDirs(
            ("interview-flow-data.json", "{}"),
            ("custom-actions.json", "{}"),
            ("notes.txt", "ignore me"));
        Assert.Equal(["custom-actions.json", "interview-flow-data.json"], DataMigration.ListDataFiles(from));
    }

    [Fact]
    public void Full_migration_copies_verifies_and_deletes()
    {
        var (from, to) = MakeDirs(("interview-flow-data.json", "{\"version\":1}"));
        var files = DataMigration.ListDataFiles(from);

        Assert.True(DataMigration.Copy(from, to, files).Ok);
        Assert.True(DataMigration.Verify(from, to, files).Ok);
        Assert.True(DataMigration.DeleteOriginals(from, files).Ok);

        Assert.Equal("{\"version\":1}", File.ReadAllText(Path.Combine(to, "interview-flow-data.json")));
        Assert.False(File.Exists(Path.Combine(from, "interview-flow-data.json")));
    }

    [Fact]
    public void Verify_detects_a_corrupted_copy()
    {
        var (from, to) = MakeDirs(("interview-flow-data.json", "original"));
        var files = DataMigration.ListDataFiles(from);
        Assert.True(DataMigration.Copy(from, to, files).Ok);

        File.WriteAllText(Path.Combine(to, "interview-flow-data.json"), "tampered");
        var verify = DataMigration.Verify(from, to, files);
        Assert.False(verify.Ok);
        Assert.Contains("not identical", verify.Error);
        // The originals survive — a failed verify must never delete anything.
        Assert.True(File.Exists(Path.Combine(from, "interview-flow-data.json")));
    }

    [Fact]
    public void Copying_onto_the_same_directory_is_refused()
    {
        var (from, _) = MakeDirs(("a.json", "{}"));
        var result = DataMigration.Copy(from, from, ["a.json"]);
        Assert.False(result.Ok);
        Assert.Contains("same as the current data directory", result.Error);
    }

    [Fact]
    public void Same_directory_detection_normalizes_paths()
    {
        var (from, _) = MakeDirs(("a.json", "{}"));
        Assert.True(DataMigration.IsSameDirectory(from, from + Path.DirectorySeparatorChar));
        Assert.False(DataMigration.IsSameDirectory(from, Path.Combine(from, "sub")));
    }

    [Fact]
    public void Missing_source_file_fails_verification_cleanly()
    {
        var (from, to) = MakeDirs(("a.json", "{}"));
        Directory.CreateDirectory(to);
        var verify = DataMigration.Verify(from, to, ["a.json"]);
        Assert.False(verify.Ok);
        Assert.Contains("Cannot read copied file", verify.Error);
    }
}

public sealed class JobPostingFetcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-fetch-" + Guid.NewGuid().ToString("N")[..8]);

    private AppConfig Config(string content = "ACTIVE_PROVIDER=ollama\n")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, content);
        return new AppConfig(EnvFile.Load(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("https://jobs.example.com/posting/123", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("We are hiring a Staff Engineer…", false)]
    [InlineData("https://example.com/a b", false)] // whitespace = not a bare URL
    [InlineData("ftp://example.com/x", false)]
    public void Detects_bare_urls(string input, bool expected) =>
        Assert.Equal(expected, JobPostingFetcher.LooksLikeUrl(input));

    [Theory]
    [InlineData("http://localhost:8000/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://192.168.1.10/x")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")] // cloud metadata
    [InlineData("file:///etc/passwd")]
    public void Ssrf_guard_rejects_private_and_non_http_targets(string url) =>
        Assert.False(JobPostingFetcher.IsSafeUrl(url));

    [Fact]
    public void Html_to_text_strips_scripts_styles_and_tags()
    {
        const string html = "<html><style>.a{color:red}</style><script>evil()</script>" +
                            "<body><h1>Staff Engineer</h1><p>Build&nbsp;things &amp; ship.</p></body></html>";
        // Entities are decoded AFTER whitespace collapse (the original's order),
        // so &nbsp; survives as U+00A0 rather than becoming a plain space.
        Assert.Equal("Staff Engineer Build things & ship.", JobPostingFetcher.HtmlToText(html));
    }

    [Fact]
    public async Task Non_url_input_passes_through_untouched()
    {
        var result = await JobPostingFetcher.ResolveAsync(Config(), "Plain posting text");
        Assert.Equal("Plain posting text", result.Text);
        Assert.False(result.WasFetched);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Private_url_is_refused_before_any_request()
    {
        var handler = new FakeHandler(); // nothing queued — a request would throw
        var result = await JobPostingFetcher.ResolveAsync(
            Config(), "http://127.0.0.1:8000/job", http: new HttpClient(handler));
        Assert.NotNull(result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Substantial_page_text_is_used_directly()
    {
        var body = "<html><body><p>" + new string('x', 400) + "</p></body></html>";
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, body, "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), "https://example.com/job", http: new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.StartsWith("xxxx", result.Text);
    }

    [Fact]
    public async Task Thin_page_on_ollama_skips_the_llm_fallback()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html><body>Loading…</body></html>", "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config("ACTIVE_PROVIDER=ollama\n"), "https://example.com/job", http: new HttpClient(handler));

        Assert.Equal(JobPostingFetcher.CouldNotExtractMessage, result.Error);
        Assert.Single(handler.Requests); // no provider call attempted
    }

    [Fact]
    public async Task Thin_page_falls_back_to_the_llm_web_fetch()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html><body>Loading…</body></html>", "text/html");
        // The provider returns the real posting via its server-side fetch.
        var posting = new string('y', 400);
        handler.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + posting + "\"}}\n\n");

        var result = await JobPostingFetcher.ResolveAsync(
            Config("ACTIVE_PROVIDER=anthropic\nANTHROPIC_API_KEY=sk-test\n"),
            "https://example.com/job", http: new HttpClient(handler));

        Assert.True(result.UsedLlmFallback);
        Assert.Equal(posting, result.Text);
        Assert.Equal(2, handler.Requests.Count);
    }
}
