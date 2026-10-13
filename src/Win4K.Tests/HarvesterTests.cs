using System.Net;
using Win4K.Core;
using Win4K.Core.Apple;
using Win4K.Core.Catalogs;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// The three catalogue harvesters, run for real against fixture responses (see
/// <see cref="HarvestFixtures"/>). These pin the behaviour scripts/harvest.py had — which
/// files qualify, the exact URL escaping the cache is keyed by — and the metadata the port
/// adds on top.
/// </summary>
public class HarvesterTests
{
    // ---- archive.org -------------------------------------------------------------------

    [Fact]
    public async Task Archive_keeps_only_4K_video_files_and_skips_youtube_items()
    {
        var http = HarvestFixtures.Http();
        var result = await new ArchiveOrgHarvester(http.Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);

        // Fire 66 (4096 wide) yes, Fire 67 (1920) no; .ogv derivative is 568 wide; Water 61/62
        // from both the original and the mirror; pixabay-4006.
        Assert.Equal(
            [
                "ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4",
                "ia:Water614KRes/Water 61 - 4K res.mp4",
                "ia:Water614KRes/Water 62 - 4K res.mp4",
                "ia:Water614KRes_201507/Water 61 - 4K res.mp4",
                "ia:Water614KRes_201507/Water 62 - 4K res.mp4",
                "ia:pixabay-4006/video-4006_source.mp4",
            ],
            result.Assets.Select(a => a.Key!).Order(StringComparer.Ordinal));

        Assert.Equal(0, http.CountOf(ArchiveOrgHarvester.MetadataUrl("youtube-abc123")));
    }

    [Fact]
    public async Task Archive_urls_are_escaped_exactly_as_harvest_py_escaped_them()
    {
        // The video cache is keyed by URL: any drift from urllib.parse.quote would orphan
        // every archive clip already cached.
        var result = await new ArchiveOrgHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);
        var fire = result.Assets.Single(a => a.ShotId == "Fire7345s4kRes");

        Assert.Equal("https://archive.org/download/Fire7345s4kRes/Fire%2066%20-%2045s%20-%204k%20res.mp4", fire.Url4KSdr);
        Assert.Equal("ia:Fire7345s4kRes:Fire 66 - 45s - 4k res.mp4", fire.Id); // v1 id, still written
    }

    [Fact]
    public async Task Archive_keeps_the_metadata_harvest_py_threw_away()
    {
        var result = await new ArchiveOrgHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);
        var fire = result.Assets.Single(a => a.ShotId == "Fire7345s4kRes");

        Assert.Equal("Fire 66", fire.AccessibilityLabel);
        Assert.Equal("Mitch Martinez", fire.Creator);
        Assert.Equal("CC BY-NC-ND 3.0", fire.Licence);
        Assert.Equal(55.5, fire.DurationSeconds);
        Assert.Equal(83_072_197, fire.SizeBytes);
        Assert.Equal((4096, 2160), (fire.Width, fire.Height));
        Assert.Equal("h264", fire.Codec);
        Assert.Contains("slow motion", fire.Subjects!);

        // The middle of 11 .thumbs frames, linked to the video through files[].original.
        Assert.Equal(
            "https://archive.org/download/Fire7345s4kRes/Fire7345s4kRes.thumbs/Fire%2066%20-%2045s%20-%204k%20res_000027.jpg",
            fire.ThumbnailUrl);
    }

    [Fact]
    public async Task Archive_pixabay_mirror_items_get_a_readable_title_and_their_subjects_split()
    {
        var result = await new ArchiveOrgHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);
        var pixabay = result.Assets.Single(a => a.ShotId == "pixabay-4006");

        Assert.Equal("Sea, Wave, Golden, Sand, Sunrise", pixabay.AccessibilityLabel);
        Assert.Equal("Pixabay Content License", pixabay.Licence);
        Assert.Contains("Sunrise", pixabay.Subjects!);   // "Sea; Wave; …;Sunrise;4k" is one string upstream
        Assert.Equal("https://archive.org/download/pixabay-4006/thumbnail-4006.jpg", pixabay.ThumbnailUrl);
    }

    [Fact]
    public async Task Archive_item_that_keeps_failing_is_reported_not_fatal()
    {
        var http = HarvestFixtures.Http();
        var result = await new ArchiveOrgHarvester(http.Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);

        Assert.Equal(["pixabay-9999"], result.FailedUnits);
        Assert.Equal(HarvestHttp.Attempts, http.CountOf(ArchiveOrgHarvester.MetadataUrl("pixabay-9999")));
    }

    [Fact]
    public async Task Archive_search_failure_fails_the_harvest()
    {
        // Without the item list "gone upstream" and "not fetched" look the same, so the
        // refresher must keep the previous catalogue rather than write a partial one.
        var http = HarvestFixtures.Http().Status(ArchiveOrgHarvester.SearchUrl(HarvestFixtures.PixabayQuery), HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ArchiveOrgHarvester(http.Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default));
    }

    [Theory]
    [InlineData("Fire 66 - 45s - 4k res", null, "Fire 66")]
    [InlineData("water bubbles 048 - 4K res", null, "Water Bubbles 048")]
    [InlineData("Thin Smoke 98 - 1m02s - 4k res", null, "Thin Smoke 98")]
    [InlineData("Pocket Watch a26 - 1m 10s - 4k res", null, "Pocket Watch a26")]
    [InlineData("Moon 17 - 3 min 30s", null, "Moon 17")]
    [InlineData("Fireworks 1 - 86s - 4k res - 120fps", null, "Fireworks 1")]
    [InlineData("Flip Clock 3 -1am", null, "Flip Clock 3 -1am")]
    [InlineData("Fireworks 14- 28s", null, "Fireworks 14")]
    [InlineData("Moon 19 - 1min 53s", null, "Moon 19")]
    [InlineData("video-20001_large", "Clouds, Sky - Pixabay Free Stock Footage 20001, 4k", "Clouds, Sky")]
    public void Archive_titles_are_cleaned(string stem, string? itemTitle, string expected)
    {
        Assert.Equal(expected, ArchiveOrgHarvester.CleanTitle(stem, itemTitle));
    }

    // ---- NASA --------------------------------------------------------------------------

    [Fact]
    public async Task Nasa_keeps_only_what_measures_4K_and_needs_no_ffprobe()
    {
        var http = HarvestFixtures.Http();
        var result = await new NasaHarvester(http.Harvest(), probe: null).HarvestAsync(HarvestFixtures.Nasa, null, default);

        var asset = Assert.Single(result.Assets);
        Assert.Equal($"nasa:{HarvestFixtures.Expedition65}", asset.Key);
        Assert.Equal(HarvestFixtures.Expedition65Video, asset.Url4KSdr);
        Assert.Equal((4096, 2160), (asset.Width, asset.Height));
        Assert.Equal(3000, asset.DurationSeconds);
        Assert.Equal("h264", asset.Codec);
        Assert.Equal(6_102_000_000, asset.SizeBytes);
        Assert.Equal("NASA JSC", asset.Creator);
        Assert.Contains("earthviews", asset.Subjects!);
        Assert.StartsWith("The people who get to see the Earth from space", asset.Description);
        Assert.StartsWith("https://images-assets.nasa.gov/", asset.ThumbnailUrl);
        Assert.Empty(result.FailedUnits);
    }

    [Fact]
    public async Task Nasa_measures_with_the_probe_only_when_metadata_has_no_dimensions()
    {
        var http = HarvestFixtures.Http()
            .Text($"https://images-assets.nasa.gov/video/{HarvestFixtures.Expedition65}/metadata.json", "{}");
        var probe = new FakeProbe(new MediaProbeResult(3840, 2160, 12.0, "hevc"));

        var result = await new NasaHarvester(http.Harvest(), probe).HarvestAsync(HarvestFixtures.Nasa, null, default);

        var asset = Assert.Single(result.Assets);
        Assert.Equal((3840, 2160, "hevc"), (asset.Width, asset.Height, asset.Codec));
        Assert.Equal([HarvestFixtures.Expedition65Video], probe.Probed);
    }

    [Fact]
    public void Nasa_urls_are_upgraded_to_https_and_escaped()
    {
        Assert.Equal(
            "https://images-assets.nasa.gov/video/Earth%20Views/Earth%20Views~orig.mp4",
            NasaHarvester.Https("http://images-assets.nasa.gov/video/Earth Views/Earth Views~orig.mp4"));
    }

    [Fact]
    public void Nasa_picks_the_original_when_the_item_name_itself_says_4K()
    {
        string[] urls =
        [
            "http://x/video/a_in_4K/a_in_4K~large.mp4",
            "http://x/video/a_in_4K/a_in_4K~orig.mp4",
            "http://x/video/a_in_4K/metadata.json",
        ];
        Assert.Equal(urls[1], NasaHarvester.PickVideo(urls));
    }

    [Theory]
    [InlineData("0:50:00", 3000.0)]
    [InlineData("0:02:31", 151.0)]
    [InlineData("12.5 s", 12.5)]
    [InlineData(null, null)]
    public void Nasa_exiftool_durations_parse(string? value, double? expected)
    {
        Assert.Equal(expected, NasaHarvester.ParseDuration(value));
    }

    // ---- aerial manifests --------------------------------------------------------------

    [Fact]
    public async Task Feeds_are_keyed_by_vendor_namespace()
    {
        var result = await new AppleFeedHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);

        Assert.Equal(
            [
                $"apple:{HarvestFixtures.SharedGuid}",
                $"apple:{HarvestFixtures.TvosOnlyGuid}",
                "amazon:content-d1b2090f-f2ad-49fa-bbc7-4ec64957680b",
                "amazon:content-82f845c1-85ae-4eba-b57a-7860f1e8e998",
                "amazon:content-8dd41619-4b4d-4077-aa02-5ad62a658f7d",
                "jetson:video_inspire_florida_miami_brickell_sunset_00036",
                "robin:robinfrcd_capmechant_202304",
            ],
            result.Assets.Select(a => a.Key!));
        Assert.All(result.Assets, a => Assert.False(string.IsNullOrEmpty(a.Feed)));
    }

    [Fact]
    public async Task Tvos26_names_and_captions_resolve_through_the_strings_file()
    {
        var result = await new AppleFeedHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);
        var shared = result.Assets.Single(a => a.Id == HarvestFixtures.SharedGuid);
        var only = result.Assets.Single(a => a.Id == HarvestFixtures.TvosOnlyGuid);

        // Category from the key, so it reads like resources-16's ("Space", not the strings file's "Earth").
        Assert.Equal("Space", shared.CategoryName);
        Assert.Equal("Korea and Japan Night", shared.SubcategoryName);
        Assert.Equal("Landscapes", only.CategoryName);
        Assert.Equal("Superbloom", only.SubcategoryName);

        // Nine points of interest, in time order, "Â " and " " normalised to spaces.
        Assert.Equal(9, shared.Captions!.Count);
        Assert.Equal(shared.Captions.OrderBy(c => c.At).Select(c => c.At), shared.Captions.Select(c => c.At));
        Assert.Equal("Moving southeast over Siberia and Mongolia", shared.Captions[0].Text);
        Assert.Equal("Following the Temblor Range in southern California", Assert.Single(only.Captions!).Text);

        Assert.StartsWith("https://sylvan.apple.com/", shared.ThumbnailUrl);
        Assert.NotEmpty(result.Categories);
    }

    [Fact]
    public async Task Amazon_time_of_day_and_vendor_captions_are_kept_but_bare_keys_are_dropped()
    {
        var result = await new AppleFeedHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);

        var amazon = result.Assets.Where(a => a.Feed == "amazon-fireos8").ToList();
        Assert.Equal(["day", "sunset", "sunrise"], amazon.Select(a => a.TimeOfDayLabel));
        Assert.Equal("A huge school of Yellowtail Snapper fish gather in formation.", amazon[0].Captions![0].Text);
        Assert.Equal("Amazon", amazon[0].Creator);

        // Jetson's captions are keys with no published strings file: better none than "video_inspire_…_0".
        var jetson = result.Assets.Single(a => a.Feed == "jetson-creative");
        Assert.Null(jetson.Captions);
    }

    [Fact]
    public async Task One_dead_feed_is_reported_and_the_rest_still_harvest()
    {
        var http = HarvestFixtures.Http().Status(HarvestFixtures.JetsonUrl, HttpStatusCode.NotFound);
        var result = await new AppleFeedHarvester(http.Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);

        Assert.Equal(["jetson-creative"], result.FailedUnits);
        Assert.Equal(6, result.Assets.Count);
        Assert.Equal(1, http.CountOf(HarvestFixtures.JetsonUrl)); // a 404 is final, not retried
    }

    [Fact]
    public async Task A_missing_strings_file_costs_captions_not_clips()
    {
        var http = HarvestFixtures.Http().Status(HarvestFixtures.Tvos26StringsUrl, HttpStatusCode.NotFound);
        var result = await new AppleFeedHarvester(http.Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);

        Assert.Equal(7, result.Assets.Count);
        var shared = result.Assets.Single(a => a.Id == HarvestFixtures.SharedGuid);
        Assert.Null(shared.Captions);
        // From the key instead: "AerialSubcategoryKoreaandJapanNight", camel-case split.
        Assert.Equal("Koreaand Japan Night", shared.SubcategoryName);
        Assert.Contains(result.Notes, n => n.Contains("strings"));
    }

    // ---- shared plumbing ---------------------------------------------------------------

    [Fact]
    public async Task Http_retries_server_errors_then_succeeds()
    {
        var calls = 0;
        var http = new FixtureHttp().Respond("https://example.test/a", () =>
            ++calls < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        Assert.Equal("ok", await http.Harvest().GetStringAsync("https://example.test/a", default));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Progress_is_reported_per_item()
    {
        var reports = new List<CatalogRefreshProgress>();
        var progress = new SyncProgress<CatalogRefreshProgress>(reports.Add);

        await new AppleFeedHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Feeds, progress, default);

        Assert.Contains(reports, r => r is { SourceId: AppleFeedHarvester.Id, Done: 4, Total: 4 });
    }

    [Fact]
    public async Task Cancellation_stops_a_harvest()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArchiveOrgHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Archive, null, cts.Token));
    }

    [Fact]
    public void Embedded_sources_json_describes_the_three_auto_sources()
    {
        var sources = SourceDefinitions.LoadEmbedded().Where(s => s.IsAuto).ToList();

        Assert.Equal(["archive-org", "nasa", "aerial-manifests"], sources.Select(s => s.Id));
        Assert.Equal(2, sources[0].Groups.Count);
        Assert.Equal(6, sources[1].Queries.Count);
        Assert.Equal(4, sources[2].Feeds.Count);
        Assert.NotNull(sources[2].Feeds.Single(f => f.Id == "apple-tvos26").StringsUrl);
    }

    private sealed class FakeProbe(MediaProbeResult result) : IMediaProbe
    {
        public List<string> Probed { get; } = [];

        public Task<MediaProbeResult?> ProbeAsync(string url, CancellationToken ct)
        {
            lock (Probed)
                Probed.Add(url);
            return Task.FromResult<MediaProbeResult?>(result);
        }
    }

    /// <summary>IProgress that reports inline, unlike Progress&lt;T&gt; which posts to the thread pool.</summary>
    internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _gate = new();

        public void Report(T value)
        {
            lock (_gate)
                report(value);
        }
    }
}
