using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// The client-rendered-posting paths (docs/05 §5.7). Fixtures are the real
/// captured responses for a Workday posting: the page a browser-shaped GET
/// returns, and the CXS JSON serving the same requisition.
/// </summary>
public sealed class WorkdayPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string PostingUrl =
        "https://brooksauto.wd1.myworkdayjobs.com/brooks_external_site/job/" +
        "US---Fremont-CA/Software-Engineer---C----Python--Contract-Role-_R1565";

    [Theory]
    // Classic site URL: tenant from the host, site from the first path segment.
    [InlineData(
        "https://brooksauto.wd1.myworkdayjobs.com/brooks_external_site/job/US---Fremont-CA/Eng_R1565",
        "https://brooksauto.wd1.myworkdayjobs.com/wday/cxs/brooksauto/brooks_external_site/job/US---Fremont-CA/Eng_R1565")]
    // Locale segment ahead of the site id is dropped.
    [InlineData(
        "https://acme.wd5.myworkdayjobs.com/en-US/careers/job/London/Engineer_R7",
        "https://acme.wd5.myworkdayjobs.com/wday/cxs/acme/careers/job/London/Engineer_R7")]
    // Newer card layout uses /details/ but the same CXS route.
    [InlineData(
        "https://acme.wd3.myworkdayjobs.com/careers/details/Engineer_R7",
        "https://acme.wd3.myworkdayjobs.com/wday/cxs/acme/careers/job/Engineer_R7")]
    public void Maps_posting_urls_to_the_cxs_endpoint(string url, string expected) =>
        Assert.Equal(expected, WorkdayPosting.CxsUrl(url));

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123")]          // not Workday
    [InlineData("https://acme.wd1.myworkdayjobs.com/careers")]           // site root, no job
    [InlineData("https://acme.wd1.myworkdayjobs.com/careers/search/x")]  // not a posting route
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(WorkdayPosting.CxsUrl(url));

    [Fact]
    public void Already_cxs_urls_pass_through()
    {
        const string cxs = "https://acme.wd1.myworkdayjobs.com/wday/cxs/acme/careers/job/Engineer_R7";
        Assert.Equal(cxs, WorkdayPosting.CxsUrl(cxs));
    }

    [Fact]
    public void Parses_the_real_cxs_payload()
    {
        var posting = WorkdayPosting.ParseCxsJson(Fixture("workday-cxs-job.json"));

        Assert.NotNull(posting);
        // Role and employer come back named, for Setup's Company/Position fields.
        Assert.Equal("Software Engineer - C# / Python (Contract Role)", posting!.Title);
        Assert.Equal("Brooks Automation US LLC", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Software Engineer - C# / Python (Contract Role)", text);
        Assert.Contains("Company: Brooks Automation US LLC", text);
        Assert.Contains("Location: US - Fremont, CA", text);
        Assert.Contains("Requisition: R1565", text);
        // The description survives as readable text, bullets and all.
        Assert.Contains("semiconductor", text);
        Assert.Contains("•", text);
        Assert.DoesNotContain("<p", text);
        Assert.DoesNotContain("&nbsp;", text);
        Assert.True(text.Length > 3000, $"expected the full posting, got {text.Length} chars");
    }

    [Theory]
    [InlineData("""{"jobPostingInfo":{"title":"Engineer"}}""")]  // no description
    [InlineData("""{"error":"not found"}""")]
    [InlineData("not json at all")]
    public void Unusable_payloads_return_null(string json) =>
        Assert.Null(WorkdayPosting.ParseCxsJson(json));

    /// <summary>
    /// The regression this whole path exists for: the page a plain GET returns
    /// contains no extractable text whatsoever.
    /// </summary>
    [Fact]
    public void The_page_itself_strips_to_nothing()
    {
        var html = Fixture("workday-job-shell.html");
        Assert.True(html.Length > 10_000);
        Assert.Equal("", JobPostingFetcher.HtmlToText(html));
    }

    [Fact]
    public async Task Workday_url_resolves_from_the_cxs_endpoint()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("workday-cxs-job.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Contains("Brooks", result.Text);
        Assert.Equal("Brooks Automation US LLC", result.Company);
        Assert.Equal("Software Engineer - C# / Python (Contract Role)", result.Position);
        // One request only: the CXS hit short-circuits the page fetch.
        Assert.Single(handler.Requests);
        Assert.Contains("/wday/cxs/", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Falls_through_to_the_page_when_cxs_is_unavailable()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "nope", "text/plain");
        handler.Enqueue(HttpStatusCode.OK,
            "<html><body><p>" + new string('x', 400) + "</p></body></html>", "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.StartsWith("xxxx", result.Text);
        Assert.Equal(2, handler.Requests.Count);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}

/// <summary>AppConfig only loads from an .env file; this makes one per test class.</summary>
internal sealed class TempEnv : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "if-posting-" + Guid.NewGuid().ToString("N")[..8]);

    public AppConfig Config(string content)
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
}

public sealed class StructuredPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private const string JsonLdPage = """
        <html><head>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"JobPosting",
         "title":"Staff Engineer","datePosted":"2026-08-01","employmentType":"FULL_TIME",
         "hiringOrganization":{"@type":"Organization","name":"Acme Corp"},
         "jobLocation":{"@type":"Place","address":{"@type":"PostalAddress",
           "addressLocality":"Austin","addressRegion":"TX",
           "addressCountry":{"@type":"Country","name":"US"}}},
         "description":"<p>Build things.</p><ul><li>Ship code</li><li>Mentor peers</li></ul>"}
        </script>
        </head><body><div id="root"></div></body></html>
        """;

    [Fact]
    public void Reads_a_json_ld_job_posting()
    {
        var posting = StructuredPosting.Extract(JsonLdPage);
        var text = posting.Text;

        Assert.Equal("Staff Engineer", posting.Title);
        Assert.Equal("Acme Corp", posting.Company);
        Assert.StartsWith("Staff Engineer", text);
        Assert.Contains("Company: Acme Corp", text);
        Assert.Contains("Location: Austin, TX, US", text);
        Assert.Contains("Employment type: FULL_TIME", text);
        Assert.Contains("• Ship code", text);
        Assert.DoesNotContain("<li>", text);
    }

    [Fact]
    public void Finds_the_posting_inside_a_graph_array()
    {
        const string page = """
            <script type="application/ld+json">
            [{"@type":"WebSite","name":"Careers"},
             {"@type":["JobPosting"],"title":"SRE","description":"<p>Keep it up.</p>"}]
            </script>
            """;

        Assert.StartsWith("SRE", StructuredPosting.Extract(page).Text);
    }

    [Fact]
    public void Falls_back_to_opengraph_meta_tags()
    {
        const string page = """
            <html><head>
            <meta name="title" property="og:title" content="Software Engineer - C# / Python">
            <meta name="description" property="og:description" content="Brooks is a leading provider of automation solutions.">
            </head><body></body></html>
            """;

        var posting = StructuredPosting.Extract(page);
        Assert.Equal("Software Engineer - C# / Python\n\nBrooks is a leading provider of automation solutions.",
            posting.Text);
        Assert.Equal("Software Engineer - C# / Python", posting.Title);
    }

    [Fact]
    public void Opengraph_site_name_becomes_the_company()
    {
        const string page = """
            <meta property="og:site_name" content="Acme Careers">
            <meta property="og:title" content="Staff Engineer">
            <meta property="og:description" content="Build things at Acme.">
            """;

        var posting = StructuredPosting.Extract(page);
        Assert.Equal("Acme Careers", posting.Company);
        Assert.Equal("Staff Engineer", posting.Title);
    }

    [Fact]
    public void Reads_meta_tags_with_content_before_the_name()
    {
        const string page =
            """<meta content="A very good job indeed." property="og:description">""";

        Assert.Equal("A very good job indeed.", StructuredPosting.Extract(page).Text);
    }

    [Theory]
    [InlineData("<html><body>Loading…</body></html>")]
    [InlineData("""<script type="application/ld+json">{"@type":"WebSite"}</script>""")]
    [InlineData("""<script type="application/ld+json">{not json}</script>""")]
    [InlineData("""<script type="application/ld+json">{"@type":"JobPosting"}</script>""")] // no description
    public void Returns_empty_when_there_is_nothing_structured(string page) =>
        Assert.True(StructuredPosting.Extract(page).IsEmpty);

    /// <summary>
    /// The Workday shell that strips to zero characters turns out to carry the
    /// whole posting as JSON-LD inside a &lt;script&gt; — which the tag strip
    /// discards. Reading it recovers the posting with no provider call at all.
    /// </summary>
    [Fact]
    public void The_workday_shell_still_yields_the_posting_via_json_ld()
    {
        var html = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "workday-job-shell.html"));

        Assert.Equal("", JobPostingFetcher.HtmlToText(html)); // nothing survives the strip
        var posting = StructuredPosting.Extract(html);
        var text = posting.Text;

        Assert.Equal("Brooks Automation US LLC", posting.Company);
        Assert.StartsWith("Software Engineer - C# / Python (Contract Role)", text);
        Assert.Contains("Company: Brooks Automation US LLC", text);
        Assert.Contains("US - Fremont, CA", text);
        Assert.Contains("semiconductor", text);
        Assert.True(text.Length > 3000, $"expected the full posting, got {text.Length} chars");
    }

    [Fact]
    public async Task Json_ld_page_resolves_without_a_provider_call()
    {
        var description = "<p>" + string.Join("</p><p>", Enumerable.Repeat("Build and ship excellent software.", 12)) + "</p>";
        var page = $$"""
            <html><head><script type="application/ld+json">
            {"@type":"JobPosting","title":"Staff Engineer","description":{{System.Text.Json.JsonSerializer.Serialize(description)}}}
            </script></head><body><div id="app"></div></body></html>
            """;

        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, page, "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=anthropic\nANTHROPIC_API_KEY=sk-test\n"),
            "https://example.com/posting/1", TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.StartsWith("Staff Engineer", result.Text);
        Assert.Single(handler.Requests); // no LLM call
    }
}
