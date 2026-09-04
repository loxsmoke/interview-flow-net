using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// iCIMS postings (docs/05 §5.7). Fixtures are the real captured responses for
/// one posting: the wrapper a browser-shaped GET of the job URL returns (the
/// employer's corporate site around an empty frame), and the frame document
/// (the same URL with in_iframe=1) that carries the posting as JSON-LD.
/// </summary>
public sealed class IcimsPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string PostingUrl =
        "https://careers-cotiviti.icims.com/jobs/19533/software-engineer---sql---c%23---.net---/job" +
        "?mobile=false&width=1100&height=500&bga=true&needsRedirect=false&jan1offset=-480&jun1offset=-420";

    private const string FrameUrl = PostingUrl + "&in_iframe=1";

    [Theory]
    // The wrapper's own query string is kept; the frame flag is appended.
    [InlineData(PostingUrl, FrameUrl)]
    // Bare job URL, no query.
    [InlineData("https://careers-acme.icims.com/jobs/123/job",
        "https://careers-acme.icims.com/jobs/123/job?in_iframe=1")]
    // Slug form without the trailing /job.
    [InlineData("https://jobs-acme.icims.com/jobs/4567/staff-engineer/job?mode=job",
        "https://jobs-acme.icims.com/jobs/4567/staff-engineer/job?mode=job&in_iframe=1")]
    // Already the frame document.
    [InlineData("https://careers-acme.icims.com/jobs/123/job?in_iframe=1",
        "https://careers-acme.icims.com/jobs/123/job?in_iframe=1")]
    public void Maps_posting_urls_to_the_frame_document(string url, string expected) =>
        Assert.Equal(expected, IcimsPosting.FrameUrl(url));

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123")]          // not iCIMS
    [InlineData("https://careers-acme.icims.com/jobs/search?ss=1")]     // search, no id
    [InlineData("https://careers-acme.icims.com/jobs/intro")]           // landing page
    [InlineData("https://www.icims.com/jobs")]                          // vendor site
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(IcimsPosting.FrameUrl(url));

    [Fact]
    public void Parses_the_real_frame_document()
    {
        var posting = IcimsPosting.ParseFrameHtml(Fixture("icims-job-iframe.html"));

        Assert.NotNull(posting);
        Assert.Equal("Cotiviti", posting!.Company);
        Assert.StartsWith("Software Engineer - SQL / C# / .Net", posting.Title);

        var text = posting.Text;
        Assert.Contains("Company: Cotiviti", text);
        // iCIMS pads the address with "UNAVAILABLE"; only the real parts remain.
        Assert.Contains("Location: Remote, US", text);
        Assert.DoesNotContain("UNAVAILABLE", text);
        Assert.Contains("Responsibilities", text);
        Assert.Contains("Qualifications", text);
        Assert.Contains("•", text);
        Assert.DoesNotContain("<p", text);
        Assert.DoesNotContain("Main Menu", text);
        Assert.True(text.Length > 3000, $"expected the full posting, got {text.Length} chars");
    }

    [Fact]
    public void A_frame_document_without_json_ld_is_a_miss() =>
        Assert.Null(IcimsPosting.ParseFrameHtml("<html><body><p>Loading…</p></body></html>"));

    /// <summary>
    /// The regression this path exists for: the wrapper strips to far more than
    /// the thin-page threshold — all of it site navigation — carries no
    /// structured data, and keeps the posting behind a same-host frame.
    /// </summary>
    [Fact]
    public void The_wrapper_is_site_navigation_around_a_frame()
    {
        var html = Fixture("icims-job-shell.html");
        var text = HtmlText.PageToText(html);

        Assert.True(text.Length > 1000, $"got {text.Length} chars");
        Assert.Contains("Main Menu", text);
        Assert.DoesNotContain("Responsibilities", text);
        Assert.True(StructuredPosting.Extract(html).IsEmpty);
        Assert.Equal(FrameUrl, JobPostingFetcher.SameHostFrameUrl(html, PostingUrl));
    }

    [Fact]
    public async Task Icims_url_resolves_from_the_frame_document()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("icims-job-iframe.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Equal("Cotiviti", result.Company);
        Assert.StartsWith("Software Engineer - SQL", result.Position);
        Assert.Contains("Qualifications", result.Text);
        Assert.DoesNotContain("Main Menu", result.Text);
        // One request only: the frame document short-circuits the wrapper fetch.
        Assert.Single(handler.Requests);
        Assert.EndsWith("&in_iframe=1", handler.Requests[0].Url);
    }

    /// <summary>
    /// Layering: when the direct frame request fails, the wrapper is fetched
    /// like any page, and the generic frame follow finds the posting anyway.
    /// </summary>
    [Fact]
    public async Task Falls_back_to_following_the_wrappers_frame()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "later", "text/plain");
        handler.Enqueue(HttpStatusCode.OK, Fixture("icims-job-shell.html"), "text/html");
        handler.Enqueue(HttpStatusCode.OK, Fixture("icims-job-iframe.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.Null(result.Error);
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("&in_iframe=1", handler.Requests[2].Url);
        Assert.Equal("Cotiviti", result.Company);
        Assert.Contains("Qualifications", result.Text);
        Assert.DoesNotContain("Main Menu", result.Text);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}

/// <summary>
/// The generic same-host frame follow in step 2 (docs/05 §5.7), independent of
/// any board handler.
/// </summary>
public sealed class FramedPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private const string PageUrl = "https://example.com/careers/42";

    /// <summary>A wrapper: menus well past the threshold, then one frame.</summary>
    private static string Wrapper(string frameSrc) =>
        "<html><body><nav>" + string.Join(" ", Enumerable.Repeat("Main Menu Overview", 40)) +
        $"</nav><iframe src=\"{frameSrc}\" width=\"100%\"></iframe></body></html>";

    private static string Posting(int paragraphs) =>
        "<html><body><h1>Staff Engineer</h1>" +
        string.Concat(Enumerable.Repeat("<p>Build and ship excellent software.</p>", paragraphs)) +
        "</body></html>";

    [Fact]
    public async Task A_same_host_frame_is_followed_when_the_page_has_no_structured_data()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Wrapper("/careers/42?embed=1"), "text/html");
        handler.Enqueue(HttpStatusCode.OK, Posting(60), "text/html"); // longer than the menus

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PageUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://example.com/careers/42?embed=1", handler.Requests[1].Url);
        Assert.StartsWith("Staff Engineer", result.Text);
        Assert.DoesNotContain("Main Menu", result.Text);
    }

    [Fact]
    public async Task Cross_host_frames_are_not_followed()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Wrapper("https://www.googletagmanager.com/ns.html?id=X"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PageUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Single(handler.Requests);
        Assert.Contains("Main Menu", result.Text); // the page itself, as before
    }

    /// <summary>
    /// A real posting page with an application form framed beside it: the form
    /// clears the threshold too, but says less than the page, so the page wins.
    /// </summary>
    [Fact]
    public async Task A_frame_that_says_less_than_the_page_does_not_replace_it()
    {
        var page = Posting(20).Replace("</body>", "<iframe src=\"/careers/42/apply\"></iframe></body>");
        var form = "<html><body><form>" +
                   string.Concat(Enumerable.Repeat("<p>Name, email, resume upload, submit.</p>", 8)) +
                   "</form></body></html>";
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, page, "text/html");
        handler.Enqueue(HttpStatusCode.OK, form, "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PageUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("Staff Engineer", result.Text);
        Assert.DoesNotContain("resume upload", result.Text);
    }

    [Fact]
    public async Task A_page_with_its_own_structured_data_is_not_followed()
    {
        var page = Wrapper("/careers/42?embed=1").Replace("<html>",
            """<html><script type="application/ld+json">{"@type":"JobPosting","title":"SRE","description":"<p>Keep it up.</p>"}</script>""");
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, page, "text/html");

        await JobPostingFetcher.ResolveAsync(
            Config(), PageUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("/careers/42?embed=1", "https://example.com/careers/42?embed=1")]   // relative
    [InlineData("//example.com/frame", "https://example.com/frame")]           // scheme-relative
    [InlineData("https://EXAMPLE.com/frame", "https://example.com/frame")]     // host case
    [InlineData("about:blank", null)]
    [InlineData("https://www.example.org/frame", null)]                                  // another host
    [InlineData(PageUrl, null)]                                                          // itself
    public void Same_host_frame_url_resolves_relative_sources(string src, string? expected) =>
        Assert.Equal(expected, JobPostingFetcher.SameHostFrameUrl(Wrapper(src), PageUrl));

    [Fact]
    public void The_first_same_host_frame_wins_over_earlier_third_party_ones()
    {
        var html = "<iframe src=\"https://vars.hotjar.com/box.html\"></iframe>" +
                   "<iframe id=\"content\" src=\"/careers/42?embed=1\"></iframe>";
        Assert.Equal("https://example.com/careers/42?embed=1",
            JobPostingFetcher.SameHostFrameUrl(html, PageUrl));
    }

    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}
