using Win4K.Core;
using Win4K.Core.Apple;
using Win4K.Core.Catalogs;

namespace Win4K.Tests;

/// <summary>
/// Clip keys are the one identity every per-clip setting hangs off (failure counts today;
/// rotation and keep-offline in settings v2), so their shapes and the migration from the
/// pre-key ids are pinned here.
/// </summary>
public class ClipKeyTests
{
    [Theory]
    [InlineData("apple", null, "009ba758-7060-4479-8ee8-fb9b40c8fb97", "", "apple:009BA758-7060-4479-8EE8-FB9B40C8FB97")]
    [InlineData("aerial-manifests", "apple-tvos26", "009BA758-7060-4479-8EE8-FB9B40C8FB97", "", "apple:009BA758-7060-4479-8EE8-FB9B40C8FB97")]
    [InlineData("aerial-manifests", "amazon-fireos8", "content-d1b2", "", "amazon:content-d1b2")]
    [InlineData("aerial-manifests", "jetson-creative", "video_inspire_x", "", "jetson:video_inspire_x")]
    [InlineData("aerial-manifests", "robin-fourcade", "robinfrcd_x", "", "robin:robinfrcd_x")]
    [InlineData("archive-org", null, "ia:Fire7345s4kRes:Fire 66 - 45s - 4k res.mp4", "Fire7345s4kRes", "ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4")]
    [InlineData("nasa", null, "nasa:jsc_x~orig.mp4", "jsc_x", "nasa:jsc_x")]
    public void Keys_derive_from_v1_catalogue_rows(string sourceId, string? feed, string id, string shotId, string expected)
    {
        var asset = new AppleAsset { Id = id, Feed = feed, ShotId = shotId };
        Assert.Equal(expected, ClipKey.ForAsset(sourceId, asset));
    }

    [Fact]
    public void A_stored_v2_key_wins_over_derivation()
    {
        var asset = new AppleAsset { Id = "anything", Key = "nasa:the-real-id" };
        Assert.Equal("nasa:the-real-id", ClipKey.ForAsset("nasa", asset));
    }

    [Theory]
    [InlineData("ia:item:dir/file.mp4", "ia:item/dir/file.mp4")]
    [InlineData("ia:item/file.mp4", "ia:item/file.mp4")]
    [InlineData("nasa:abc~orig.mp4", "nasa:abc")]
    [InlineData("nasa:abc", "nasa:abc")]
    [InlineData("apple:abc", "apple:ABC")]
    public void Normalize_is_idempotent_and_upgrades_old_shapes(string key, string expected)
    {
        Assert.Equal(expected, ClipKey.Normalize(key));
        Assert.Equal(expected, ClipKey.Normalize(expected));
    }

    [Theory]
    [InlineData("009BA758-7060-4479-8EE8-FB9B40C8FB97", "apple:009BA758-7060-4479-8EE8-FB9B40C8FB97")]
    [InlineData("ia:Water614KRes:Water 61 - 4K res.mp4", "ia:Water614KRes/Water 61 - 4K res.mp4")]
    [InlineData("nasa:322_SS101-CEO~orig.mp4", "nasa:322_SS101-CEO")]
    [InlineData(@"D:\Clips\beach 01.mp4", @"local:D:\Clips\beach 01.mp4")]
    [InlineData("content-d1b2090f-f2ad-49fa-bbc7-4ec64957680b", "amazon:content-d1b2090f-f2ad-49fa-bbc7-4ec64957680b")]
    [InlineData("robinfrcd_capmechant_202304", "robin:robinfrcd_capmechant_202304")]
    [InlineData("video_inspire_florida_miami_brickell_sunset_00036", "jetson:video_inspire_florida_miami_brickell_sunset_00036")]
    [InlineData("no-such-clip", "no-such-clip")]
    public void Legacy_ids_map_to_keys(string legacy, string expected)
    {
        // Bare vendor ids resolve through the shipped aerial-manifests catalogue.
        Assert.Equal(expected, ClipKey.FromLegacy(legacy, ClipKey.ResolveFromCatalogues));
    }

    [Fact]
    public void Every_shipped_clip_has_a_unique_namespaced_key()
    {
        foreach (var id in SourceRegistry.KnownCatalogs)
        {
            var clips = CatalogClips.ToClipInfos(CatalogStore.LoadEmbedded(id)!, id);
            Assert.All(clips, c => Assert.NotNull(ClipKey.NamespaceOf(c.Key)));
            Assert.Equal(clips.Count, clips.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Clip_Id_is_the_key_for_every_source()
    {
        var apple = AppleAerialSource.ToClips(AppleJson.Deserialize(CatalogStore.ReadEmbedded("fallback-entries.json")!)!, VideoQuality.Uhd4KSdr);
        Assert.All(apple, c => Assert.StartsWith("apple:", c.Id));

        var dir = Path.Combine(Path.GetTempPath(), "win4k-keys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "beach 01.mp4");
            File.WriteAllBytes(file, [0]);
            var local = Assert.Single(await new LocalFolderSource(dir).GetClipsAsync());
            Assert.Equal("local:" + file, local.Id);
            Assert.Equal(file, local.PlayableUrl);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Settings_written_before_keys_migrate_on_load()
    {
        // Today's real settings.json shape, plus failure counts under every legacy id style.
        var path = Path.Combine(Path.GetTempPath(), "win4k-settings-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
        {
          "version": 1,
          "sourceMode": "Apple",
          "quality": "Auto",
          "cacheBudgetGb": 20,
          "selectedClipIds": ["009BA758-7060-4479-8EE8-FB9B40C8FB97", "apple:009ba758-7060-4479-8ee8-fb9b40c8fb97"],
          "localFolders": [],
          "enabledCatalogIds": ["archive-org"],
          "failedClips": {
            "009BA758-7060-4479-8EE8-FB9B40C8FB97": 1,
            "apple:009BA758-7060-4479-8EE8-FB9B40C8FB97": 2,
            "ia:Water614KRes:Water 61 - 4K res.mp4": 3,
            "nasa:322_SS101-CEO~orig.mp4": 1,
            "robinfrcd_capmechant_202304": 1
          }
        }
        """);
        try
        {
            var settings = AppSettings.Load(path);

            Assert.Equal(["apple:009BA758-7060-4479-8EE8-FB9B40C8FB97"], settings.SelectedClipIds);
            Assert.Equal(
                new Dictionary<string, int>
                {
                    ["apple:009BA758-7060-4479-8EE8-FB9B40C8FB97"] = 2, // same clip twice: max, not sum
                    ["ia:Water614KRes/Water 61 - 4K res.mp4"] = 3,
                    ["nasa:322_SS101-CEO"] = 1,
                    ["robin:robinfrcd_capmechant_202304"] = 1,
                },
                settings.FailedClips);
            Assert.Equal(["archive-org"], settings.EnabledCatalogIds); // untouched
            Assert.False(settings.MigrateClipKeys());                // idempotent
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Health_reads_migrated_keys()
    {
        var settings = new AppSettings { FailedClips = new() { ["ia:A:x.mp4"] = 3 } };
        settings.MigrateClipKeys(_ => null);

        Assert.True(Core.Playback.ClipHealth.IsUnhealthy(settings, "ia:A/x.mp4"));
    }
}
