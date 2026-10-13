using Win4K.Core;
using Win4K.Core.Catalogs;
using F = Win4K.Core.Catalogs.TagFacet;

namespace Win4K.Tests;

/// <summary>
/// One sample clip per source, shaped as that source's catalogue produces it, with its exact
/// tag set. Between them they cover every facet group in the plan: Source, Category,
/// Subcategory, Subject, Time of day, Resolution, Aspect, Dynamic range, Duration, Codec,
/// Licence and State. Asserting the whole set (not just Contains) also pins what a source
/// must NOT be tagged with.
/// </summary>
public class TagBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static void AssertTags(IEnumerable<ClipTag> actual, params (TagFacet Facet, string Value)[] expected)
    {
        Assert.Equal(
            expected.Select(e => new ClipTag(e.Facet, e.Value)).OrderBy(t => t.Facet).ThenBy(t => t.Value, StringComparer.Ordinal),
            actual.OrderBy(t => t.Facet).ThenBy(t => t.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void Apple_aerial_merged_with_tvOS_26()
    {
        var clip = new ClipInfo
        {
            Key = "apple:009BA758-7060-4479-8EE8-FB9B40C8FB97",
            SourceId = "apple",
            SourceIds = ["apple", "apple-tvos26"],
            Title = "Korea and Japan Night",
            Category = "Space",
            Subcategory = "Korea and Japan Night",
            Width = 3840,
            Height = 2160,
            Codec = "hevc",
            TimeOfDay = TimeOfDay.Night,
            Urls = new ClipUrls(Uhd4KSdr: "https://a/sdr.mov", Uhd4KHdr: "https://a/hdr.mov", Hd1080Sdr: "https://a/1080.mov"),
        };

        AssertTags(TagBuilder.Build(clip, ClipState.InRotation | ClipState.Cached, Now),
            (F.Source, "Apple"),
            (F.Category, "Space"),
            (F.Subcategory, "Korea and Japan Night"),
            (F.TimeOfDay, "Night"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.DynamicRange, "HDR available"),
            (F.Codec, "HEVC"),
            (F.State, "In rotation"),
            (F.State, "Cached"));
    }

    [Fact]
    public void Internet_Archive_stock_clip()
    {
        var clip = new ClipInfo
        {
            Key = "ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4",
            SourceId = "archive-org",
            Title = "Fire 66",
            Category = "Nature",
            Creator = "Mitch Martinez",
            Licence = "CC BY-NC-ND 3.0",
            Subjects = ["fire", "slow motion", "fire stock footage", "stock footage", "free stock footage", "4k", "mitch martinez"],
            Width = 4096,
            Height = 2160,
            DurationSeconds = 55.5,
            SizeBytes = 83_072_197,
            Codec = "h264",
            Urls = new ClipUrls(Uhd4KSdr: "https://archive.org/download/x/y.mp4"),
        };

        // Subjects: the label prefix ("Fire") first, the source's own keywords after it,
        // stop-worded ("stock footage", "4k") and without the creator's name.
        AssertTags(TagBuilder.Build(clip, ClipState.KeptOffline | ClipState.Downloading, Now),
            (F.Source, "Internet Archive"),
            (F.Category, "Nature"),
            (F.Subject, "Fire"),
            (F.Subject, "Slow Motion"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.Duration, "30–60 s"),
            (F.Codec, "H.264"),
            (F.Licence, "CC BY-NC-ND 3.0"),
            (F.State, "Kept offline"),
            (F.State, "Downloading"));
    }

    [Fact]
    public void Internet_Archive_pixabay_mirror_in_8K_with_no_category()
    {
        var clip = new ClipInfo
        {
            Key = "ia:pixabay-1/video-1_source.mp4",
            SourceId = "archive-org",
            Title = "Sea, Wave, Golden, Sand, Sunrise",
            Category = "Mixed",
            Creator = "Pixabay",
            Licence = "Pixabay Content License",
            Subjects = ["Sea; Wave;Sunrise;4k;UHD;Ultra High Definition;Pixabay;stock footage"],
            Width = 7680,
            Height = 4320,
            DurationSeconds = 9,
            Codec = "h264",
            Urls = new ClipUrls(Uhd4KSdr: "https://archive.org/download/x/y.mp4"),
        };

        // A comma-list title is not a label prefix; "Mixed" is not a category.
        AssertTags(TagBuilder.Build(clip, ClipState.None, Now),
            (F.Source, "Internet Archive"),
            (F.Subject, "Sea"),
            (F.Subject, "Wave"),
            (F.Subject, "Sunrise"),
            (F.Resolution, "8K"),
            (F.Aspect, "16:9"),
            (F.Duration, "<30 s"),
            (F.Codec, "H.264"),
            (F.Licence, "Pixabay Content License"));
    }

    [Fact]
    public void Nasa_orbital_reel()
    {
        var clip = new ClipInfo
        {
            Key = "nasa:jsc2022m000172_Earth_in_4K_Expedition_65_Edition",
            SourceId = "nasa",
            Title = "Earth from Space in 4K – Expedition 65 Edition",
            Category = "Space",
            Creator = "NASA JSC",
            Licence = "NASA Media (public domain)",
            Subjects = ["earthviews", "Expedition 65 Resource Reel"],
            Width = 4096,
            Height = 2160,
            DurationSeconds = 3000,
            Codec = "h264",
            Urls = new ClipUrls(Uhd4KSdr: "https://images-assets.nasa.gov/video/x/x~orig.mp4"),
        };

        // NASA keywords are kept for search, not tagged: they run to dozens per clip.
        AssertTags(TagBuilder.Build(clip, ClipState.Unhealthy, Now),
            (F.Source, "NASA"),
            (F.Category, "Space"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.Duration, "10 min+"),
            (F.Codec, "H.264"),
            (F.Licence, "NASA Media (public domain)"),
            (F.State, "Unhealthy"));
    }

    [Fact]
    public void Amazon_ambient_clip_added_recently_is_New()
    {
        var clip = new ClipInfo
        {
            Key = "amazon:content-82f845c1-85ae-4eba-b57a-7860f1e8e998",
            SourceId = "amazon-fireos8",
            Title = "Magdalena Bay Sunset",
            Category = "Sea",
            Creator = "Amazon",
            Width = 3840,
            Height = 2160,
            Codec = "hevc",
            TimeOfDay = TimeOfDay.Sunset,
            AddedUtc = Now.AddDays(-3),
            Urls = new ClipUrls(Uhd4KSdr: "https://aebcs/UHD/x.mp4", Hd1080Sdr: "https://aebcs/FHD/x.mp4"),
        };

        AssertTags(TagBuilder.Build(clip, ClipState.None, Now),
            (F.Source, "Amazon"),
            (F.Category, "Sea"),
            (F.TimeOfDay, "Sunset"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.Codec, "HEVC"),
            (F.State, "New"));
    }

    [Fact]
    public void Jetson_community_clip_groups_by_region_and_city_scene()
    {
        var clip = new ClipInfo
        {
            Key = "jetson:video_inspire_oregon_lone-ranch-beach_00015",
            SourceId = "jetson-creative",
            Title = "Lone Ranch Beach",
            Category = "City", // the vendor scene; shares a tag with Apple's "Cities"
            Subcategory = "Oregon",
            Creator = "Jetson Creative",
            Width = 3840,
            Height = 2160,
            Codec = "hevc",
            TimeOfDay = TimeOfDay.Sunrise,
            AddedUtc = Now.AddDays(-60), // outside the New window
            Urls = new ClipUrls(Uhd4KSdr: "https://github.com/x.4k-sdr.mov"),
        };

        AssertTags(TagBuilder.Build(clip, ClipState.None, Now),
            (F.Source, "Jetson"),
            (F.Category, "Cities"),
            (F.Subcategory, "Oregon"),
            (F.TimeOfDay, "Sunrise"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.Codec, "HEVC"));
    }

    [Fact]
    public void Robin_community_clip()
    {
        var clip = new ClipInfo
        {
            Key = "robin:robinfrcd_capmechant_202304",
            SourceId = "robin-fourcade",
            Title = "Cap Méchant",
            Category = "Sea",
            Subcategory = "France",
            Creator = "Robin Fourcade",
            Width = 3840,
            Height = 2160,
            Codec = "hevc",
            TimeOfDay = TimeOfDay.Day,
            Urls = new ClipUrls(Uhd4KSdr: "https://github.com/x_4k_hevc.m4v"),
        };

        AssertTags(TagBuilder.Build(clip, ClipState.New, Now),
            (F.Source, "Robin"),
            (F.Category, "Sea"),
            (F.Subcategory, "France"),
            (F.TimeOfDay, "Day"),
            (F.Resolution, "4K"),
            (F.Aspect, "16:9"),
            (F.Codec, "HEVC"),
            (F.State, "New"));
    }

    [Fact]
    public void Local_file_in_a_wide_format()
    {
        var clip = new ClipInfo
        {
            Key = @"local:D:\Clips\anamorphic.mp4",
            SourceId = "local",
            Title = "anamorphic",
            Category = "Local",
            LocalPath = @"D:\Clips\anamorphic.mp4",
            Width = 5760,
            Height = 2400,
            DurationSeconds = 240,
        };

        AssertTags(TagBuilder.Build(clip, ClipState.InRotation, Now),
            (F.Source, "Local"),
            (F.Category, "Local"),
            (F.Resolution, "5K+"),
            (F.Aspect, "Ultrawide"),
            (F.Duration, "3–10 min"),
            (F.State, "In rotation"));
    }

    [Fact]
    public void Static_tags_on_ClipInfo_carry_no_state()
    {
        // ClipInfo.Tags is built without state or a clock; state is layered on live.
        var clip = new ClipInfo { Key = "k", SourceId = "nasa", Title = "t", AddedUtc = DateTime.UtcNow };
        Assert.DoesNotContain(TagBuilder.Build(clip), t => t.Facet == F.State);
    }

    [Theory]
    [InlineData(1920, "1080p")]
    [InlineData(3840, "4K")]
    [InlineData(4096, "4K")]
    [InlineData(5120, "5K+")]
    [InlineData(6144, "5K+")]
    [InlineData(7680, "8K")]
    public void Resolution_buckets(int width, string expected)
    {
        Assert.Equal(expected, TagBuilder.Resolution(new ClipInfo { Key = "k", SourceId = "s", Title = "t", Width = width }));
    }

    [Theory]
    [InlineData(4096, 2160, "16:9")]   // DCI 1.90:1 still fills a 16:9 panel
    [InlineData(4096, 2304, "16:9")]
    [InlineData(4096, 1728, "Ultrawide")]  // 2.37:1 letterboxes
    [InlineData(1080, 1920, null)]     // portrait
    public void Aspect_buckets(int width, int height, string? expected)
    {
        Assert.Equal(expected, TagBuilder.Aspect(width, height));
    }

    [Theory]
    [InlineData(29.9, "<30 s")]
    [InlineData(30, "30–60 s")]
    [InlineData(60, "1–3 min")]
    [InlineData(180, "3–10 min")]
    [InlineData(600, "10 min+")]
    [InlineData(0, null)]
    public void Duration_buckets(double seconds, string? expected)
    {
        Assert.Equal(expected, TagBuilder.DurationBucket(seconds));
    }

    [Theory]
    [InlineData("Thin Smoke 98 - 30s - 4k res", "Thin Smoke")]
    [InlineData("Pocket Watch a42", "Pocket Watch")]
    [InlineData("Timelapse Clouds3", "Timelapse Clouds")]
    [InlineData("video-20001_large", null)]
    [InlineData("Sea, Wave, Sand", null)]
    public void Label_prefix_is_the_clip_name_without_its_number(string title, string? expected)
    {
        Assert.Equal(expected, TagBuilder.LabelPrefix(title));
    }

    [Fact]
    public void Pure_same_input_same_tags()
    {
        var clip = new ClipInfo { Key = "k", SourceId = "archive-org", Title = "Fire 66", Subjects = ["fire"], Width = 4096, Height = 2160 };
        Assert.Equal(TagBuilder.Build(clip, ClipState.Cached, Now), TagBuilder.Build(clip, ClipState.Cached, Now));
    }
}
