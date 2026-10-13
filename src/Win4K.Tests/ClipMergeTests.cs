using Win4K.Core;
using Win4K.Core.Apple;
using Win4K.Core.Catalogs;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// Dedupe and merge: Apple's resources-16 ⋈ tvOS 26 by GUID, and archive.org's
/// "_201507" re-uploads dropped by (file name, size). Run both on small fixtures and on
/// the real shipped data, whose counts are the ones the plan quotes.
/// </summary>
public class ClipMergeTests
{
    private static async Task<List<ClipInfo>> HarvestedFeedClipsAsync()
    {
        var result = await new AppleFeedHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Feeds, null, default);
        var manifest = new AppleManifest { Assets = result.Assets, Categories = result.Categories };
        return CatalogClips.ToClipInfos(manifest, AppleFeedHarvester.Id).ToList();
    }

    private static AppleManifest AppleFallback() =>
        AppleJson.Deserialize(CatalogStore.ReadEmbedded("fallback-entries.json")!)!;

    [Fact]
    public async Task Apple_and_tvOS26_merge_by_GUID_with_Apples_URLs_and_tvOS_metadata()
    {
        var apple = CatalogClips.ToClipInfos(AppleFallback(), AppleAerialSource.SourceId)
                                .Where(c => c.Key == $"apple:{HarvestFixtures.SharedGuid}")
                                .ToList();
        var feeds = await HarvestedFeedClipsAsync();

        var merged = ClipMerger.Merge([.. apple, .. feeds]);

        // 1 Apple + 7 feed clips, one of which is the same aerial → 7.
        Assert.Equal(7, merged.Count);
        var clip = merged.Single(c => c.Key == $"apple:{HarvestFixtures.SharedGuid}");

        Assert.Equal("apple", clip.SourceId);
        Assert.Equal(["apple", "apple-tvos26"], clip.SourceIds);
        Assert.Equal(apple[0].Urls, clip.Urls);                       // resources-16 URL stays primary
        Assert.NotEqual(feeds.First(f => f.Key == clip.Key).Urls.Uhd4KSdr, clip.Urls.Uhd4KSdr);
        Assert.Equal("Korea and Japan Night", clip.Subcategory);      // from tvOS 26
        Assert.Equal(9, clip.Captions.Count);                         // from tvOS 26
        Assert.StartsWith("https://sylvan.apple.com/", clip.ThumbnailUrl); // tvOS 26 preview image
        Assert.Equal("Space", clip.Category);
        Assert.Contains(clip.Tags, t => t == new ClipTag(TagFacet.Subcategory, "Korea and Japan Night"));

        // The tvOS-only clip stays as it was.
        var only = merged.Single(c => c.Key == $"apple:{HarvestFixtures.TvosOnlyGuid}");
        Assert.Equal(["apple-tvos26"], only.SourceIds);
    }

    [Fact]
    public void Shipped_Apple_and_tvOS26_merge_to_139_with_114_shared()
    {
        var apple = CatalogClips.ToClipInfos(AppleFallback(), AppleAerialSource.SourceId);
        var tvos = CatalogClips.ToClipInfos(CatalogStore.LoadEmbedded("aerial-manifests")!, "aerial-manifests")
                               .Where(c => c.SourceId == "apple-tvos26")
                               .ToList();

        var merged = ClipMerger.Merge([.. apple, .. tvos]);

        Assert.Equal((114, 139), (apple.Count, tvos.Count));
        Assert.Equal(139, merged.Count);
        Assert.Equal(114, merged.Count(c => c.SourceIds.Count == 2));
        Assert.Equal(25, merged.Count(c => c.SourceIds is ["apple-tvos26"]));
        Assert.All(merged.Where(c => c.SourceIds.Count == 2), c => Assert.Equal("apple", c.SourceId));
    }

    [Fact]
    public async Task Archive_mirrors_are_dropped_by_file_name_and_size()
    {
        var result = await new ArchiveOrgHarvester(HarvestFixtures.Http().Harvest()).HarvestAsync(HarvestFixtures.Archive, null, default);

        var kept = ClipMerger.DropArchiveMirrors(result.Assets, ArchiveOrgHarvester.Id);

        // Water 61 is identical in both items → the _201507 copy goes. Water 62 differs in
        // size between them, so it is not provably the same file and both stay.
        Assert.Equal(6, result.Assets.Count);
        Assert.Equal(
            [
                "ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4",
                "ia:Water614KRes/Water 61 - 4K res.mp4",
                "ia:Water614KRes/Water 62 - 4K res.mp4",
                "ia:Water614KRes_201507/Water 62 - 4K res.mp4",
                "ia:pixabay-4006/video-4006_source.mp4",
            ],
            kept.Select(a => a.Key!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Mirror_drop_is_order_independent()
    {
        ClipInfo Clip(string key) => new() { Key = key, SourceId = "archive-org", Title = key, SizeBytes = 10 };

        var mirrorFirst = ClipMerger.DropArchiveMirrors([Clip("ia:A_201507/x.mp4"), Clip("ia:A/x.mp4")]).ToList();

        Assert.Equal(["ia:A/x.mp4"], mirrorFirst.Select(c => c.Key));
    }

    [Fact]
    public void Shipped_archive_catalogue_plays_no_mirrors()
    {
        var all = CatalogClips.ToClipInfos(CatalogStore.LoadEmbedded("archive-org")!, "archive-org");
        var kept = ClipMerger.DropArchiveMirrors(all).ToList();

        // Measured on the 2026-08-12 harvest: one mirror item, Water614KRes_201507, 21 files.
        // Holds for v1 (harvest.py) and for a v2 catalogue regenerated from it, which is
        // already mirror-free (then nothing more is dropped).
        var mirrors = all.Count(c => c.Key.StartsWith("ia:Water614KRes_201507/", StringComparison.Ordinal));
        Assert.Equal(all.Count - mirrors, kept.Count);
        Assert.DoesNotContain(kept, c => c.Key.StartsWith("ia:Water614KRes_201507/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_merges_every_source_and_tolerates_a_failing_one()
    {
        var feeds = await HarvestedFeedClipsAsync();
        var apple = CatalogClips.ToClipInfos(AppleFallback(), AppleAerialSource.SourceId);

        var clips = await ClipLibrary.LoadAsync(
        [
            new FixedSource("apple", apple),
            new FixedSource("feeds", feeds),
            new FixedSource("broken", null),
        ]);

        // 114 Apple + 7 feed clips, one of which is an Apple aerial → 120; the broken source adds nothing.
        Assert.Equal(120, clips.Count);
        Assert.Equal(clips.Count, clips.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public async Task Resolver_plays_one_clip_per_key_and_the_first_source_wins()
    {
        Clip C(string id, string url) => new(id, id, "Space", TimeOfDay.Unknown, new Uri(url), false);

        var clips = await ClipResolver.ResolveAsync(
        [
            new FixedSource("apple", null, [C("apple:A", "https://sylvan.apple.com/a.mov")]),
            new FixedSource("aerial-manifests", null, [C("apple:A", "https://sylvan.apple.com/tvos/a.mov"), C("amazon:B", "https://amazon/b.mp4")]),
        ]);

        Assert.Equal(["apple:A", "amazon:B"], clips.Select(c => c.Id));
        Assert.Equal("https://sylvan.apple.com/a.mov", clips[0].PlayableUrl);
    }

    private sealed class FixedSource(string id, IReadOnlyList<ClipInfo>? infos, IReadOnlyList<Clip>? clips = null) : IClipInfoSource
    {
        public string Id => id;
        public string DisplayName => id;

        public Task<IReadOnlyList<ClipInfo>> GetClipInfosAsync(CancellationToken ct = default) =>
            infos is null ? throw new InvalidOperationException("source down") : Task.FromResult(infos);

        public Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct = default) =>
            Task.FromResult(clips ?? infos?.Select(i => i.ToClip(VideoQuality.Uhd4KSdr)).OfType<Clip>().ToList() ?? []);
    }
}
