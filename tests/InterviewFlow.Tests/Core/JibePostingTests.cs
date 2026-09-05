using System.Net;
using InterviewFlow.Core.Agents;

namespace InterviewFlow.Tests.Core;

/// <summary>
/// Jibe (iCIMS's hosted career sites, *.jibeapply.com) — docs/05 §5.7. The
/// fixture is the real GitHub posting page, trimmed to the parts that bit:
/// a JSON-LD JobPosting whose description carries "&amp;quot;" inside JSON
/// strings, an i18n script quoting an HTML e-mail template (so a literal
/// "&lt;/head&gt;" sits inside a script), og:site_name that repeats og:title,
/// and a client-rendered body that is nothing but the site's navigation.
/// </summary>
public sealed class JibePostingTests : IDisposable
{
    private readonly TempEnv _env = new();

    public void Dispose() => _env.Dispose();

    private const string PostingUrl =
        "https://githubinc.jibeapply.com/jobs/5679?lang=en-us&iis=Job+Board&iisn=LinkedIn&lever-source=LinkedinPosting";

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jibe-job-page.html"));

    [Fact]
    public void Json_ld_is_parsed_as_written_not_entity_decoded()
    {
        var posting = StructuredPosting.Extract(Fixture());

        Assert.Equal("Software Engineer III", posting.Title);
        Assert.Equal("GitHub, Inc.", posting.Company);
        Assert.False(posting.Teaser);
        Assert.StartsWith("Software Engineer III\nCompany: GitHub, Inc.\nLocation: United States", posting.Text);
        Assert.Contains("help build npm", posting.Text);
        Assert.Contains("• ", posting.Text);
        Assert.True(posting.Text.Length > 3000, $"expected the full posting, got {posting.Text.Length} chars");
    }

    [Fact]
    public void Entity_escaped_json_ld_still_decodes()
    {
        const string page = """
            <script type="application/ld+json">
            {&quot;@type&quot;:&quot;JobPosting&quot;,&quot;title&quot;:&quot;SRE&quot;,&quot;description&quot;:&quot;Keep it up.&quot;}
            </script>
            """;

        Assert.StartsWith("SRE", StructuredPosting.Extract(page).Text);
    }

    [Fact]
    public void A_script_quoting_an_html_document_does_not_leak_into_page_text()
    {
        var text = HtmlText.PageToText(Fixture());

        Assert.DoesNotContain("{{companyName}}", text);
        Assert.DoesNotContain("Thanks for starting your application", text);
        Assert.Contains("Skip to Main Content", text); // the body's own chrome survives
        Assert.True(text.Length < 3000, $"expected only the site chrome, got {text.Length} chars");
    }

    [Fact]
    public void Site_name_that_repeats_the_title_names_no_company()
    {
        const string page = """
            <html><head><title>Staff Engineer in Austin | Acme</title>
            <meta property="og:title" content="Staff Engineer in Austin | Acme">
            <meta property="og:site_name" content="Staff Engineer in Austin | Acme">
            <meta property="og:description" content="Acme is hiring a Staff Engineer.">
            </head></html>
            """;

        var posting = StructuredPosting.Extract(page);
        Assert.True(posting.Teaser);
        Assert.Equal("", posting.Company);
        Assert.Equal("", StructuredPosting.CompanyFromPage(page));
    }

    [Fact]
    public async Task The_posting_wins_over_the_navigation_the_page_strips_to()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture(), "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=anthropic\nANTHROPIC_API_KEY=sk-test\n"),
            PostingUrl, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Null(result.Error);
        Assert.True(result.WasFetched);
        Assert.False(result.UsedLlmFallback);
        Assert.Single(handler.Requests); // the page itself; no board API, no LLM
        Assert.Equal("GitHub, Inc.", result.Company);
        Assert.Equal("Software Engineer III", result.Position);
        Assert.StartsWith("Software Engineer III", result.Text);
        Assert.Contains("Responsibilities", result.Text);
        Assert.DoesNotContain("Skip to Main Content", result.Text);
        Assert.DoesNotContain("{{companyName}}", result.Text);
    }

    /// <summary>
    /// The JSON-LD preference only applies to a JSON-LD body that is a posting.
    /// A thin one still names the role and employer, and the page text stays
    /// the body — the pre-existing behaviour for server-rendered pages.
    /// </summary>
    [Fact]
    public async Task A_thin_json_ld_names_the_posting_but_the_page_text_stays()
    {
        var body = "<p>" + string.Join("</p><p>", Enumerable.Repeat("Build and ship excellent software.", 12)) + "</p>";
        var page = $$$"""
            <html><head><script type="application/ld+json">
            {"@type":"JobPosting","title":"Staff Engineer","description":"Short teaser.",
             "hiringOrganization":{"@type":"Organization","name":"Acme Corp"}}
            </script></head><body><h1>Staff Engineer</h1>{{{body}}}</body></html>
            """;
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, page, "text/html");

        var result = await JobPostingFetcher.ResolveAsync(
            _env.Config("ACTIVE_PROVIDER=anthropic\nANTHROPIC_API_KEY=sk-test\n"),
            "https://example.com/posting/1", TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal("Acme Corp", result.Company);
        Assert.Equal("Staff Engineer", result.Position);
        Assert.Contains("Build and ship excellent software.", result.Text);
        Assert.DoesNotContain("Short teaser", result.Text);
    }
}
