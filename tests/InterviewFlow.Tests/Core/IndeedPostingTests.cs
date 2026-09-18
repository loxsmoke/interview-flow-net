using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// Indeed postings (docs/05 §5.7). Fixtures are the real captured responses
/// for one posting: the viewjob page's own JSON (`spa=1`), the description
/// RPC, and the 403 "Security Check" challenge every plain page fetch gets
/// (script, style and svg blocks removed).
/// </summary>
public sealed class IndeedPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string EmailUrl =
        "https://www.indeed.com/viewjob?jk=fb7d36a03f4830f4&tk=1k2n897ejlf6s804&from=jobi2a_jobmatch-reactivation-en-US_email&rjptk=1k2n896bipefu805&xpse=SoCr67I2eZJE5FylyB0LbzkdCdPP&xfps=85c65c04-1559-46a0-a206-b2d1137505b7&xkcb=SoDK67M2eXYq-sWB4Z0ObzkdCdPP";

    private const string SpaUrl = "https://www.indeed.com/viewjob?jk=fb7d36a03f4830f4&spa=1";
    private const string DescriptionsUrl = "https://www.indeed.com/rpc/jobdescs?jks=fb7d36a03f4830f4";

    [Theory]
    [InlineData(EmailUrl)]
    [InlineData("https://www.indeed.com/viewjob?jk=fb7d36a03f4830f4")]
    [InlineData("https://www.indeed.com/jobs?q=engineer&l=Remote&vjk=fb7d36a03f4830f4")]
    [InlineData("https://www.indeed.com/m/viewjob?jk=fb7d36a03f4830f4")]
    [InlineData("https://www.indeed.com/rc/clk?jk=fb7d36a03f4830f4&from=serp")]
    [InlineData(SpaUrl)]
    [InlineData(DescriptionsUrl)]
    public void Maps_posting_urls_to_both_endpoints(string url)
    {
        Assert.Equal("fb7d36a03f4830f4", IndeedPosting.JobKey(url));
        Assert.Equal(SpaUrl, IndeedPosting.SpaUrl(url));
        Assert.Equal(DescriptionsUrl, IndeedPosting.DescriptionsUrl(url));
    }

    [Fact]
    public void Country_sites_keep_their_own_host()
    {
        const string url = "https://uk.indeed.com/viewjob?jk=0123456789abcdef&from=email";
        Assert.Equal("https://uk.indeed.com/viewjob?jk=0123456789abcdef&spa=1", IndeedPosting.SpaUrl(url));
        Assert.Equal("https://uk.indeed.com/rpc/jobdescs?jks=0123456789abcdef", IndeedPosting.DescriptionsUrl(url));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/jobs/view/4466229826/")]          // not Indeed
    [InlineData("https://www.indeed.com/jobs?q=engineer&l=Remote")]          // search, no job selected
    [InlineData("https://www.indeed.com/cmp/Indeed/jobs")]                   // company page
    [InlineData("https://www.indeed.com/viewjob?jk=not-a-key")]
    [InlineData("https://www.indeed.com/viewjob?jk=FB7D36A03F4830F4X")]      // wrong length
    [InlineData("https://notindeed.com/viewjob?jk=fb7d36a03f4830f4")]
    public void Leaves_non_posting_urls_alone(string url)
    {
        Assert.Null(IndeedPosting.JobKey(url));
        Assert.Null(IndeedPosting.SpaUrl(url));
        Assert.Null(IndeedPosting.DescriptionsUrl(url));
    }

    [Fact]
    public void Parses_the_real_viewjob_json()
    {
        var posting = IndeedPosting.ParseSpaJson(Fixture("indeed-viewjob-spa.json"));

        Assert.NotNull(posting);
        Assert.Equal("Software Engineer IV", posting!.Title);
        Assert.Equal("Indeed", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Software Engineer IV\nCompany: Indeed\nLocation: Remote\n", text);
        Assert.Contains("Pay: $140,000 - $293,000 a year", text);
        Assert.Contains("Job type: Full-time", text);
        Assert.Contains("Benefits: Paid parental leave, Parental leave, 401(k), Health insurance", text);
        Assert.Contains("Our Mission", text);
        Assert.Contains("Day to Day", text);
        Assert.Contains("Reference ID: 47661", text);
        Assert.Contains("world’s number 1 job site", text);   // entities decoded
        Assert.DoesNotContain("<", text);
        Assert.True(text.Length > 5000, $"expected the full posting, got {text.Length} chars");
    }

    [Fact]
    public void Parses_the_real_descriptions_rpc()
    {
        var posting = IndeedPosting.ParseDescriptionsJson(Fixture("indeed-jobdescs.json"));

        Assert.NotNull(posting);
        Assert.Equal("", posting!.Title);
        Assert.Equal("", posting.Company);
        Assert.StartsWith("Our Mission", posting.Text);
        Assert.Contains("Software Engineer IV at Indeed", posting.Text);
        Assert.Contains("Reference ID: 47661", posting.Text);
        Assert.DoesNotContain("<", posting.Text);
    }

    [Fact]
    public void Remote_flag_is_added_when_the_location_does_not_say_so()
    {
        const string json = """
            {"status":"success","body":{"jobTitle":"SRE","salaryInfoModel":{"salaryText":"$1 a year"},
             "jobInfoWrapperModel":{"jobInfoModel":{
               "jobInfoHeaderModel":{"companyName":"Acme","formattedLocation":"Austin, TX","remoteLocation":true},
               "jobMetadataHeaderModel":{"jobType":"Contract"},
               "sanitizedJobDescription":"<p>Keep it up.</p>"}}}}
            """;

        var posting = IndeedPosting.ParseSpaJson(json);

        Assert.NotNull(posting);
        Assert.Equal("SRE", posting!.Title);       // header had no title; the body's own is used
        Assert.Equal("SRE\nCompany: Acme\nLocation: Austin, TX (Remote)\nPay: $1 a year\nJob type: Contract\n\nKeep it up.", posting.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"status":"error","body":null}""")]
    [InlineData("""{"status":"success","body":{"jobTitle":"SRE"}}""")]                                   // no description
    [InlineData("""{"status":"success","body":{"jobInfoWrapperModel":{"jobInfoModel":{"sanitizedJobDescription":"<div></div>"}}}}""")]
    public void Unusable_viewjob_payloads_return_null(string json) =>
        Assert.Null(IndeedPosting.ParseSpaJson(json));

    [Theory]
    [InlineData("")]
    [InlineData("<html><title>Security Check - Indeed.com</title></html>")]
    [InlineData("{}")]
    [InlineData("""{"fb7d36a03f4830f4":""}""")]
    [InlineData("""{"fb7d36a03f4830f4":null}""")]
    [InlineData("[]")]
    public void Unusable_description_payloads_return_null(string json) =>
        Assert.Null(IndeedPosting.ParseDescriptionsJson(json));

    /// <summary>What every plain page fetch gets: a challenge, not a posting.</summary>
    [Fact]
    public void The_page_is_a_security_check_with_nothing_to_read()
    {
        var html = Fixture("indeed-security-check.html");
        Assert.Contains("Security Check", html);
        Assert.True(StructuredPosting.FromJsonLd(html).IsEmpty);
        Assert.DoesNotContain("Software Engineer", HtmlText.PageToText(html));
    }

    [Fact]
    public async Task Indeed_url_resolves_from_the_viewjob_json()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("indeed-viewjob-spa.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), EmailUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Equal("Indeed", result.Company);
        Assert.Equal("Software Engineer IV", result.Position);
        Assert.Contains("Pay: $140,000 - $293,000 a year", result.Text);
        Assert.Single(handler.Requests);
        Assert.Equal(SpaUrl, handler.Requests[0].Url);
        // The bot check lets QUIC through and refuses HTTP/1.1 and HTTP/2.
        Assert.Equal(HttpVersion.Version30, handler.Requests[0].Version);
    }

    /// <summary>
    /// Refuses every HTTP/3 request the way a platform without QUIC does (no
    /// response at all), and answers the ordinary retry.
    /// </summary>
    private sealed class NoQuicHandler(FakeHandler inner) : HttpMessageHandler
    {
        public int RefusedHttp3 { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Version.Major == 3)
            {
                RefusedHttp3++;
                throw new HttpRequestException("The requested HTTP version is not supported (QUIC unavailable).");
            }

            // Not a second HttpClient: it would refuse a message the outer one already marked as sent.
            return new HttpMessageInvoker(inner).SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Without_quic_the_request_is_retried_the_ordinary_way()
    {
        var inner = new FakeHandler();
        inner.Enqueue(HttpStatusCode.OK, Fixture("indeed-viewjob-spa.json"), "application/json");
        var handler = new NoQuicHandler(inner);

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), EmailUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal("Software Engineer IV", result.Position);
        Assert.Equal(1, handler.RefusedHttp3);
        var retry = Assert.Single(inner.Requests);
        Assert.Equal(SpaUrl, retry.Url);
        Assert.NotEqual(3, retry.Version.Major);
    }

    [Fact]
    public async Task Other_boards_do_not_ask_for_http3()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("linkedin-guest-posting.html"), "text/html");

        await JobPostingFetcher.ResolveAsync(
            Config(), "https://www.linkedin.com/jobs/view/4466229826/",
            TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.NotEqual(3, Assert.Single(handler.Requests).Version.Major);
    }

    /// <summary>
    /// The bot check answers the JSON endpoint with a 403 challenge roughly
    /// every other request. A second try, then the description RPC — which
    /// resolves the posting but cannot name it.
    /// </summary>
    [Fact]
    public async Task Challenged_twice_falls_back_to_the_description_rpc()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, Fixture("indeed-security-check.html"), "text/html");
        handler.Enqueue(HttpStatusCode.Forbidden, Fixture("indeed-security-check.html"), "text/html");
        handler.Enqueue(HttpStatusCode.OK, Fixture("indeed-jobdescs.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), EmailUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Equal("", result.Company);
        Assert.Equal("", result.Position);
        Assert.StartsWith("Our Mission", result.Text);
        Assert.Equal([SpaUrl, SpaUrl, DescriptionsUrl], handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task Second_try_of_the_viewjob_json_can_succeed()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, Fixture("indeed-security-check.html"), "text/html");
        handler.Enqueue(HttpStatusCode.OK, Fixture("indeed-viewjob-spa.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), EmailUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal("Indeed", result.Company);
        Assert.Equal("Software Engineer IV", result.Position);
        Assert.Equal(2, handler.Requests.Count);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}
