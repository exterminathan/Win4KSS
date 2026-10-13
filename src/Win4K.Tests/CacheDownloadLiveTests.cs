using Win4K.Core;
using Win4K.Core.Caching;

namespace Win4K.Tests;

/// <summary>
/// Exercises the real download and resume path against Apple's CDN. Inert unless
/// WIN4K_LIVE_TESTS=1.
///
/// Uses the ~1.9 MB manifest tarball as the payload rather than a 346 MB video: the code
/// under test is agnostic about content, and a third of a gigabyte per test run is not a
/// reasonable thing to inflict on a suite.
/// </summary>
public class CacheDownloadLiveTests : IDisposable
{
    private const string PayloadUrl = "https://sylvan.apple.com/Aerials/resources-16.tar";

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") == "1";

    private readonly string _root;

    public CacheDownloadLiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings => new()
    {
        CacheEnabled = true,
        CacheDirectory = _root,
        CacheBudgetGb = 1,
    };

    private static Clip Payload => new("payload", "Payload", "Test", TimeOfDay.Unknown,
                                       new Uri(PayloadUrl), false);

    [Fact]
    public async Task Downloads_and_stages_through_the_partial_directory()
    {
        if (!LiveEnabled) return;

        var cache = new VideoCache(Settings);

        Assert.True(await cache.DownloadAsync(Payload));
        Assert.True(cache.IsCached(Payload.Url));

        // Nothing may be left behind in staging.
        Assert.Empty(Directory.GetFiles(cache.PartialDirectory));

        var size = new FileInfo(cache.PathFor(Payload.Url)).Length;
        Assert.True(size > 1_000_000, $"expected a real payload, got {size} bytes");

        // And it now resolves to the local file rather than the origin.
        Assert.Equal(cache.PathFor(Payload.Url), cache.Resolve(Payload));
    }

    [Fact]
    public async Task Resumes_from_a_partial_file_rather_than_restarting()
    {
        if (!LiveEnabled) return;

        var cache = new VideoCache(Settings);

        // Fetch once to learn the true length.
        Assert.True(await cache.DownloadAsync(Payload));
        var full = new FileInfo(cache.PathFor(Payload.Url)).Length;

        // Now simulate an interrupted download: move the first half into staging and drop
        // the completed file.
        var key = VideoCache.KeyFor(Payload.Url);
        var bytes = await File.ReadAllBytesAsync(cache.PathFor(Payload.Url));
        var half = (int)(full / 2);

        File.Delete(cache.PathFor(Payload.Url));
        await File.WriteAllBytesAsync(Path.Combine(cache.PartialDirectory, key), bytes[..half]);

        // A fresh cache instance would wipe partials on construction, so reuse this one.
        Assert.True(await cache.DownloadAsync(Payload));

        var resumed = await File.ReadAllBytesAsync(cache.PathFor(Payload.Url));

        // The resumed file must be byte-identical — a Range request appended to the wrong
        // offset would produce a same-length-but-corrupt file, so compare contents, not size.
        Assert.Equal(full, resumed.Length);
        Assert.True(bytes.AsSpan().SequenceEqual(resumed), "resumed file differs from the original");
    }
}
