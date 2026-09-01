using System.Net;
using InterviewFlow.App.ViewModels;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Tests.Core;
using Pages = InterviewFlow.App.ViewModels.Pages;

namespace InterviewFlow.Tests.App;

/// <summary>
/// Setup's "Fetch from URL" button (docs/03-ui-spec.md §3.2): resolves the URL
/// in place, fills Company/Position when the posting names them, and — unlike
/// Save &amp; Continue — never saves or navigates.
/// </summary>
public sealed class SetupFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "if-fetchbtn-" + Guid.NewGuid().ToString("N")[..8]);

    private MainViewModel NewShell()
    {
        Directory.CreateDirectory(_dir);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath,
            $"INTERVIEW_DATA_DIR={Path.Combine(_dir, "data")}\nACTIVE_PROVIDER=ollama\n");
        return new MainViewModel(new AppConfig(EnvFile.Load(envPath)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Url = "https://example.com/jobs/1";

    /// <summary>A page whose JSON-LD names the role and the employer.</summary>
    private static string Page(string title = "Staff Engineer", string company = "Acme Corp")
    {
        var body = string.Join("", Enumerable.Repeat("<p>Build and ship excellent software.</p>", 12));
        return $$"""
            <html><head><script type="application/ld+json">
            {"@type":"JobPosting","title":"{{title}}",
             "hiringOrganization":{"@type":"Organization","name":"{{company}}"},
             "description":"{{body}}"}
            </script></head><body><div id="app"></div></body></html>
            """;
    }

    private static (Pages.SetupPageViewModel Setup, FakeHandler Handler) Fetchable(
        MainViewModel shell, string page)
    {
        var setup = Assert.IsType<Pages.SetupPageViewModel>(shell.CurrentPage);
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, page, "text/html");
        setup.FetchClient = new HttpClient(handler);
        setup.JobPosting = Url;
        return (setup, handler);
    }

    [Fact]
    public async Task Fetch_button_fills_the_posting_and_the_names()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, Page());

        await setup.FetchFromUrlCommand.ExecuteAsync(null);

        Assert.StartsWith("Staff Engineer", setup.JobPosting);
        Assert.Equal("Acme Corp", setup.CompanyName);
        Assert.Equal("Staff Engineer", setup.Position);
        Assert.Contains("Filled Company and Position", setup.FetchStatus);
    }

    [Fact]
    public async Task Fetch_button_does_not_navigate_or_save()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, Page());

        await setup.FetchFromUrlCommand.ExecuteAsync(null);

        // Still on Setup, with nothing written to the store.
        Assert.Same(setup, shell.CurrentPage);
        Assert.Equal("setup", shell.Steps.First(s => s.IsActive).Key);
        Assert.Empty(shell.Store.ListSummaries());
    }

    [Fact]
    public async Task Typed_names_survive_a_fetch()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, Page());
        setup.CompanyName = "Acme (via referral)";
        setup.Position = "Staff Engineer, Platform";

        await setup.FetchFromUrlCommand.ExecuteAsync(null);

        Assert.Equal("Acme (via referral)", setup.CompanyName);
        Assert.Equal("Staff Engineer, Platform", setup.Position);
        Assert.Contains("Left your Company and Position as typed", setup.FetchStatus);
    }

    [Fact]
    public async Task A_failed_fetch_leaves_the_url_in_place_and_reports_it()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, "<html><body>Loading…</body></html>");

        await setup.FetchFromUrlCommand.ExecuteAsync(null);

        Assert.Equal(Url, setup.JobPosting); // the URL is not consumed
        Assert.Equal(InterviewFlow.Core.Agents.JobPostingFetcher.CouldNotExtractMessage, setup.FetchStatus);
        Assert.Equal("", setup.CompanyName);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("We are hiring a Staff Engineer…", false)]
    [InlineData("https://example.com/jobs/1", true)]
    public void Button_is_enabled_only_for_a_bare_url(string posting, bool enabled)
    {
        var setup = Assert.IsType<Pages.SetupPageViewModel>(NewShell().CurrentPage);
        setup.JobPosting = posting;
        Assert.Equal(enabled, setup.FetchFromUrlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_and_continue_still_resolves_a_url_and_moves_on()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, Page("SRE", "Globex"));

        await setup.SaveAndContinueCommand.ExecuteAsync(null);

        var saved = Assert.Single(shell.Store.ListSummaries());
        Assert.Equal("Globex", saved.CompanyName);
        Assert.Equal("SRE", saved.Position);
        Assert.Equal("resume", shell.Steps.First(s => s.IsActive).Key);
    }

    [Fact]
    public async Task Save_and_continue_stays_put_when_the_fetch_fails()
    {
        var shell = NewShell();
        var (setup, _) = Fetchable(shell, "<html><body>Loading…</body></html>");

        await setup.SaveAndContinueCommand.ExecuteAsync(null);

        Assert.Equal("setup", shell.Steps.First(s => s.IsActive).Key);
        Assert.Empty(shell.Store.ListSummaries());
    }

    [Fact]
    public async Task Pasted_text_is_saved_as_is_without_any_request()
    {
        var shell = NewShell();
        var setup = Assert.IsType<Pages.SetupPageViewModel>(shell.CurrentPage);
        var handler = new FakeHandler(); // nothing queued — a request would throw
        setup.FetchClient = new HttpClient(handler);
        setup.CompanyName = "Acme";
        setup.JobPosting = "We are hiring a Staff Engineer.";

        await setup.SaveAndContinueCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requests);
        Assert.Equal("We are hiring a Staff Engineer.",
            shell.Store.LoadState(Assert.Single(shell.Store.ListSummaries()).Id)!.JobPosting);
    }
}
