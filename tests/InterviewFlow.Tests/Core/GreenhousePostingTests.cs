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
    // A board embedded on the employer's own site: gh_jid is the job id, and
    // the board token is guessed from the domain name (the Roblox regression).
    [InlineData("https://careers.roblox.com/jobs/8171506?gh_jid=8171506",
        "https://boards-api.greenhouse.io/v1/boards/roblox/jobs/8171506")]
    [InlineData("https://www.acme.com/careers/openings?gh_jid=42&gh_src=abc",
        "https://boards-api.greenhouse.io/v1/boards/acme/jobs/42")]
    [InlineData("https://jobs.acme.co.uk/?gh_jid=42",
        "https://boards-api.greenhouse.io/v1/boards/acme/jobs/42")]
    public void Maps_posting_urls_to_the_board_api(string url, string expected) =>
        Assert.Equal(expected, GreenhousePosting.ApiUrl(url));

    [Theory]
    [InlineData("https://brooksauto.wd1.myworkdayjobs.com/site/job/x_R1")]  // not Greenhouse
    [InlineData("https://boards.greenhouse.io/acme")]                        // board root
    [InlineData("https://boards.greenhouse.io/jobs/12345")]                  // no board segment
    [InlineData("https://boards.greenhouse.io/embed/job_app?for=acme")]      // no token
    [InlineData("https://careers.roblox.com/jobs/8171506")]                  // no gh_jid: not known to be Greenhouse
    [InlineData("https://careers.roblox.com/jobs?gh_jid=abc")]               // gh_jid is numeric
    [InlineData("https://localhost/jobs?gh_jid=42")]                         // no domain label to guess from
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(GreenhousePosting.ApiUrl(url));

    [Theory]
    [InlineData("careers.roblox.com", "roblox")]
    [InlineData("www.acme.com", "acme")]
    [InlineData("acme.com", "acme")]
    [InlineData("jobs.acme.co.uk", "acme")]
    [InlineData("acme.com.au", "acme")]
    [InlineData("localhost", "")]
    public void Board_token_is_the_domain_name(string host, string expected) =>
        Assert.Equal(expected, GreenhousePosting.BoardFromHost(host));

    private const string EmbeddedPageUrl = "https://careers.roblox.com/jobs/8171506?gh_jid=8171506";

    [Theory]
    // The embed script names the board; the id comes from the page URL.
    [InlineData("""<script src="https://boards.greenhouse.io/embed/job_board/js?for=robloxcorp"></script>""",
        EmbeddedPageUrl, "https://boards-api.greenhouse.io/v1/boards/robloxcorp/jobs/8171506")]
    // The application frame names both, so a host page without gh_jid maps too.
    [InlineData("""<iframe src="https://boards.greenhouse.io/embed/job_app?for=robloxcorp&amp;token=8171506">""",
        "https://careers.roblox.com/jobs/8171506", "https://boards-api.greenhouse.io/v1/boards/robloxcorp/jobs/8171506")]
    // A board-hosted link on the page.
    [InlineData("""<a href="https://boards.greenhouse.io/robloxcorp/jobs/8171506">Apply</a>""",
        EmbeddedPageUrl, "https://boards-api.greenhouse.io/v1/boards/robloxcorp/jobs/8171506")]
    // Nothing Greenhouse on the page.
    [InlineData("<html><body>Apply now</body></html>", EmbeddedPageUrl, null)]
    // A board named, but no job id anywhere.
    [InlineData("""<script src="https://boards.greenhouse.io/embed/job_board/js?for=robloxcorp"></script>""",
        "https://careers.roblox.com/jobs/8171506", null)]
    public void Reads_the_board_from_an_embedding_page(string html, string url, string? expected) =>
        Assert.Equal(expected, GreenhousePosting.ApiUrlFromPage(html, url));

    /// <summary>
    /// The Roblox regression: a Next.js careers site embedding Greenhouse. The
    /// page scrape "worked" — it stored the site menus, five related jobs and
    /// the footer around the posting, with the title as "… | Roblox".
    /// </summary>
    [Fact]
    public async Task Embedded_board_resolves_from_the_board_api()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("greenhouse-embedded-job.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=ollama\n"), EmbeddedPageUrl,
            TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Equal("Roblox", result.Company);
        Assert.Equal("Software Engineer, Engine Infrastructure", result.Position);
        Assert.Contains("Location: San Mateo, CA, United States", result.Text);
        Assert.Contains("Requisition: 41464", result.Text);
        Assert.Contains("\nYou will:\n", result.Text);
        Assert.Contains("\n• Develop engine code in C++", result.Text);
        Assert.DoesNotContain("Related Jobs", result.Text);
        Assert.DoesNotContain("Skip to content", result.Text);
        Assert.Single(handler.Requests);
        Assert.Equal("https://boards-api.greenhouse.io/v1/boards/roblox/jobs/8171506", handler.Requests[0].Url);
    }

    /// <summary>
    /// When the domain name is not the board token, the page's embed script is.
    /// </summary>
    [Fact]
    public async Task Embedded_board_falls_back_to_the_token_the_page_names()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"status":404,"error":"Job not found"}""", "application/json");
        handler.Enqueue(HttpStatusCode.OK,
            """<html><head><script src="https://boards.greenhouse.io/embed/job_board/js?for=robloxcorp"></script>"""
            + "</head><body><nav>Careers</nav></body></html>", "text/html");
        handler.Enqueue(HttpStatusCode.OK, Fixture("greenhouse-embedded-job.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=ollama\n"), EmbeddedPageUrl,
            TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.Equal("Roblox", result.Company);
        Assert.Equal("Software Engineer, Engine Infrastructure", result.Position);
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/boards/roblox/jobs/8171506", handler.Requests[0].Url);
        Assert.Equal(EmbeddedPageUrl, handler.Requests[1].Url);
        Assert.EndsWith("/boards/robloxcorp/jobs/8171506", handler.Requests[2].Url);
    }

    [Theory]
    [InlineData("Software Engineer, Engine Infrastructure | Roblox", "Roblox", "Software Engineer, Engine Infrastructure")]
    [InlineData("Staff Engineer - Acme", "Acme", "Staff Engineer")]
    [InlineData("Staff Engineer at Acme", "Acme", "Staff Engineer")]
    [InlineData("Staff Engineer | Acme", "acme", "Staff Engineer")]   // case-insensitive
    [InlineData("Staff Engineer | Acme", "Other Co", "Staff Engineer | Acme")]
    [InlineData("Staff Engineer | Acme", "", "Staff Engineer | Acme")]
    [InlineData(" | Acme", "Acme", " | Acme")]                          // nothing left: keep as is
    public void Site_suffix_is_dropped_from_the_title(string title, string company, string expected) =>
        Assert.Equal(expected, StructuredPosting.WithoutSiteSuffix(title, company));

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
