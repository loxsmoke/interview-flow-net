using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// ADP WorkforceNow postings (docs/05 §5.7). Fixtures are the real captured
/// responses for one posting: the Angular shell a browser-shaped GET of the
/// career-center page returns, and the requisition JSON its public staffing
/// endpoint serves for the same ids.
/// </summary>
public sealed class AdpPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string PostingUrl =
        "https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?cid=6aef1f75-5949-4f59-9b6d-6919536eaa99&ccId=19000101_000001&jobId=597987&lang=en_US";

    private const string ApiUrl =
        "https://workforcenow.adp.com/mascsr/default/careercenter/public/events/staffing/v1/job-requisitions/597987"
        + "?cid=6aef1f75-5949-4f59-9b6d-6919536eaa99&ccId=19000101_000001&locale=en_US";

    [Theory]
    [InlineData(PostingUrl, ApiUrl)]
    // Parameter order on the page is whatever the link carried; a source tag
    // and a router fragment are dropped.
    [InlineData("https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?jobId=597987&lang=en_US&source=CC2&ccId=19000101_000001&cid=6aef1f75-5949-4f59-9b6d-6919536eaa99#/",
        ApiUrl)]
    // No lang: the endpoint picks its own locale.
    [InlineData("https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?cid=abc&ccId=19000101_000001&jobId=42",
        "https://workforcenow.adp.com/mascsr/default/careercenter/public/events/staffing/v1/job-requisitions/42"
        + "?cid=abc&ccId=19000101_000001")]
    // The cloud host serves the same career center.
    [InlineData("https://workforcenow.cloud.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?cid=abc&ccId=1&jobId=42&lang=fr_CA",
        "https://workforcenow.cloud.adp.com/mascsr/default/careercenter/public/events/staffing/v1/job-requisitions/42"
        + "?cid=abc&ccId=1&locale=fr_CA")]
    // Already the endpoint.
    [InlineData(ApiUrl, ApiUrl)]
    public void Maps_posting_urls_to_the_requisition_endpoint(string url, string expected) =>
        Assert.Equal(expected, AdpPosting.ApiUrl(url));

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123")]                        // not ADP
    [InlineData("https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?cid=abc&ccId=1&type=MP&lang=en_US&selectedMenuKey=CareerCenter")]        // listing, no jobId
    [InlineData("https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?ccId=1&jobId=42")]                                                        // no client id
    [InlineData("https://workforcenow.adp.com/mascsr/default/mdf/recruitment/recruitment.html"
        + "?cid=abc&ccId=1&jobId=../x")]                                              // not a requisition id
    [InlineData("https://workforcenow.adp.com/theme/index.html?cid=abc&ccId=1&jobId=42")] // not the career center
    [InlineData("https://myjobs.adp.com/acme/cx/job-details?req=1")]                  // another ADP product
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(AdpPosting.ApiUrl(url));

    [Fact]
    public void Parses_the_real_requisition_payload()
    {
        var posting = AdpPosting.ParseJobJson(Fixture("adp-job.json"));

        Assert.NotNull(posting);
        Assert.Equal("Senior Software Engineer", posting!.Title);
        // ADP never names the client in the payload; Setup leaves Company to the user.
        Assert.Equal("", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Senior Software Engineer", text);
        Assert.Contains("Location: Portsmouth, NH, US", text);
        Assert.Contains("Employment type: Full-time Regular", text);
        Assert.Contains("Requisition: 1361", text);
        // The description survives as readable text, inline styles and all gone.
        Assert.Contains("PlaneSense", text);
        Assert.Contains("\n• ", text);
        Assert.DoesNotContain("<p", text);
        Assert.DoesNotContain("font-family", text);
        Assert.True(text.Length > 3000, $"expected the full posting, got {text.Length} chars");
    }

    [Theory]
    [InlineData("""{"requisitionTitle":"Engineer"}""")]  // no description
    [InlineData("""{"timestamp":"2026-09-06T02:42:38.914+00:00","status":500,"error":"Internal Server Error"}""")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    public void Unusable_payloads_return_null(string json) =>
        Assert.Null(AdpPosting.ParseJobJson(json));

    /// <summary>
    /// The regression this path exists for: the career-center page is a shell
    /// with no structured data, and its text is a browser-compatibility notice.
    /// </summary>
    [Fact]
    public void The_page_itself_is_an_empty_shell()
    {
        var html = Fixture("adp-job-shell.html");
        Assert.True(html.Length > 10_000);
        Assert.True(HtmlText.PageToText(html).Length < 200);
        Assert.True(StructuredPosting.Extract(html).IsEmpty);
        Assert.Null(JobPostingFetcher.SameHostFrameUrl(html, PostingUrl));
    }

    [Fact]
    public async Task Adp_url_resolves_from_the_requisition_endpoint()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("adp-job.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Contains("PlaneSense", result.Text);
        Assert.Equal("Senior Software Engineer", result.Position);
        Assert.Equal("", result.Company);
        // One request only: the endpoint hit short-circuits the page fetch.
        Assert.Single(handler.Requests);
        Assert.Equal(ApiUrl, handler.Requests[0].Url);
    }

    [Fact]
    public async Task Shell_alone_cannot_be_resolved()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "nope", "text/plain");
        handler.Enqueue(HttpStatusCode.OK, Fixture("adp-job-shell.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal(JobPostingFetcher.CouldNotExtractMessage, result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}
