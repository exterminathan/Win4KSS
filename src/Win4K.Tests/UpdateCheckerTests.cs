using System.Net;
using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Updates;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// <see cref="UpdateChecker"/>: the 24h throttle (persisted in <see cref="AppSettings"/>, so it
/// survives a restart), draft/prerelease re-checked defensively even though GitHub's own
/// "latest release" endpoint already excludes both, and release-JSON parsing against fixtures.
/// No real api.github.com call is made anywhere here — everything goes through
/// <see cref="FixtureHttp"/> or a hand-written fake <see cref="IUpdateSource"/>.
/// </summary>
public class UpdateCheckerTests
{
    private const string CurrentVersion = "1.1.0";
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class FakeSource(Func<ReleaseInfo?> respond) : IUpdateSource
    {
        public int Calls { get; private set; }
        public Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    // ---- release-JSON parsing (GitHubReleaseSource.Parse), against fixtures -----------------

    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(FixtureHttp.Fixture(name)).RootElement;

    [Fact]
    public void Parses_an_available_release_with_assets()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-available.json"));

        Assert.NotNull(release);
        Assert.Equal("v1.10.0", release!.TagName);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
        Assert.Equal(2, release.Assets.Count);
        Assert.Contains(release.Assets, a => a.Name == "Win4KSS-Setup-1.10.0.exe" && a.Size == 123456789);
        Assert.Contains(release.Assets, a => a.Name == "Win4KSS-Setup-1.10.0.exe.sha256");
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), release.PublishedUtc);
    }

    [Fact]
    public void Parses_the_draft_flag()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-draft.json"));
        Assert.NotNull(release);
        Assert.True(release!.Draft);
        Assert.Empty(release.Assets);
        Assert.Null(release.PublishedUtc);
    }

    [Fact]
    public void Parses_the_prerelease_flag()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-prerelease.json"));
        Assert.NotNull(release);
        Assert.True(release!.Prerelease);
    }

    [Fact]
    public void Parses_a_release_with_no_assets()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-no-assets.json"));
        Assert.NotNull(release);
        Assert.Empty(release!.Assets);
    }

    [Fact]
    public void A_response_with_no_tag_name_does_not_parse()
    {
        using var doc = JsonDocument.Parse("""{ "draft": false }""");
        Assert.Null(GitHubReleaseSource.Parse(doc.RootElement));
    }

    [Fact]
    public void An_asset_missing_its_download_url_is_skipped_not_fatal()
    {
        using var doc = JsonDocument.Parse("""
            { "tag_name": "v1.0.0", "assets": [ { "name": "no-url.exe", "size": 5 }, { "name": "ok.exe", "size": 5, "browser_download_url": "https://x/ok.exe" } ] }
            """);
        var release = GitHubReleaseSource.Parse(doc.RootElement);
        Assert.NotNull(release);
        Assert.Equal(["ok.exe"], release!.Assets.Select(a => a.Name));
    }

    // ---- Evaluate: the pure comparison, independent of I/O -----------------------------------

    [Fact]
    public void Evaluate_reports_available_when_the_release_is_numerically_newer()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-available.json")); // v1.10.0
        var result = UpdateChecker.Evaluate(CurrentVersion, release, Now, null);

        Assert.True(result.Available);
        Assert.Equal("1.10.0", result.Latest);
        Assert.Equal("1.1.0", result.Current);
        Assert.NotNull(result.Notes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_reports_not_available_when_up_to_date()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-up-to-date.json")); // v1.1.0
        var result = UpdateChecker.Evaluate(CurrentVersion, release, Now, null);

        Assert.False(result.Available);
        Assert.Equal("1.1.0", result.Latest);
        Assert.Null(result.Notes); // notes are only carried when an update is actually available
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_skips_a_draft_release_even_though_the_tag_is_newer()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-draft.json")); // v1.10.0, draft
        var result = UpdateChecker.Evaluate(CurrentVersion, release, Now, null);

        Assert.False(result.Available);
        Assert.Null(result.Latest);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_skips_a_prerelease_even_though_the_tag_is_newer()
    {
        var release = GitHubReleaseSource.Parse(Fixture("github-release-prerelease.json")); // v2.0.0-beta.1
        var result = UpdateChecker.Evaluate(CurrentVersion, release, Now, null);

        Assert.False(result.Available);
        Assert.Null(result.Latest);
    }

    [Fact]
    public void Evaluate_reports_the_fetch_error_when_there_was_no_release()
    {
        var result = UpdateChecker.Evaluate(CurrentVersion, null, Now, "offline");
        Assert.False(result.Available);
        Assert.Equal("offline", result.Error);
    }

    [Fact]
    public void Evaluate_reports_an_error_for_an_unparsable_tag_rather_than_throwing()
    {
        var release = new ReleaseInfo("not-a-version", null, null, false, false, [], null);
        var result = UpdateChecker.Evaluate(CurrentVersion, release, Now, null);

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
    }

    // ---- CheckAsync: throttle, persistence, force bypass --------------------------------------

    private static AppSettings NewSettings() => new();

    [Fact]
    public async Task First_check_ever_is_never_throttled()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => GitHubReleaseSource.Parse(Fixture("github-release-available.json")));
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        var outcome = await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.True(outcome.Result.Available);
        Assert.NotNull(outcome.Release);
        Assert.Equal(Now, settings.LastUpdateCheckUtc);
        Assert.Equal("1.10.0", settings.LastUpdateLatestVersion);
        Assert.True(settings.LastUpdateAvailable);
        Assert.NotNull(settings.LastUpdateNotes);
    }

    [Fact]
    public async Task A_second_check_within_24_hours_does_not_touch_the_network()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => GitHubReleaseSource.Parse(Fixture("github-release-available.json")));
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);
        var second = await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);

        Assert.Equal(1, source.Calls); // still just the first call
        Assert.True(second.Result.Available);
        Assert.Equal("1.10.0", second.Result.Latest);
        Assert.Null(second.Release); // replayed from settings, not re-fetched
    }

    [Fact]
    public async Task A_check_24_hours_later_is_due_again()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => GitHubReleaseSource.Parse(Fixture("github-release-available.json")));
        var time = new FixedTime(Now);
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = time });

        await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);
        Assert.False(checker.IsDue);

        var later = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now.AddHours(24)) });
        Assert.True(later.IsDue);

        await later.CheckAsync(CurrentVersion, force: false, CancellationToken.None);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Force_bypasses_the_throttle_for_manual_Check_now()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => GitHubReleaseSource.Parse(Fixture("github-release-available.json")));
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);
        await checker.CheckAsync(CurrentVersion, force: true, CancellationToken.None);

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_failed_fetch_never_throws_and_is_reported_as_an_error()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => null); // GitHubReleaseSource itself never throws; it returns null on failure
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        var outcome = await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);

        Assert.False(outcome.Result.Available);
        Assert.NotNull(outcome.Result.Error);
        Assert.Null(outcome.Release);
    }

    [Fact]
    public async Task Up_to_date_hands_back_no_release_to_install()
    {
        var settings = NewSettings();
        var source = new FakeSource(() => GitHubReleaseSource.Parse(Fixture("github-release-up-to-date.json")));
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        var outcome = await checker.CheckAsync(CurrentVersion, force: false, CancellationToken.None);

        Assert.False(outcome.Result.Available);
        Assert.Null(outcome.Release);
    }

    // ---- end to end over real fixture HTTP, including draft/prerelease on the live endpoint --

    [Fact]
    public async Task End_to_end_over_fixture_http_against_the_real_GitHub_endpoint_shape()
    {
        var http = new FixtureHttp().Json(GitHubReleaseSource.ApiUrl, "github-release-available.json");
        var source = new GitHubReleaseSource(http.Client());
        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        var outcome = await checker.CheckAsync(CurrentVersion, force: true, CancellationToken.None);

        Assert.True(outcome.Result.Available);
        Assert.Equal("1.10.0", outcome.Result.Latest);
        Assert.Equal(1, http.CountOf(GitHubReleaseSource.ApiUrl));
    }

    [Fact]
    public async Task A_non_success_status_from_GitHub_is_reported_as_an_error_not_an_exception()
    {
        var http = new FixtureHttp().Status(GitHubReleaseSource.ApiUrl, HttpStatusCode.Forbidden);
        var source = new GitHubReleaseSource(http.Client());
        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(Now) });

        var outcome = await checker.CheckAsync(CurrentVersion, force: true, CancellationToken.None);

        Assert.False(outcome.Result.Available);
        Assert.NotNull(outcome.Result.Error);
    }
}
