using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// SmartRecruiters postings (docs/05 §5.7). Fixtures are the real captured
/// responses for one posting: the public Posting API payload, and the job page
/// with its script, style and svg blocks removed (the strip discards those
/// anyway; what remains is the markup the page path actually reads).
/// </summary>
public sealed class SmartRecruitersPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string PostingUrl =
        "https://jobs.smartrecruiters.com/LinkedIn3/744000149684236-software-engineer-ai-platform";

    private const string ApiUrl =
        "https://api.smartrecruiters.com/v1/companies/LinkedIn3/postings/744000149684236";

    [Theory]
    [InlineData(PostingUrl, ApiUrl)]
    // The apply link is the same page with a flag; tracking parameters are dropped.
    [InlineData(PostingUrl + "?oga=true&trid=abc", ApiUrl)]
    // No slug.
    [InlineData("https://jobs.smartrecruiters.com/LinkedIn3/744000149684236", ApiUrl)]
    // A hosted career site serves the same posting ids.
    [InlineData("https://careers.smartrecruiters.com/Acme/743999000000001-staff-engineer",
        "https://api.smartrecruiters.com/v1/companies/Acme/postings/743999000000001")]
    // Already the endpoint.
    [InlineData(ApiUrl, ApiUrl)]
    [InlineData(ApiUrl + "?x=1", ApiUrl)]
    public void Maps_posting_urls_to_the_posting_api(string url, string expected) =>
        Assert.Equal(expected, SmartRecruitersPosting.ApiUrl(url));

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123")]                              // not SmartRecruiters
    [InlineData("https://jobs.smartrecruiters.com/LinkedIn3")]                              // company listing
    [InlineData("https://jobs.smartrecruiters.com/LinkedIn3/")]
    [InlineData("https://jobs.smartrecruiters.com/")]
    [InlineData("https://jobs.smartrecruiters.com/LinkedIn3/software-engineer")]           // no id
    [InlineData("https://jobs.smartrecruiters.com/oneclick-ui/company/LinkedIn3/publication/11df278c")] // apply flow
    [InlineData("https://www.smartrecruiters.com/customers/linkedin")]                     // vendor site
    [InlineData("https://api.smartrecruiters.com/v1/companies/LinkedIn3/postings")]         // listing endpoint
    public void Leaves_non_posting_urls_alone(string url) =>
        Assert.Null(SmartRecruitersPosting.ApiUrl(url));

    [Fact]
    public void Parses_the_real_posting_payload()
    {
        var posting = SmartRecruitersPosting.ParsePostingJson(Fixture("smartrecruiters-posting.json"));

        Assert.NotNull(posting);
        Assert.Equal("Software Engineer - AI Platform", posting!.Title);
        Assert.Equal("LinkedIn", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Software Engineer - AI Platform\nCompany: LinkedIn\n", text);
        Assert.Contains("Location: Mountain View, CA, United States (Hybrid)", text);
        Assert.Contains("Employment type: Full-time", text);
        Assert.Contains("Experience level: Associate", text);
        Assert.Contains("Requisition: REF26412S", text);
        // The ad's sections keep their titles, in the order the payload lists them.
        var company = text.IndexOf("\nCompany Description\n", StringComparison.Ordinal);
        var job = text.IndexOf("\nJob Description\n", StringComparison.Ordinal);
        var quals = text.IndexOf("\nQualifications\n", StringComparison.Ordinal);
        var more = text.IndexOf("\nAdditional Information\n", StringComparison.Ordinal);
        Assert.True(company > 0 && company < job && job < quals && quals < more,
            $"sections out of order: {company}, {job}, {quals}, {more}");
        Assert.Contains("Basic Qualifications:", text);
        Assert.Contains("\n• ", text);
        Assert.DoesNotContain("<p", text);
        Assert.DoesNotContain("<ul", text);
        Assert.DoesNotContain("&#xa0;", text);
        Assert.DoesNotContain("sr-tagline", text);
        // Site chrome the page scrape drags in.
        Assert.DoesNotContain("I'm interested", text);
        Assert.DoesNotContain("Google Chrome", text);      // the browser-support notice
        Assert.True(text.Length > 3000, $"expected the full posting, got {text.Length} chars");
    }

    [Fact]
    public void Location_falls_back_to_the_address_parts()
    {
        const string json = """
            {"name":"SRE","company":{"name":"Acme"},
             "location":{"city":"Austin","region":"TX","country":"us","remote":true},
             "jobAd":{"sections":{"jobDescription":{"title":"Job Description","text":"<p>Keep it up.</p>"}}}}
            """;

        var posting = SmartRecruitersPosting.ParsePostingJson(json);

        Assert.NotNull(posting);
        Assert.Contains("Location: Austin, TX, US (Remote)", posting!.Text);
        Assert.EndsWith("Job Description\nKeep it up.", posting.Text);
    }

    [Theory]
    [InlineData("""{"name":"Engineer","company":{"name":"Acme"}}""")]                       // no ad
    [InlineData("""{"name":"Engineer","jobAd":{"sections":{"jobDescription":{"title":"Job Description","text":""}}}}""")]
    [InlineData("""{"message":"Posting not found","errors":[{"code":"NOT_FOUND"}]}""")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    public void Unusable_payloads_return_null(string json) =>
        Assert.Null(SmartRecruitersPosting.ParsePostingJson(json));

    /// <summary>
    /// What the page path had to work with: a server-rendered posting wrapped
    /// in site chrome, marked up as microdata rather than JSON-LD. The strip
    /// clears the threshold with ease, so the chrome was stored as the posting.
    /// </summary>
    [Fact]
    public void The_page_is_the_posting_inside_site_chrome_without_json_ld()
    {
        var html = Fixture("smartrecruiters-job-page.html");
        var text = HtmlText.PageToText(html);

        Assert.True(StructuredPosting.FromJsonLd(html).IsEmpty);
        Assert.Contains("Basic Qualifications", text);
        Assert.Contains("I'm interested", text);
        Assert.Contains("Google Chrome", text);      // the browser-support notice
        Assert.Null(JobPostingFetcher.SameHostFrameUrl(html, PostingUrl));
    }

    [Fact]
    public void Microdata_names_the_employer_on_the_page()
    {
        Assert.Equal("LinkedIn", StructuredPosting.CompanyFromPage(Fixture("smartrecruiters-job-page.html")));

        const string organization =
            "<div itemprop=\"hiringOrganization\" itemscope itemtype=\"http://schema.org/Organization\">"
            + "<meta itemprop=\"name\" content=\"Acme &amp; Co\"></div>";
        Assert.Equal("Acme & Co", StructuredPosting.CompanyFromPage(organization));

        // og:site_name still wins when present.
        Assert.Equal("Acme Careers", StructuredPosting.CompanyFromPage(
            "<meta property=\"og:site_name\" content=\"Acme Careers\">" + organization));

        // An itemprop="name" outside a hiringOrganization does not count.
        Assert.Equal("", StructuredPosting.CompanyFromPage("<meta itemprop=\"name\" content=\"Acme\">"));
    }

    [Fact]
    public async Task SmartRecruiters_url_resolves_from_the_posting_api()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("smartrecruiters-posting.json"), "application/json");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Equal("LinkedIn", result.Company);
        Assert.Equal("Software Engineer - AI Platform", result.Position);
        Assert.Contains("Basic Qualifications", result.Text);
        Assert.DoesNotContain("I'm interested", result.Text);
        // One request only: the endpoint hit short-circuits the page fetch.
        Assert.Single(handler.Requests);
        Assert.Equal(ApiUrl, handler.Requests[0].Url);
    }

    /// <summary>
    /// If the API ever stops answering, the page still resolves — with the
    /// employer now recovered from its microdata instead of left blank.
    /// </summary>
    [Fact]
    public async Task Page_fallback_names_the_company_and_role()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Posting not found"}""", "application/json");
        handler.Enqueue(HttpStatusCode.OK, Fixture("smartrecruiters-job-page.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Equal("LinkedIn", result.Company);
        Assert.Equal("Software Engineer - AI Platform", result.Position);
        Assert.Contains("Basic Qualifications", result.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(PostingUrl, handler.Requests[1].Url);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}
