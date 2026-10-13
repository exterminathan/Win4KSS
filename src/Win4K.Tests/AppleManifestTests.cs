using Win4K.Core;
using Win4K.Core.Apple;

namespace Win4K.Tests;

/// <summary>
/// Parsing tests run against the embedded fallback manifest, which is a byte-for-byte copy
/// of Apple's real entries.json from resources-16.tar. That keeps the suite offline and
/// deterministic while still exercising the exact schema we ship against.
/// </summary>
public class AppleManifestTests
{
    private static AppleManifest Manifest => AppleManifestClient.ReadEmbeddedFallback();

    [Fact]
    public void EmbeddedFallback_parses_the_full_catalogue()
    {
        var m = Manifest;

        Assert.Equal(114, m.Assets.Count);
        Assert.Equal(4, m.Categories.Count);
        Assert.All(m.Assets, a => Assert.False(string.IsNullOrWhiteSpace(a.Id)));
        Assert.All(m.Assets, a => Assert.False(string.IsNullOrWhiteSpace(a.AccessibilityLabel)));
    }

    [Fact]
    public void Every_asset_has_a_playable_4K_url()
    {
        // If this ever fails, the fallback manifest was refreshed from a source that dropped
        // 4K variants — which would silently downgrade the whole point of the project.
        foreach (var asset in Manifest.Assets)
        {
            var url = asset.UrlFor(VideoQuality.Uhd4KSdr);
            Assert.NotNull(url);
            Assert.Contains("_4K_", url);
            Assert.StartsWith("https://sylvan.apple.com/", url);
        }
    }

    [Fact]
    public void ShotId_maps_from_the_odd_cased_json_key()
    {
        // The JSON key is "shotID", not "shotId" — camelCase policy alone does not match it,
        // so this asserts the explicit [JsonPropertyName] is doing its job.
        Assert.All(Manifest.Assets, a => Assert.False(string.IsNullOrWhiteSpace(a.ShotId)));
    }

    [Theory]
    [InlineData(VideoQuality.Uhd4KSdr, "_SDR_4K_HEVC")]
    [InlineData(VideoQuality.Uhd4KHdr, "_HDR_4K_HEVC")]
    [InlineData(VideoQuality.Hd1080Sdr, "_SDR_2K_HEVC")]
    [InlineData(VideoQuality.Hd1080Hdr, "_HDR_2K_HEVC")]
    [InlineData(VideoQuality.Hd1080H264, "_SDR_2K_AVC")]
    public void UrlFor_selects_the_requested_variant(VideoQuality quality, string expectedMarker)
    {
        var asset = Manifest.Assets[0];
        Assert.Contains(expectedMarker, asset.UrlFor(quality));
    }

    [Fact]
    public void UrlFor_falls_back_rather_than_returning_null()
    {
        // A clip playable at lower quality beats a hole in the playlist.
        var sparse = new AppleAsset { Id = "x", Url1080H264 = "https://example/x.mov" };
        Assert.Equal("https://example/x.mov", sparse.UrlFor(VideoQuality.Uhd4KHdr));
    }

    [Fact]
    public void UrlFor_returns_null_when_the_asset_has_nothing()
    {
        Assert.Null(new AppleAsset { Id = "empty" }.UrlFor(VideoQuality.Uhd4KSdr));
    }

    [Theory]
    [InlineData("AerialCategoryUnderwater", "Underwater")]
    [InlineData("AerialCategorySpace", "Space")]
    [InlineData("AerialCategoryLandscapes", "Landscapes")]
    [InlineData("AerialCategoryCities", "Cities")]
    [InlineData("AerialCategoryDeepSpaceProbe", "Deep Space Probe")]
    [InlineData("", "Other")]
    public void Category_display_name_is_derived_without_the_binary_plist(string key, string expected)
    {
        Assert.Equal(expected, new AppleCategory { LocalizedNameKey = key }.DisplayName);
    }

    [Fact]
    public void Categories_cover_every_asset()
    {
        var manifest = Manifest;
        var clips = AppleAerialSource.ToClips(manifest, VideoQuality.Uhd4KSdr);

        Assert.Equal(114, clips.Count);
        Assert.DoesNotContain(clips, c => c.Category == "Other");

        var expected = new[] { "Space", "Landscapes", "Cities", "Underwater" };
        Assert.All(clips, c => Assert.Contains(c.Category, expected));
    }

    [Theory]
    [InlineData("Africa Night", TimeOfDay.Night)]
    [InlineData("New York Night", TimeOfDay.Night)]
    [InlineData("Caribbean Day", TimeOfDay.Day)]
    [InlineData("Caribbean", TimeOfDay.Unknown)]
    [InlineData("Los Angeles", TimeOfDay.Unknown)]
    [InlineData(null, TimeOfDay.Unknown)]
    public void TimeOfDay_is_read_from_the_label_suffix(string? label, TimeOfDay expected)
    {
        Assert.Equal(expected, ClipHeuristics.FromLabel(label));
    }

    [Fact]
    public void TimeOfDay_is_only_known_for_a_handful_of_clips()
    {
        // Documents a real limitation rather than pretending the feature is meaningful:
        // the 4K manifest dropped the per-asset timeOfDay the legacy 1080p one had.
        var clips = AppleAerialSource.ToClips(Manifest, VideoQuality.Uhd4KSdr);
        var known = clips.Count(c => c.TimeOfDay != TimeOfDay.Unknown);

        Assert.Equal(4, known);
    }

    [Theory]
    [InlineData(3840, VideoQuality.Uhd4KSdr)]
    [InlineData(2560, VideoQuality.Uhd4KSdr)]
    [InlineData(1920, VideoQuality.Hd1080Sdr)]
    [InlineData(1366, VideoQuality.Hd1080Sdr)]
    public void Auto_quality_follows_the_primary_display_width(int width, VideoQuality expected)
    {
        Assert.Equal(expected, AppleAerialSource.Resolve(VideoQuality.Auto, width));
    }

    [Fact]
    public void Explicit_quality_is_never_overridden_by_Auto_resolution()
    {
        Assert.Equal(VideoQuality.Hd1080H264,
            AppleAerialSource.Resolve(VideoQuality.Hd1080H264, primaryWidth: 3840));
    }

    [Fact]
    public void Manifest_round_trips_through_serialisation()
    {
        // The disk cache writes what it parsed, so a lossy round trip would quietly
        // degrade the cached copy relative to the network one.
        var original = Manifest;
        var reparsed = AppleJson.Deserialize(AppleJson.Serialize(original));

        Assert.NotNull(reparsed);
        Assert.Equal(original.Assets.Count, reparsed!.Assets.Count);
        Assert.Equal(original.Categories.Count, reparsed.Categories.Count);
        Assert.Equal(original.Assets[0].Url4KSdr, reparsed.Assets[0].Url4KSdr);
        Assert.Equal(original.Assets[0].ShotId, reparsed.Assets[0].ShotId);
    }
}
