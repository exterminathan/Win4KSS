using System.Net;
using Win4K.Core;
using Win4K.Core.Apple;
using Win4K.Core.Catalogs;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// The refresh service end to end over fixture HTTP, the AddedUtc diff, and the catalogue
/// store's local → embedded load order. Every test writes into its own temp store; none
/// touches the real %LOCALAPPDATA%.
/// </summary>
public sealed class CatalogRefreshTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "win4k-catalogs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CatalogStore _store;

    public CatalogRefreshTests() => _store = new CatalogStore(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private CatalogRefresher Refresher(FixtureHttp http, DateTime? now = null) =>
        new(_store, HarvestFixtures.All, HarvestFixtures.Harvesters(http), new FixedTime(now ?? Now));

    // ---- refresh --------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_writes_a_v2_catalogue_per_source_and_reports_counts()
    {
        var reports = new List<CatalogRefreshProgress>();
        var report = await Refresher(HarvestFixtures.Http()).RefreshAsync(progress: new HarvesterTests.SyncProgress<CatalogRefreshProgress>(reports.Add));

        Assert.True(report.AllSucceeded);
        var byId = report.Sources.ToDictionary(s => s.SourceId);

        Assert.Equal((6, 1, 5), (byId["archive-org"].HarvestedCount, byId["archive-org"].MirrorsDropped, byId["archive-org"].AssetCount));
        Assert.Equal((1, 1), (byId["nasa"].HarvestedCount, byId["nasa"].AssetCount));
        Assert.Equal((7, 7), (byId["aerial-manifests"].HarvestedCount, byId["aerial-manifests"].AssetCount));

        foreach (var id in new[] { "archive-org", "nasa", "aerial-manifests" })
        {
            var written = _store.LoadLocal(id)!;
            Assert.Equal(CatalogStore.CurrentSchemaVersion, written.SchemaVersion);
            Assert.Equal(id, written.SourceId);
            Assert.Equal(Now, written.GeneratedUtc);
            Assert.All(written.Assets, a => Assert.False(string.IsNullOrEmpty(a.Key)));
        }

        Assert.Contains(reports, r => r.SourceId == "archive-org");
        Assert.Contains(reports, r => r.SourceId == "nasa");
    }

    [Fact]
    public async Task First_refresh_over_a_v1_catalogue_dates_its_clips_to_the_original_harvest()
    {
        // The shape scripts/harvest.py wrote: no key, no addedUtc. Fire 66 was in it; the rest
        // of the fixture's clips were not, so only they are stamped as new.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.PathFor("archive-org"), """
        {"assets": [{"id": "ia:Fire7345s4kRes:Fire 66 - 45s - 4k res.mp4", "accessibilityLabel": "Fire 66 - 45s - 4k res",
                     "shotID": "Fire7345s4kRes", "scene": "nature",
                     "url-4K-SDR": "https://archive.org/download/Fire7345s4kRes/Fire%2066%20-%2045s%20-%204k%20res.mp4",
                     "width": 4096, "height": 2160, "sizeBytes": 83072197}],
         "version": 1, "initialAssetCount": 1}
        """);

        var outcome = (await Refresher(HarvestFixtures.Http()).RefreshAsync(["archive-org"])).Sources.Single();
        var assets = _store.LoadLocal("archive-org")!.Assets.ToDictionary(a => a.Key!, a => a.AddedUtc);

        Assert.Equal(CatalogRefresher.BaselineAddedUtc, assets["ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4"]);
        Assert.All(assets.Where(kv => !kv.Key.StartsWith("ia:Fire", StringComparison.Ordinal)), kv => Assert.Equal(Now, kv.Value));
        Assert.Equal(assets.Count - 1, outcome.Added);
    }

    [Fact]
    public async Task First_in_app_refresh_keeps_the_dates_in_the_shipped_catalogue()
    {
        // No local catalogue yet: the previous one is the embedded copy, whose dates carry over.
        await Refresher(HarvestFixtures.Http()).RefreshAsync(["archive-org"]);
        var assets = _store.LoadLocal("archive-org")!.Assets.ToDictionary(a => a.Key!);

        var embedded = CatalogStore.LoadEmbedded("archive-org")!.Assets
                                   .ToDictionary(a => ClipKey.ForAsset("archive-org", a), a => a.AddedUtc ?? CatalogRefresher.BaselineAddedUtc);
        foreach (var (key, asset) in assets)
        {
            var expected = embedded.TryGetValue(key, out var shipped) ? shipped : Now;
            Assert.True(expected == asset.AddedUtc, $"{key}: expected {expected:u}, got {asset.AddedUtc:u}");
        }
        Assert.Equal(CatalogRefresher.BaselineAddedUtc, assets["ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4"].AddedUtc);
    }

    [Fact]
    public async Task A_later_refresh_keeps_first_seen_dates_and_stamps_only_new_clips()
    {
        await Refresher(HarvestFixtures.Http(), Now).RefreshAsync(["aerial-manifests"]);
        var first = _store.LoadLocal("aerial-manifests")!.Assets.ToDictionary(a => a.Key!, a => a.AddedUtc);

        // A week later the Robin feed has gained a clip.
        var later = Now.AddDays(7);
        var robin = FixtureHttp.Fixture("comm2.json").Replace("robinfrcd_capmechant_202304", "robinfrcd_newclip_202610");
        var http = HarvestFixtures.Http().Text(HarvestFixtures.RobinUrl, robin);
        var outcome = (await Refresher(http, later).RefreshAsync(["aerial-manifests"])).Sources.Single();

        var second = _store.LoadLocal("aerial-manifests")!.Assets.ToDictionary(a => a.Key!, a => a.AddedUtc);
        Assert.Equal((1, 1), (outcome.Added, outcome.Removed));
        Assert.Equal(later, second["robin:robinfrcd_newclip_202610"]);
        Assert.All(second.Where(kv => kv.Key != "robin:robinfrcd_newclip_202610"), kv => Assert.Equal(first[kv.Key], kv.Value));
    }

    [Fact]
    public async Task An_item_that_fails_keeps_its_previous_rows()
    {
        var previous = new AppleManifest
        {
            Assets =
            [
                new AppleAsset
                {
                    Id = "ia:pixabay-9999:video-9999_source.mp4", ShotId = "pixabay-9999", Scene = "mixed",
                    Url4KSdr = "https://archive.org/download/pixabay-9999/video-9999_source.mp4", Width = 3840, Height = 2160,
                    AddedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            ],
        };
        _store.Save("archive-org", previous);

        var outcome = (await Refresher(HarvestFixtures.Http()).RefreshAsync(["archive-org"])).Sources.Single();

        var kept = _store.LoadLocal("archive-org")!.Assets.Single(a => a.ShotId == "pixabay-9999");
        Assert.Equal(1, outcome.CarriedOver);
        Assert.Equal("ia:pixabay-9999/video-9999_source.mp4", kept.Key);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), kept.AddedUtc);
        Assert.Contains(outcome.Notes, n => n.Contains("could not be fetched"));
    }

    [Fact]
    public async Task A_failed_harvest_leaves_the_previous_catalogue_untouched()
    {
        await Refresher(HarvestFixtures.Http()).RefreshAsync(["archive-org"]);
        var before = File.ReadAllText(_store.PathFor("archive-org"));

        var http = HarvestFixtures.Http().Status(ArchiveOrgHarvester.SearchUrl(HarvestFixtures.ScenicQuery), HttpStatusCode.ServiceUnavailable);
        var outcome = (await Refresher(http, Now.AddDays(1)).RefreshAsync(["archive-org"])).Sources.Single();

        Assert.False(outcome.Succeeded);
        Assert.Equal(5, outcome.AssetCount);
        Assert.Equal(before, File.ReadAllText(_store.PathFor("archive-org")));
    }

    [Fact]
    public async Task Cancelling_a_refresh_writes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Refresher(HarvestFixtures.Http()).RefreshAsync(ct: cts.Token));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.json").Any());
    }

    [Fact]
    public async Task Refresh_is_due_until_every_auto_source_is_fresh()
    {
        var refresher = Refresher(HarvestFixtures.Http());
        Assert.True(refresher.IsDue());

        await refresher.RefreshAsync();
        Assert.False(refresher.IsDue());

        var weekLater = new CatalogRefresher(_store, HarvestFixtures.All, HarvestFixtures.Harvesters(HarvestFixtures.Http()),
                                             new FixedTime(Now + CatalogRefresher.AutoRefreshInterval));
        Assert.True(weekLater.IsDue());
    }

    // ---- store and loader -------------------------------------------------------------------

    [Fact]
    public void Store_prefers_a_local_catalogue_and_falls_back_to_the_embedded_one()
    {
        Assert.Equal(CatalogOrigin.Embedded, _store.Load("nasa").Origin);

        _store.Save("nasa", new AppleManifest { Assets = [new AppleAsset { Id = "x", Url4KSdr = "https://x/y.mp4" }] });
        Assert.Equal(CatalogOrigin.Local, _store.Load("nasa").Origin);

        File.WriteAllText(_store.PathFor("nasa"), "{ not json");
        var (manifest, origin) = _store.Load("nasa");
        Assert.Equal(CatalogOrigin.Embedded, origin);
        Assert.NotEmpty(manifest!.Assets);
    }

    [Fact]
    public void v1_catalogue_loads_into_clips_with_keys()
    {
        // The shape scripts/harvest.py wrote: no key, no schemaVersion, a v1 id.
        const string v1 = """
        {"assets": [{"id": "ia:Fire7345s4kRes:Fire 66 - 45s - 4k res.mp4", "accessibilityLabel": "Fire 66 - 45s - 4k res",
                     "shotID": "Fire7345s4kRes", "scene": "nature",
                     "url-4K-SDR": "https://archive.org/download/Fire7345s4kRes/Fire%2066%20-%2045s%20-%204k%20res.mp4",
                     "width": 4096, "height": 2160, "sizeBytes": 83072197}],
         "version": 1, "initialAssetCount": 1}
        """;
        var clip = Assert.Single(CatalogClips.ToClipInfos(AppleJson.Deserialize(v1)!, "archive-org"));

        Assert.Equal("ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4", clip.Key);
        Assert.Equal("Nature", clip.Category);
        Assert.Null(clip.AddedUtc);
        Assert.Equal("https://archive.org/download/Fire7345s4kRes/Fire%2066%20-%2045s%20-%204k%20res.mp4",
                     clip.ToClip(VideoQuality.Uhd4KSdr)!.PlayableUrl);
    }

    [Fact]
    public async Task v2_catalogue_round_trips_every_extension_field()
    {
        await Refresher(HarvestFixtures.Http()).RefreshAsync(["aerial-manifests"]);
        var text = File.ReadAllText(_store.PathFor("aerial-manifests"));
        var clips = CatalogClips.ToClipInfos(AppleJson.Deserialize(text)!, "aerial-manifests");

        var shared = clips.Single(c => c.Key == $"apple:{HarvestFixtures.SharedGuid}");
        Assert.Equal(("Space", "Korea and Japan Night", 9), (shared.Category, shared.Subcategory, shared.Captions.Count));
        Assert.Equal("apple-tvos26", shared.SourceId);
        Assert.Equal(CatalogRefresher.BaselineAddedUtc, shared.AddedUtc); // listed in the shipped v1 catalogue

        var amazon = clips.Single(c => c.Key == "amazon:content-8dd41619-4b4d-4077-aa02-5ad62a658f7d");
        Assert.Equal((TimeOfDay.Sunrise, "Nature", "Morning mist"), (amazon.TimeOfDay, amazon.Category, amazon.Title));

        var robin = clips.Single(c => c.SourceId == "robin-fourcade");
        Assert.Equal(("Cap Méchant", "France"), (robin.Title, robin.Subcategory));
        Assert.Contains("Cap Méchant", text); // written readably, not \u-escaped

        // Feed fields the model does not know (showInTopLevel, preferredOrder…) survive.
        Assert.Contains("\"showInTopLevel\"", text);
    }

    [Fact]
    public async Task Loader_reads_a_refreshed_catalogue_over_the_shipped_one()
    {
        var home = Path.Combine(_dir, "home");
        using var scope = AppPaths.UseRoot(home);

        var store = new CatalogStore();
        Assert.Equal(Path.Combine(home, "catalogs"), store.Directory);

        await new CatalogRefresher(store, HarvestFixtures.All, HarvestFixtures.Harvesters(HarvestFixtures.Http()), new FixedTime(Now))
            .RefreshAsync(["nasa"]);

        var (manifest, origin) = store.Load("nasa");
        Assert.Equal(CatalogOrigin.Local, origin);
        Assert.Single(manifest!.Assets);
    }

    internal sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
