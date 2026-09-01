using System.Net;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// Greenhouse postings (docs/05 §5.7). Fixtures are the real captured responses
/// for one posting: the board-API payload and the head/opening of the public
/// page. The page scrapes fine but names the employer only in its &lt;title&gt;
/// and drags in site chrome — which is why the API path exists.
/// </summary>
public sealed class GreenhousePostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string PostingUrl = "https://job-boards.greenhouse.io/caredxinc/jobs/4272026009";

    [Theory]
    [InlineData("https://job-boards.greenhouse.io/caredxinc/jobs/4272026009",
        "https://boards-api.greenhouse.io/v1/boards/caredxinc/jobs/4272026009")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/12345",
        "https://boards-api.greenhouse.io/v1/boards/acme/jobs/12345")]
    // EU-region boards answer on their own API host.
    [InlineData("https://job-boards.eu.greenhouse.io/acme/jobs/12345",
        "https://boards-api.eu.greenhouse.io/v1/boards/acme/jobs/12345")]
    // The embedded application form carries board + id in the query string.
    [InlineData("https://boards.greenhouse.io/embed/job_app?for=acme&token=12345",
        "https://boards-api.greenhouse.io/v1/boards/acme/jobs/12345")]
    public void Maps_posting_urls_to_the_board_api(string url, string expected) =>
        Assert.Equal(expected, GreenhousePosting.ApiUrl(url));

    [Theory]
    [InlineData("https://brooksauto.wd1.myworkdayjobs.com/site/job/x_R1")]  // not Greenhouse
    [InlineData("https://boards.greenhouse.io/acme")]                        // board root
    [InlineData("https://boards.greenhouse.io/jobs/12345")]                  // no board segment
    [InlineData("https://boards.greenhouse.io/embed/job_app?for=acme")]      // no token
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(GreenhousePosting.ApiUrl(url));

    [Fact]
    public void Parses_the_real_board_api_payload()
    {
        var posting = GreenhousePosting.ParseJobJson(Fixture("greenhouse-job.json"));

        Assert.NotNull(posting);
        Assert.Equal("Staff Software Engineer", posting!.Title);
        Assert.Equal("CareDx, Inc.", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Staff Software Engineer", text);
        Assert.Contains("Company: CareDx, Inc.", text);
        Assert.Contains("Location: Brisbane, CA", text);
        // The body keeps its structure: headings on their own lines, real bullets.
        Assert.Contains("\nKey Responsibilities\n", text);
        Assert.Contains("\n• ", text);
        Assert.DoesNotContain("<p", text);
        Assert.DoesNotContain("&lt;", text);   // content is escaped once in the JSON
        Assert.DoesNotContain("&nbsp;", text);
        // Site chrome the page scrape used to drag in.
        Assert.DoesNotContain("Back to jobs", text);
        Assert.True(text.Split('\n').Length > 50, "expected a multi-line posting");
    }

    [Theory]
    [InlineData("""{"title":"Engineer"}""")]      // no content
    [InlineData("""{"error":"not found"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Unusable_payloads_return_null(string json) =>
        Assert.Null(GreenhousePosting.ParseJobJson(json));

    [Fact]
    public async Task Greenhouse_url_resolves_from_the_board_api()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("greenhouse-job.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=ollama\n"), PostingUrl,
            TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Equal("CareDx, Inc.", result.Company);
        Assert.Equal("Staff Software Engineer", result.Position);
        Assert.Single(handler.Requests); // the API hit short-circuits the page
        Assert.Contains("boards-api.greenhouse.io", handler.Requests[0].Url);
    }

    /// <summary>
    /// If the API ever stops answering, the page still resolves — with the
    /// employer recovered from the document title rather than lost.
    /// </summary>
    [Fact]
    public async Task Page_fallback_still_names_the_company()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}", "application/json");
        handler.Enqueue(HttpStatusCode.OK, Fixture("greenhouse-job-page.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=ollama\n"), PostingUrl,
            TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.Equal("CareDx, Inc.", result.Company);
        Assert.Equal("Staff Software Engineer", result.Position);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    // Greenhouse's own title shape.
    [InlineData("<title>Job Application for Staff Software Engineer at CareDx, Inc.</title>", "CareDx, Inc.")]
    // og:site_name wins when present.
    [InlineData("""<meta property="og:site_name" content="Acme Careers"><title>Engineer at Ignored</title>""",
        "Acme Careers")]
    [InlineData("<title>Careers</title>", "")]
    [InlineData("<html><body>no title</body></html>", "")]
    public void Company_is_recovered_from_the_page_shell(string html, string expected) =>
        Assert.Equal(expected, StructuredPosting.CompanyFromPage(html));
}

/// <summary>
/// The block-aware strip behind every resolved posting (docs/05 §5.7): the flat
/// <c>HtmlToText</c> port collapses a whole posting onto one line, which is what
/// the Greenhouse page used to produce.
/// </summary>
public sealed class HtmlTextTests
{
    private const string Posting = """
        <html><head><style>.x{color:red}</style><script>evil()</script></head>
        <body><h2>Key Responsibilities</h2>
        <ul><li><p>Ship code</p></li><li><p>Mentor peers</p></li></ul>
        <p>Apply&nbsp;today &amp; say hi.</p></body></html>
        """;

    [Fact]
    public void Page_text_keeps_headings_and_bullets()
    {
        var text = HtmlText.PageToText(Posting);

        Assert.Contains("Key Responsibilities", text);
        Assert.Contains("• Ship code", text);
        Assert.Contains("• Mentor peers", text);
        Assert.DoesNotContain("evil()", text);   // script dropped
        Assert.DoesNotContain("color:red", text); // style dropped
        Assert.True(text.Split('\n').Length >= 4, $"expected several lines, got:\n{text}");
    }

    [Fact]
    public void The_flat_port_is_what_lost_the_structure()
    {
        // Kept as the parity reference; no longer what the pipeline stores.
        var flat = JobPostingFetcher.HtmlToText(Posting);
        Assert.Single(flat.Split('\n'));
        Assert.DoesNotContain("•", flat);
    }

    [Fact]
    public void Entities_decode_and_blank_runs_collapse()
    {
        var text = HtmlText.FragmentToText("<p>A</p><p></p><p></p><p>B &amp; C</p>");
        Assert.Equal("A\n\nB & C", text);
    }

    [Fact]
    public void Empty_input_is_empty_output()
    {
        Assert.Equal("", HtmlText.PageToText(""));
        Assert.Equal("", HtmlText.FragmentToText(""));
    }
}
