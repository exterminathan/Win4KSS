using System.Net;
using Win4K.Core.Catalogs;

namespace Win4K.Tests.Fakes;

/// <summary>
/// sources.json-shaped definitions for the three auto sources, plus a <see cref="FixtureHttp"/>
/// that answers every request their harvesters make from the files under Fixtures/.
///
/// What the fixtures contain, and so what the tests can rely on:
///  - archive.org "scenic": Fire7345s4kRes (one 4096-wide clip, one 1080p clip, 11 thumbs),
///    youtube-abc123 (must be skipped unrequested), Water614KRes and its "_201507" mirror
///    (Water 61 identical in name and size; Water 62 differs in size).
///  - archive.org "pixabay": pixabay-4006 (one 3840x2160 clip), pixabay-9999 (always 500).
///  - NASA "earth from space": Expedition 65 (4096x2160, kept) and "Earth Views from the
///    International Space Station" (1920x1080, dropped; its id has spaces).
///  - aerial feeds: tvOS 26 (one GUID shared with Apple's resources-16, one tvOS-only),
///    Fire TV (day, sunset, sunrise), Jetson (one, caption keys only), Robin (one).
/// </summary>
internal static class HarvestFixtures
{
    public const string ScenicQuery = "identifier:(*4kRes*) AND mediatype:movies";
    public const string PixabayQuery = "identifier:(pixabay-*) AND mediatype:movies";
    public const string NasaQuery = "earth from space";

    public const string Tvos26Url = "https://example.test/feeds/tvos26.json";
    public const string Tvos26StringsUrl = "https://example.test/feeds/tvos26_strings.json";
    public const string FireUrl = "https://example.test/feeds/fireos8.json";
    public const string JetsonUrl = "https://example.test/feeds/comm1.json";
    public const string RobinUrl = "https://example.test/feeds/comm2.json";

    public const string SharedGuid = "009BA758-7060-4479-8EE8-FB9B40C8FB97";
    public const string TvosOnlyGuid = "F390FE3B-FA61-483D-BADC-2447F89951BA";

    public const string Expedition65 = "jsc2022m000172_Earth_in_4K_Expedition_65_Edition";
    public const string EarthViews = "Earth Views from the International Space Station";

    public static readonly string Expedition65Video =
        $"https://images-assets.nasa.gov/video/{Expedition65}/{Expedition65}~orig.mp4";

    public static SourceDefinition Archive { get; } = new(
        ArchiveOrgHarvester.Id, "Internet Archive", "auto",
        [new ArchiveGroup("scenic", "nature", ScenicQuery), new ArchiveGroup("pixabay-mirror", "mixed", PixabayQuery)],
        [], []);

    public static SourceDefinition Nasa { get; } = new(
        NasaHarvester.Id, "NASA", "auto", [], [NasaQuery], []);

    public static SourceDefinition Feeds { get; } = new(
        AppleFeedHarvester.Id, "Aerial manifests", "auto", [], [],
        [
            new FeedDefinition("apple-tvos26", Tvos26Url, Tvos26StringsUrl),
            new FeedDefinition("amazon-fireos8", FireUrl, null),
            new FeedDefinition("jetson-creative", JetsonUrl, null),
            new FeedDefinition("robin-fourcade", RobinUrl, null),
        ]);

    public static IReadOnlyList<SourceDefinition> All { get; } = [Archive, Nasa, Feeds];

    public static FixtureHttp Http()
    {
        var http = new FixtureHttp();

        http.Json(ArchiveOrgHarvester.SearchUrl(ScenicQuery), "ia-search-scenic.json")
            .Json(ArchiveOrgHarvester.SearchUrl(PixabayQuery), "ia-search-pixabay.json");
        foreach (var id in new[] { "Fire7345s4kRes", "Water614KRes", "Water614KRes_201507", "pixabay-4006" })
            http.Json(ArchiveOrgHarvester.MetadataUrl(id), $"ia-metadata-{id}.json");
        http.Status(ArchiveOrgHarvester.MetadataUrl("pixabay-9999"), HttpStatusCode.InternalServerError);

        http.Json(NasaHarvester.SearchUrl(NasaQuery), "nasa-search.json")
            .Json($"https://images-assets.nasa.gov/video/{Expedition65}/collection.json", "nasa-collection-expedition65.json")
            .Json($"https://images-assets.nasa.gov/video/{Expedition65}/metadata.json", "nasa-metadata-expedition65.json")
            .Json($"https://images-assets.nasa.gov/video/{EarthViews}/collection.json", "nasa-collection-earthviews.json")
            .Json($"https://images-assets.nasa.gov/video/{EarthViews}/metadata.json", "nasa-metadata-earthviews.json")
            .Length(Expedition65Video, 6_102_000_000);

        http.Json(Tvos26Url, "tvos26.json")
            .Json(Tvos26StringsUrl, "tvos26-strings.json")
            .Json(FireUrl, "fireos8.json")
            .Json(JetsonUrl, "comm1.json")
            .Json(RobinUrl, "comm2.json");

        return http;
    }

    /// <summary>The live harvesters over the fixture handler, with no ffprobe.</summary>
    public static IReadOnlyList<ICatalogHarvester> Harvesters(FixtureHttp http)
    {
        var client = http.Harvest();
        return [new ArchiveOrgHarvester(client), new NasaHarvester(client, probe: null), new AppleFeedHarvester(client)];
    }
}
