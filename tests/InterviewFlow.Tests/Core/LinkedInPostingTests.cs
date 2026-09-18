using System.Net;
using InterviewFlow.Core.Agents;
using InterviewFlow.Core.Config;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// LinkedIn postings (docs/05 §5.7). Fixtures are the real captured responses
/// for one posting: the guest job API fragment, and the guest page with its
/// script, style, noscript and svg blocks removed (the strip discards those
/// anyway; what remains is the markup the page path actually reads).
/// </summary>
public sealed class LinkedInPostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string SharedUrl =
        "https://www.linkedin.com/jobs/view/4466229826/?trk=eml-email_job_alert_digest_01-primary_job_list-0-jobcard_body_5_jobid_4466229826_ssid_15441926092_fmid_97j2s2~mu783o29~qk&refId=hpF7fGWmnyIEXUMx9BJmvA%3D%3D&trackingId=yUByvDhVRUJW2X9v3HrktQ%3D%3D";

    private const string GuestApiUrl = "https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/4466229826";
    private const string PageUrl = "https://www.linkedin.com/jobs/view/4466229826/";

    [Theory]
    [InlineData(SharedUrl, "4466229826")]                                                       // job-alert email link
    [InlineData("https://www.linkedin.com/jobs/view/4466229826", "4466229826")]
    [InlineData("https://www.linkedin.com/jobs/view/software-engineer-automations-at-retool-4466229826?trk=x", "4466229826")]
    [InlineData("https://linkedin.com/jobs/view/4466229826/", "4466229826")]
    [InlineData("https://uk.linkedin.com/jobs/view/4466229826", "4466229826")]                  // country subdomain
    [InlineData("https://www.linkedin.com/jobs/search/?currentJobId=4466229826&keywords=engineer", "4466229826")]
    [InlineData("https://www.linkedin.com/jobs/collections/recommended/?currentJobId=4466229826", "4466229826")]
    [InlineData(GuestApiUrl, "4466229826")]                                                     // already the endpoint
    public void Reads_the_job_id_from_every_posting_url_form(string url, string id)
    {
        Assert.Equal(id, LinkedInPosting.JobId(url));
        Assert.Equal(GuestApiUrl, LinkedInPosting.GuestApiUrl(url));
        Assert.Equal(PageUrl, LinkedInPosting.PageUrl(url));
    }

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123")]                 // not LinkedIn
    [InlineData("https://www.linkedin.com/company/tryretool/jobs/")]          // company jobs listing
    [InlineData("https://www.linkedin.com/jobs/search/?keywords=engineer")]   // search, no job selected
    [InlineData("https://www.linkedin.com/jobs/view/")]
    [InlineData("https://www.linkedin.com/jobs/view/not-a-job")]
    [InlineData("https://www.linkedin.com/in/someone/")]                       // a profile
    [InlineData("https://notlinkedin.com/jobs/view/4466229826")]
    public void Leaves_non_posting_urls_alone(string url)
    {
        Assert.Null(LinkedInPosting.JobId(url));
        Assert.Null(LinkedInPosting.GuestApiUrl(url));
    }

    [Fact]
    public void Parses_the_real_guest_fragment()
    {
        var posting = LinkedInPosting.ParseHtml(Fixture("linkedin-guest-posting.html"));

        Assert.NotNull(posting);
        Assert.Equal("Software Engineer, Automations", posting!.Title);
        Assert.Equal("Retool", posting.Company);

        var text = posting.Text;
        Assert.StartsWith("Software Engineer, Automations\nCompany: Retool\nLocation: San Francisco, CA\n", text);
        Assert.Contains("Base pay range: $163,800.00/yr - $306,000.00/yr", text);
        Assert.Contains("Employment type: Full-time", text);
        Assert.Contains("Job function: Engineering and Information Technology", text);
        Assert.Contains("Industries: Software Development", text);
        Assert.Contains("About Retool", text);
        Assert.Contains("What You'll Do", text);
        Assert.Contains("The Skillset You'll Bring", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("Sign in", text);
        Assert.DoesNotContain("Similar jobs", text);
        Assert.DoesNotContain("Show more", text);
        Assert.True(text.Length > 5000, $"expected the full posting, got {text.Length} chars");
    }

    /// <summary>
    /// The same parser reads the full guest page: the description block and
    /// top card are identical markup, and the surrounding chrome is skipped.
    /// </summary>
    [Fact]
    public void Parses_the_real_guest_page()
    {
        var html = Fixture("linkedin-job-page.html");
        var posting = LinkedInPosting.ParseHtml(html);

        Assert.NotNull(posting);
        Assert.Equal("Software Engineer, Automations", posting!.Title);
        Assert.Equal("Retool", posting.Company);
        Assert.Contains("Base pay range: $163,800.00/yr - $306,000.00/yr", posting.Text);
        Assert.Contains("About Retool", posting.Text);
        Assert.DoesNotContain("Skip to main content", posting.Text);
        Assert.DoesNotContain("Similar jobs", posting.Text);
        Assert.DoesNotContain("Join now", posting.Text);
        Assert.True(posting.Text.Length > 5000 && posting.Text.Length < 12000,
            $"expected just the posting, got {posting.Text.Length} chars");
    }

    [Fact]
    public void A_description_with_nested_divs_is_read_to_its_own_closing_tag()
    {
        const string html =
            "<h2 class=\"top-card-layout__title\">SRE</h2>"
            + "<a class=\"topcard__org-name-link\" href=\"#\">\n Acme &amp; Co \n</a>"
            + "<div class=\"show-more-less-html__markup relative\">"
            + "<div><strong>About</strong><br>Keep it up.</div><ul><li>Pager</li></ul>"
            + "</div><div class=\"similar-jobs\">Other roles</div>";

        var posting = LinkedInPosting.ParseHtml(html);

        Assert.NotNull(posting);
        Assert.Equal("Acme & Co", posting!.Company);
        Assert.StartsWith("SRE\nCompany: Acme & Co\n\nAbout\nKeep it up.", posting.Text);
        Assert.Contains("• Pager", posting.Text);
        Assert.DoesNotContain("Other roles", posting.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><h1>Sign in to view this job</h1></body></html>")]        // auth wall
    [InlineData("<h2 class=\"top-card-layout__title\">SRE</h2>")]                        // no description
    [InlineData("<div class=\"show-more-less-html__markup\">   </div>")]                 // empty description
    public void Unusable_documents_return_null(string html) =>
        Assert.Null(LinkedInPosting.ParseHtml(html));

    /// <summary>
    /// What the page path had to work with: a server-rendered posting inside
    /// the guest site's chrome, with no JSON-LD and an OpenGraph title that
    /// runs the employer and the city together.
    /// </summary>
    [Fact]
    public void The_page_is_the_posting_inside_site_chrome_without_json_ld()
    {
        var html = Fixture("linkedin-job-page.html");
        var text = HtmlText.PageToText(html);

        Assert.True(StructuredPosting.FromJsonLd(html).IsEmpty);
        Assert.Contains("About Retool", text);
        Assert.Contains("Skip to main content", text);
        Assert.Contains("Similar jobs", text);
        Assert.Equal("Retool — San Francisco, CA | LinkedIn Jobs", StructuredPosting.FromMetaTags(html).Company);
    }

    [Fact]
    public async Task LinkedIn_url_resolves_from_the_guest_api()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("linkedin-guest-posting.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), SharedUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Null(result.Error);
        Assert.Equal("Retool", result.Company);
        Assert.Equal("Software Engineer, Automations", result.Position);
        Assert.Contains("Base pay range", result.Text);
        Assert.DoesNotContain("Sign in", result.Text);
        // One request only: the endpoint hit short-circuits the page fetch.
        Assert.Single(handler.Requests);
        Assert.Equal(GuestApiUrl, handler.Requests[0].Url);
    }

    /// <summary>
    /// The guest endpoint rate-limits bursts (HTTP 429). The canonical page,
    /// fetched without the shared link's tracking parameters, carries the same
    /// markup and resolves through the same parser.
    /// </summary>
    [Fact]
    public async Task Page_fallback_parses_the_same_markup()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "", "text/html");
        handler.Enqueue(HttpStatusCode.OK, Fixture("linkedin-job-page.html"), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            Config(), SharedUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Equal("Retool", result.Company);
        Assert.Equal("Software Engineer, Automations", result.Position);
        Assert.Contains("About Retool", result.Text);
        Assert.DoesNotContain("Skip to main content", result.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(PageUrl, handler.Requests[1].Url);
    }

    // Ollama: a provider call would fail loudly rather than pass silently.
    private AppConfig Config() => _env.Config("ACTIVE_PROVIDER=ollama\n");
}
