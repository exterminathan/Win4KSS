using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// Kept-offline clips: never evicted, counted against the one budget, refused when they would
/// not fit, and re-downloaded at a new quality with the old file released only afterwards.
/// </summary>
public class CachePinTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    private readonly string _root;
    private readonly string _settingsFile;
    private readonly List<IDisposable> _disposables = [];

    public CachePinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-pin-" + Guid.NewGuid().ToString("N")[..8]);
        _settingsFile = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables))
            d.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings(double budgetGb = 20) => new()
    {
        CacheEnabled = true,
        CacheDirectory = Path.Combine(_root, "cache"),
        CacheBudgetGb = budgetGb,
    };

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static Clip Clip(int i) =>
        new($"ia:clip{i}", $"clip{i}", "Test", TimeOfDay.Unknown, new Uri($"https://o.example/clip{i}.mp4"), false);

    private static ClipInfo Info(string key, long? size, string url, string? url1080 = null, string source = "archive-org") => new()
    {
        Key = key,
        SourceId = source,
        CatalogId = source,
        Title = key,
        SizeBytes = size,
        Urls = new ClipUrls(Uhd4KSdr: url, Hd1080Sdr: url1080),
    };

    // ---- eviction ---------------------------------------------------------------------------

    [Fact]
    public void Pinned_files_are_never_evicted_even_when_least_recently_played()
    {
        var clock = new ManualTimeProvider();
        var settings = Settings(budgetGb: 3.0 / 1024); // 3 MB
        var clips = Enumerable.Range(0, 5).Select(Clip).ToList();

        var cache = Track(new VideoCache(settings, time: clock));
        foreach (var clip in clips)
            File.WriteAllBytes(cache.PathFor(clip.Url), new byte[MB]);

        // Adopt the files, then play them oldest-first so clip0 and clip1 are the LRU pair.
        cache = Track(new VideoCache(settings, time: clock));
        foreach (var clip in clips)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            cache.Resolve(clip);
        }

        settings.KeptOffline.UnionWith([clips[0].Id, clips[1].Id]);

        Assert.Equal(2, cache.EvictToBudget());

        Assert.True(cache.IsCached(clips[0].Url), "pinned clip0 was evicted");
        Assert.True(cache.IsCached(clips[1].Url), "pinned clip1 was evicted");
        Assert.False(cache.IsCached(clips[2].Url));
        Assert.False(cache.IsCached(clips[3].Url));
        Assert.True(cache.IsCached(clips[4].Url), "the most recently played rolling clip should stay");

        var stats = cache.Stats();
        Assert.Equal(2 * MB, stats.PinnedBytes);
        Assert.Equal(1 * MB, stats.RollingBytes);
    }

    [Fact]
    public void When_pins_alone_exceed_the_budget_the_rolling_cache_empties_and_pins_stay()
    {
        var settings = Settings(budgetGb: 1.0 / 1024); // 1 MB
        var clips = Enumerable.Range(0, 4).Select(Clip).ToList();

        var cache = Track(new VideoCache(settings));
        foreach (var clip in clips)
            File.WriteAllBytes(cache.PathFor(clip.Url), new byte[MB]);
        cache = Track(new VideoCache(settings));
        clips.ForEach(c => cache.Resolve(c));

        settings.KeptOffline.UnionWith([clips[0].Id, clips[1].Id]);
        cache.EvictToBudget();

        Assert.True(cache.IsCached(clips[0].Url));
        Assert.True(cache.IsCached(clips[1].Url));
        Assert.False(cache.IsCached(clips[2].Url));
        Assert.False(cache.IsCached(clips[3].Url));
        Assert.Equal(0, cache.Stats().RollingBytes);
    }

    [Fact]
    public void Purge_unpinned_keeps_kept_offline_clips()
    {
        var settings = Settings();
        var clips = Enumerable.Range(0, 3).Select(Clip).ToList();
        var cache = Track(new VideoCache(settings));
        foreach (var clip in clips)
            File.WriteAllBytes(cache.PathFor(clip.Url), new byte[1000]);
        cache = Track(new VideoCache(settings));
        clips.ForEach(c => cache.Resolve(c));
        settings.KeptOffline.Add(clips[1].Id);

        Assert.Equal(2, cache.PurgeUnpinned());

        Assert.True(cache.IsCached(clips[1].Url));
        Assert.False(cache.IsCached(clips[0].Url));
        Assert.False(cache.IsCached(clips[2].Url));
    }

    [Fact]
    public void Pins_made_in_another_process_are_read_live_from_settings_json()
    {
        var running = Settings(budgetGb: 1.0 / 1024);
        var clips = Enumerable.Range(0, 2).Select(Clip).ToList();
        var cache = Track(new VideoCache(running));
        foreach (var clip in clips)
            File.WriteAllBytes(cache.PathFor(clip.Url), new byte[MB]);
        cache = Track(new VideoCache(running));
        clips.ForEach(c => cache.Resolve(c));

        running.Save(_settingsFile);
        cache.PinnedKeys = PinnedKeySource.FromSettingsFile(running, _settingsFile);

        // The browser pins clip0 (the LRU one) after the saver loaded its settings.
        var browser = AppSettings.Load(_settingsFile);
        browser.KeptOffline.Add(clips[0].Id);
        browser.Save(_settingsFile);
        File.SetLastWriteTimeUtc(_settingsFile, DateTime.UtcNow.AddSeconds(5));

        cache.EvictToBudget();

        Assert.True(cache.IsCached(clips[0].Url), "a pin made in the browser was evicted by the saver");
        Assert.False(cache.IsCached(clips[1].Url));
    }

    // ---- budget -----------------------------------------------------------------------------

    [Fact]
    public void A_pin_that_would_exceed_the_budget_is_refused_with_the_limit_that_would_fit()
    {
        var settings = Settings(budgetGb: 2);
        var cache = Track(new VideoCache(settings, new FakeOrigin().Client()));
        var queue = Track(new DownloadQueue(cache)); // never started: nothing downloads
        var pins = new PinManager(settings, cache, queue, s => s.Save(_settingsFile));
        pins.SetCatalog([
            Info("ia:a", 1 * GB, "https://o.example/a.mp4"),
            Info("ia:b", 1 * GB, "https://o.example/b.mp4"),
            Info("ia:c", 1 * GB, "https://o.example/c.mp4"),
        ]);

        var first = pins.Pin(["ia:a", "ia:b"]);
        Assert.True(first.Ok);
        Assert.Equal(["ia:a", "ia:b"], settings.KeptOffline.Order());
        Assert.Equal(DownloadPriority.Pin, queue.Get("ia:a")?.Priority);

        var refused = pins.Pin(["ia:c"]);
        Assert.False(refused.Ok);
        Assert.Equal(3 * GB, refused.RequiredBytes);
        Assert.Equal(1 * GB, refused.ShortfallBytes);
        Assert.Equal(3, refused.RequiredGb);
        Assert.Equal(["ia:c"], refused.Added);
        Assert.DoesNotContain("ia:c", settings.KeptOffline);
        Assert.Null(queue.Get("ia:c"));

        // "Raise limit to 3 GB?" — yes.
        settings.CacheBudgetGb = refused.RequiredGb;
        Assert.True(pins.Pin(["ia:c"]).Ok);
        Assert.Contains("ia:c", AppSettings.Load(_settingsFile).KeptOffline);
    }

    [Fact]
    public void Sizes_fall_back_to_the_cached_file_then_to_an_estimate()
    {
        var settings = Settings();
        var cache = Track(new VideoCache(settings, new FakeOrigin().Client()));
        var queue = Track(new DownloadQueue(cache));
        var pins = new PinManager(settings, cache, queue, _ => { });

        var cachedUrl = new Uri("https://o.example/cached.mp4");
        File.WriteAllBytes(cache.PathFor(cachedUrl), new byte[5000]);
        pins.SetCatalog([
            Info("ia:cached", null, cachedUrl.AbsoluteUri),
            Info("ia:unknown", null, "https://o.example/unknown.mp4"),
            Info("ia:sized", 7777, "https://o.example/sized.mp4"),
            new ClipInfo { Key = "local:C:\\v.mp4", SourceId = "local", Title = "v", LocalPath = "C:\\v.mp4" },
        ]);

        Assert.Equal(5000, pins.EstimatedBytes("ia:cached"));
        Assert.Equal(7777, pins.EstimatedBytes("ia:sized"));
        Assert.Equal(PinManager.UnknownClipBytes, pins.EstimatedBytes("ia:unknown"));

        var check = pins.CanPin(["ia:cached", "ia:sized", "local:C:\\v.mp4", "nope:missing"]);
        Assert.True(check.Ok);
        Assert.Equal(5000 + 7777, check.RequiredBytes);
        Assert.Equal(["local:C:\\v.mp4", "nope:missing"], check.Skipped);
    }

    [Fact]
    public void Unpinning_cancels_the_pending_download_and_frees_the_key()
    {
        var settings = Settings();
        var cache = Track(new VideoCache(settings, new FakeOrigin().Client()));
        var queue = Track(new DownloadQueue(cache));
        var pins = new PinManager(settings, cache, queue, _ => { });
        pins.SetCatalog([Info("ia:a", 1000, "https://o.example/a.mp4")]);

        pins.Pin(["ia:a"]);
        Assert.NotNull(queue.Get("ia:a"));

        Assert.Equal(1, pins.Unpin(["ia:a", "ia:never"]));
        Assert.Null(queue.Get("ia:a"));
        Assert.Empty(settings.KeptOffline);
    }

    // ---- quality change ---------------------------------------------------------------------

    [Fact]
    public async Task A_quality_change_re_downloads_pins_and_releases_the_old_file_only_once_the_new_one_lands()
    {
        var origin = new FakeOrigin();
        const string u4k = "https://sylvan.example/Q_SDR_4K_HEVC.mov";
        const string u1080 = "https://sylvan.example/Q_SDR_2K_HEVC.mov";
        origin.Serve(u4k, FakeOrigin.Bytes(80_000, 4));
        origin.Serve(u1080, FakeOrigin.Bytes(30_000, 1));

        var settings = Settings();
        settings.Quality = VideoQuality.Hd1080Sdr;
        var cache = Track(new VideoCache(settings, origin.Client()));
        var queue = Track(new DownloadQueue(cache, new DownloadQueueOptions { DownloadDelay = TimeSpan.Zero }));
        var pins = new PinManager(settings, cache, queue, _ => { });
        pins.SetCatalog([Info("apple:Q", 80_000, u4k, u1080, source: "apple")]);
        queue.Start();

        Assert.True(pins.Pin(["apple:Q"]).Ok);
        var old = new Uri(u1080);
        await Eventually.True(() => cache.IsCached(old), "the 1080p pin");

        // Switch to 4K, with the new download held at the origin.
        origin.Gated = true;
        settings.Quality = VideoQuality.Uhd4KSdr;
        Assert.True(pins.SyncPins().Ok);
        await Eventually.True(() => origin.Urls.Contains(new Uri(u4k).AbsoluteUri), "the 4K re-download to start");

        // Until it lands, the old rendition is still the pinned one: eviction cannot touch it.
        settings.CacheBudgetGb = 1.0 / 1024 / 1024; // 1 KB
        cache.EvictToBudget();
        Assert.True(cache.IsCached(old), "the old rendition was released before the new one landed");
        Assert.Equal(30_000, cache.Stats().PinnedBytes);
        settings.CacheBudgetGb = 20;

        origin.Release(10);
        await Eventually.True(() => cache.IsCached(new Uri(u4k)) && cache.EntriesFor("apple:Q").Count == 1, "the swap");
        Assert.False(cache.IsCached(old), "the old rendition should be released once the new one landed");

        var entry = Assert.Single(cache.EntriesFor("apple:Q"));
        Assert.Equal(VideoCache.KeyFor(new Uri(u4k)), entry.Key);
        Assert.Equal(80_000, cache.Stats().PinnedBytes);
        await Eventually.True(() => queue.Get("apple:Q") is null, "the job to retire");
    }
}
