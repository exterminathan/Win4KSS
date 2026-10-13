using Win4K.Core;
using Win4K.Core.Caching;

namespace Win4K.Tests;

public class VideoCacheTests : IDisposable
{
    private readonly string _root;

    public VideoCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings(double budgetGb = 20) => new()
    {
        CacheEnabled = true,
        CacheDirectory = _root,
        CacheBudgetGb = budgetGb,
    };

    /// <summary>Budgets in these tests are naturally expressed in MB, not GB.</summary>
    private AppSettings SettingsMb(double budgetMb) => Settings(budgetMb / 1024);

    private static Clip Clip(string url, string title = "Test") =>
        new(title, title, "Cities", TimeOfDay.Unknown, new Uri(url), false);

    // ---- keys ---------------------------------------------------------------------------

    [Fact]
    public void Key_keeps_a_readable_stem_and_the_extension()
    {
        var key = VideoCache.KeyFor(new Uri("https://sylvan.apple.com/Aerials/2x/Videos/SE_A016_C009_SDR_4K_HEVC.mov"));

        Assert.StartsWith("SE_A016_C009_SDR_4K_HEVC.", key);
        Assert.EndsWith(".mov", key);
    }

    [Fact]
    public void Same_filename_from_different_hosts_does_not_collide()
    {
        // The exact bug cdima/aerial had: it keyed on Path.GetFileName(url) alone, so two
        // sources sharing a file name served each other's video.
        var a = VideoCache.KeyFor(new Uri("https://one.example/videos/beach.mov"));
        var b = VideoCache.KeyFor(new Uri("https://two.example/videos/beach.mov"));

        Assert.NotEqual(a, b);
        Assert.StartsWith("beach.", a);
        Assert.StartsWith("beach.", b);
    }

    [Fact]
    public void Key_is_stable_for_the_same_url()
    {
        var url = new Uri("https://sylvan.apple.com/x/y.mov");
        Assert.Equal(VideoCache.KeyFor(url), VideoCache.KeyFor(url));
    }

    [Fact]
    public void Key_never_contains_characters_illegal_in_a_filename()
    {
        var key = VideoCache.KeyFor(new Uri("https://example.test/a%20b/we:ird*name?.mov"));
        Assert.DoesNotContain(key, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void Key_supplies_an_extension_when_the_url_has_none()
    {
        Assert.EndsWith(".mov", VideoCache.KeyFor(new Uri("https://example.test/stream")));
    }

    // ---- resolution ----------------------------------------------------------------------

    [Fact]
    public void Uncached_clips_resolve_to_their_origin_url()
    {
        var cache = new VideoCache(Settings());
        var clip = Clip("https://example.test/a.mov");

        Assert.Equal(clip.PlayableUrl, cache.Resolve(clip));
    }

    [Fact]
    public void Uncached_clips_keep_their_percent_escapes()
    {
        // The archive-org stream bug: Uri.ToString() unescapes %20 into a space, libcurl
        // rejects the space, and every uncached archive clip failed with mpv error -13.
        var cache = new VideoCache(Settings());
        var clip = Clip("https://archive.org/download/LA01/LA%2001%20-%204k.mp4");

        var resolved = cache.Resolve(clip);

        Assert.Equal("https://archive.org/download/LA01/LA%2001%20-%204k.mp4", resolved);
        Assert.DoesNotContain(' ', resolved);
    }

    [Fact]
    public void Cached_clips_resolve_to_the_local_file()
    {
        var cache = new VideoCache(Settings());
        var clip = Clip("https://example.test/a.mov");

        var path = cache.PathFor(clip.Url);
        File.WriteAllBytes(path, new byte[1024]);

        Assert.Equal(path, cache.Resolve(clip));
        Assert.True(cache.IsCached(clip.Url));
    }

    [Fact]
    public void Local_clips_resolve_to_their_path_and_bypass_the_cache()
    {
        var cache = new VideoCache(Settings());
        var file = Path.Combine(_root, "local.mp4");
        File.WriteAllBytes(file, new byte[16]);

        var clip = new Clip(file, "local", "Local", TimeOfDay.Unknown, new Uri(file), IsLocal: true);

        Assert.Equal(file, cache.Resolve(clip));
    }

    // ---- staging -------------------------------------------------------------------------

    [Fact]
    public void Partial_downloads_are_never_treated_as_cache_hits()
    {
        var cache = new VideoCache(Settings());
        var clip = Clip("https://example.test/big.mov");

        // Simulate an interrupted download sitting in the staging area.
        File.WriteAllBytes(Path.Combine(cache.PartialDirectory, VideoCache.KeyFor(clip.Url)), new byte[4096]);

        Assert.False(cache.IsCached(clip.Url));
        Assert.Equal(clip.PlayableUrl, cache.Resolve(clip));
    }

    [Fact]
    public void Recent_partials_survive_construction_so_they_can_resume()
    {
        // Until Phase C every partial was deleted on startup, which threw away a 300 MB
        // download every time the saver was dismissed mid-clip. Only 7-day orphans go now
        // (CacheCrossProcessTests covers that with a fake clock).
        var settings = Settings();
        var partialDir = Path.Combine(_root, "partial");
        Directory.CreateDirectory(partialDir);
        File.WriteAllBytes(Path.Combine(partialDir, "leftover.mov"), new byte[128]);

        using var cache = new VideoCache(settings);

        Assert.Single(Directory.GetFiles(partialDir));
    }

    // ---- budget and eviction ---------------------------------------------------------------

    [Fact]
    public void Eviction_removes_the_least_recently_played_first()
    {
        // 3 MB budget against 5 x 1 MB files, so exactly two must go.
        var cache = new VideoCache(SettingsMb(3));

        var clips = Enumerable.Range(0, 5).Select(i => Clip($"https://example.test/{i}.mov", $"clip{i}")).ToList();
        foreach (var clip in clips)
            File.WriteAllBytes(cache.PathFor(clip.Url), new byte[1024 * 1024]);

        // Rebuild so the index adopts the files, then mark two as recently played.
        cache = new VideoCache(SettingsMb(3));
        cache.Resolve(clips[3]);
        cache.Resolve(clips[4]);

        var removed = cache.EvictToBudget();

        Assert.True(removed > 0, "expected eviction to remove something");
        Assert.True(cache.CurrentSizeBytes() <= SettingsMb(3).CacheBudgetBytes,
            $"cache is {cache.CurrentSizeBytes()} bytes, over the {SettingsMb(3).CacheBudgetBytes} budget");

        // The two most recently played must survive.
        Assert.True(cache.IsCached(clips[3].Url), "most recently played clip was evicted");
        Assert.True(cache.IsCached(clips[4].Url), "most recently played clip was evicted");
    }

    [Fact]
    public void Nothing_is_evicted_when_the_cache_fits()
    {
        var cache = new VideoCache(Settings(budgetGb: 20));
        var clip = Clip("https://example.test/small.mov");
        File.WriteAllBytes(cache.PathFor(clip.Url), new byte[2048]);

        Assert.Equal(0, cache.EvictToBudget());
        Assert.True(cache.IsCached(clip.Url));
    }

    [Fact]
    public void Purge_empties_the_cache()
    {
        var cache = new VideoCache(Settings());
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(cache.PathFor(new Uri($"https://example.test/{i}.mov")), new byte[512]);

        cache.Purge();

        Assert.Equal(0, cache.CurrentSizeBytes());
        Assert.Empty(Directory.GetFiles(cache.VideosDirectory));
    }

    [Fact]
    public void Index_forgets_files_deleted_behind_its_back()
    {
        var settings = Settings();
        var cache = new VideoCache(settings);
        var clip = Clip("https://example.test/gone.mov");
        File.WriteAllBytes(cache.PathFor(clip.Url), new byte[256]);

        // Adopt it, then delete the file by hand and rebuild.
        cache = new VideoCache(settings);
        Assert.Single(cache.Entries());

        File.Delete(cache.PathFor(clip.Url));
        cache = new VideoCache(settings);

        Assert.Empty(cache.Entries());
    }

    [Fact]
    public void Index_adopts_files_that_appear_without_it()
    {
        var settings = Settings();
        var cache = new VideoCache(settings);
        File.WriteAllBytes(Path.Combine(cache.VideosDirectory, "handcopied.abc12345.mov"), new byte[777]);

        cache = new VideoCache(settings);

        var entry = Assert.Single(cache.Entries());
        Assert.Equal(777, entry.SizeBytes);
    }

    // ---- formatting -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(346_649_491, "330.59 MB")]
    [InlineData(40_000_000_000, "37.25 GB")]
    public void Sizes_format_the_way_a_user_expects(long bytes, string expected)
    {
        Assert.Equal(expected, VideoCache.FormatSize(bytes));
    }
}
