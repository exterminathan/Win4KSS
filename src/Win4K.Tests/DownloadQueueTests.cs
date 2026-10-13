using System.Collections.Concurrent;
using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

public class DownloadQueueTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];

    public DownloadQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-dq-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Queues first, so their workers stop before the caches go.
        foreach (var d in Enumerable.Reverse(_disposables))
            d.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings() => new() { CacheEnabled = true, CacheDirectory = _root, CacheBudgetGb = 20 };

    private VideoCache Cache(FakeOrigin origin)
    {
        var cache = new VideoCache(Settings(), origin.Client()) { Delay = (_, _) => Task.CompletedTask };
        _disposables.Add(cache);
        return cache;
    }

    private DownloadQueue Queue(VideoCache cache, DownloadQueueOptions? options = null)
    {
        var queue = new DownloadQueue(cache, options ?? new DownloadQueueOptions
        {
            DownloadDelay = TimeSpan.Zero,
            OwnershipRetryInterval = TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.Zero,
        });
        _disposables.Add(queue);
        return queue;
    }

    private sealed record PersistedPin(string ClipKey, string Url);

    /// <summary>queue.json as another process would read it.</summary>
    private static List<PersistedPin> ReadQueueFile(string root)
    {
        var path = Path.Combine(root, DownloadQueue.FileName);
        string text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return [];
                text = File.ReadAllText(path);
                break;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(10); // being replaced by the queue right now
            }
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("jobs").EnumerateArray()
                  .Select(j => new PersistedPin(j.GetProperty("clipKey").GetString()!, j.GetProperty("url").GetString()!))
                  .ToList();
    }

    // ---- priority and concurrency -----------------------------------------------------------

    [Fact]
    public async Task Pins_run_before_download_ahead_before_background_two_at_a_time()
    {
        var origin = new FakeOrigin { Gated = true };
        var bg1 = origin.Add("ia:bg1", "https://o.example/bg1.mp4", 1000);
        var bg2 = origin.Add("ia:bg2", "https://o.example/bg2.mp4", 1000);
        var ahead = origin.Add("ia:ahead", "https://o.example/ahead.mp4", 1000);
        var pinA = origin.Add("ia:pinA", "https://o.example/pinA.mp4", 1000);
        var pinB = origin.Add("ia:pinB", "https://o.example/pinB.mp4", 1000);

        var cache = Cache(origin);
        var queue = Queue(cache);

        // Lowest priority first, so arrival order cannot explain the result.
        queue.Enqueue(bg1, DownloadPriority.Background);
        queue.Enqueue(bg2, DownloadPriority.Background);
        queue.Enqueue(ahead, DownloadPriority.DownloadAhead);
        queue.Enqueue(pinA, DownloadPriority.Pin);
        queue.Enqueue(pinB, DownloadPriority.Pin);
        queue.Start();

        await Eventually.True(() => origin.Requests.Count == 2, "two downloads to start");
        await Task.Delay(200);
        Assert.Equal(2, origin.Requests.Count);
        Assert.Equal(2, origin.InFlight);
        Assert.Equal(
            new[] { pinA.Url.AbsoluteUri, pinB.Url.AbsoluteUri }.Order(),
            origin.Urls.Order());

        origin.Release();
        await Eventually.True(() => origin.Requests.Count == 3, "the third download");
        Assert.Equal(ahead.Url.AbsoluteUri, origin.Urls[2]);

        origin.Release();
        await Eventually.True(() => origin.Requests.Count == 4, "the fourth download");
        Assert.Equal(bg1.Url.AbsoluteUri, origin.Urls[3]);

        origin.Release(10);
        await Eventually.True(() => new[] { bg1, bg2, ahead, pinA, pinB }.All(c => cache.IsCached(c.Url)), "everything cached");

        Assert.Equal(2, origin.MaxInFlight);

        // The file lands a moment before its job is retired.
        await Eventually.True(() => queue.Snapshot().Count == 0, "the queue to drain");
    }

    [Fact]
    public async Task Progress_and_state_changes_are_reported_per_clip()
    {
        var origin = new FakeOrigin();
        var clip = origin.Add("ia:prog", "https://o.example/prog.mp4", 3 * 1024 * 1024 + 17);
        var cache = Cache(origin);
        var queue = Queue(cache);

        var states = new ConcurrentQueue<DownloadState>();
        var progress = new ConcurrentQueue<DownloadStatus>();
        queue.JobChanged += (_, e) => states.Enqueue(e.Status.State);
        queue.Progress += (_, e) => progress.Enqueue(e.Status);

        queue.Start();
        queue.Enqueue(clip, DownloadPriority.Pin);

        await Eventually.True(() => states.Contains(DownloadState.Done), "the download to finish");
        Assert.Equal([DownloadState.Queued, DownloadState.Downloading, DownloadState.Done], states.ToArray());
        Assert.All(progress, p => Assert.Equal(clip.Id, p.ClipKey));
        Assert.Equal(1.0, progress.Last().Fraction);
        Assert.Equal(3 * 1024 * 1024 + 17, progress.Last().BytesReceived);
    }

    [Fact]
    public async Task Download_ahead_takes_the_next_clip_and_later_ones_only_when_cheap()
    {
        var origin = new FakeOrigin();
        var next = origin.Add("ia:next", "https://o.example/next.mp4", 1000);
        var big = origin.Add("ia:big", "https://o.example/big.mp4", 1000, declaredSize: 900L * 1024 * 1024);
        var cache = Cache(origin);
        var queue = Queue(cache);
        queue.Start();

        queue.Request([next, big], currentIsStreaming: false);

        await Eventually.True(() => cache.IsCached(next.Url), "the next clip");
        await Task.Delay(100);
        Assert.False(cache.IsCached(big.Url));
        Assert.DoesNotContain(big.Url.AbsoluteUri, origin.Urls);
    }

    // ---- cancel ----------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_removes_a_queued_job_and_stops_a_running_one()
    {
        var origin = new FakeOrigin { Gated = true };
        var a = origin.Add("ia:a", "https://o.example/a.mp4", 1000);
        var b = origin.Add("ia:b", "https://o.example/b.mp4", 1000);
        var c = origin.Add("ia:c", "https://o.example/c.mp4", 1000);
        var cache = Cache(origin);
        var queue = Queue(cache);

        var cancelled = new ConcurrentQueue<string>();
        queue.JobChanged += (_, e) =>
        {
            if (e.Status.State == DownloadState.Cancelled)
                cancelled.Enqueue(e.Status.ClipKey);
        };

        queue.Enqueue(a, DownloadPriority.Pin);
        queue.Enqueue(b, DownloadPriority.Pin);
        queue.Enqueue(c, DownloadPriority.Pin);
        queue.Start();
        await Eventually.True(() => origin.Requests.Count == 2, "a and b to start");

        // Queued: gone at once, never requested, and out of queue.json.
        Assert.True(queue.Cancel(c.Id));
        Assert.Contains(c.Id, cancelled);
        Assert.Null(queue.Get(c.Id));

        // Running: its request is cancelled and it reports Cancelled.
        Assert.True(queue.Cancel(a.Id));
        await Eventually.True(() => cancelled.Contains(a.Id), "a to report cancelled");
        Assert.Null(queue.Get(a.Id));

        Assert.Equal([b.Id], ReadQueueFile(_root).Select(j => j.ClipKey));

        origin.Release(10);
        await Eventually.True(() => cache.IsCached(b.Url), "b to finish");
        Assert.False(cache.IsCached(a.Url));
        Assert.False(cache.IsCached(c.Url));
        Assert.DoesNotContain(c.Url.AbsoluteUri, origin.Urls);
        Assert.False(File.Exists(cache.PartialPathFor(a.Url)), "a cancelled pin's partial is discarded");
        await Eventually.True(() => ReadQueueFile(_root).Count == 0, "b to leave queue.json");
    }

    // ---- persistence ----------------------------------------------------------------------

    [Fact]
    public async Task Pins_persist_in_queue_json_and_resume_in_a_new_instance()
    {
        var origin = new FakeOrigin { Gated = true };
        var x = origin.Add("ia:x", "https://o.example/x.mp4", 5000, 1);
        var y = origin.Add("ia:y", "https://o.example/y.mp4", 5000, 2);
        var ahead = origin.Add("ia:ahead", "https://o.example/ahead.mp4", 5000, 3);

        var cache1 = Cache(origin);
        var queue1 = Queue(cache1);
        queue1.Enqueue(x, DownloadPriority.Pin);
        queue1.Enqueue(y, DownloadPriority.Pin);
        queue1.Enqueue(ahead, DownloadPriority.DownloadAhead);
        queue1.Start();
        await Eventually.True(() => origin.Requests.Count == 2, "both pins to start");

        // The session ends mid-download.
        await queue1.StopAsync();
        queue1.Dispose();
        cache1.Dispose();

        var persisted = ReadQueueFile(_root);
        Assert.Equal([x.Id, y.Id], persisted.Select(j => j.ClipKey).Order());
        Assert.Equal(x.Url.AbsoluteUri, persisted.Single(j => j.ClipKey == x.Id).Url);

        origin.Gated = false;
        origin.Release(10);

        var cache2 = Cache(origin);
        var queue2 = Queue(cache2);
        queue2.Start();

        await Eventually.True(() => cache2.IsCached(x.Url) && cache2.IsCached(y.Url), "the pins to resume");
        Assert.Equal(origin.Body(x), await File.ReadAllBytesAsync(cache2.PathFor(x.Url)));
        await Eventually.True(() => ReadQueueFile(_root).Count == 0, "finished pins to leave queue.json");

        // Download-ahead is ephemeral: it does not come back.
        Assert.False(cache2.IsCached(ahead.Url));
        Assert.Equal(x.Id, cache2.Entries().Single(e => e.Key == VideoCache.KeyFor(x.Url)).ClipKey);
    }

    // ---- ownership -------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_owner_downloads_pins_and_ownership_passes_on_when_it_exits()
    {
        var origin = new FakeOrigin();
        var fromOther = origin.Add("ia:other", "https://o.example/other.mp4", 3000, 1);
        var later = origin.Add("ia:later", "https://o.example/later.mp4", 3000, 2);
        var aheadClip = origin.Add("ia:ahead", "https://o.example/ahead.mp4", 3000, 3);

        var browserCache = Cache(origin);
        var browser = Queue(browserCache);
        browser.Start();
        Assert.True(browser.OwnsPins);

        var saverCache = Cache(origin);
        var saver = Queue(saverCache);
        var ownership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        saver.OwnershipChanged += (_, _) => ownership.TrySetResult();
        saver.Start();
        Assert.False(saver.OwnsPins);

        // A pin recorded by the non-owner is downloaded by the owner, via queue.json...
        saver.Enqueue(fromOther, DownloadPriority.Pin);
        await Eventually.True(() => browserCache.IsCached(fromOther.Url), "the owner to download the other's pin");

        // ...and the non-owner sees it finish through the same file.
        await Eventually.True(() => saver.Get(fromOther.Id) is null, "the non-owner to drop the finished pin");

        // The non-owner still does its own download-ahead.
        saver.Request([aheadClip], currentIsStreaming: false);
        await Eventually.True(() => saverCache.IsCached(aheadClip.Url), "the non-owner's download-ahead");

        // The owner exits; the other takes over and drains pins itself.
        browser.Dispose();
        await ownership.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(saver.OwnsPins);

        saver.Enqueue(later, DownloadPriority.Pin);
        await Eventually.True(() => saverCache.IsCached(later.Url), "the new owner to download its pin");
    }

    [Fact]
    public async Task A_non_owner_never_starts_a_pin_itself()
    {
        var origin = new FakeOrigin();
        var pin = origin.Add("ia:pin", "https://o.example/pin.mp4", 3000);

        // Another process holds the owner mutex but never looks at queue.json.
        using var lease = new HeldOwnership(_root);

        var cache = Cache(origin);
        var queue = Queue(cache);
        queue.Start();
        queue.Enqueue(pin, DownloadPriority.Pin);

        await Task.Delay(300);
        Assert.Empty(origin.Requests);
        Assert.Equal(DownloadState.Queued, queue.Get(pin.Id)?.State);
    }

    /// <summary>Holds the owner mutex the way another process would.</summary>
    private sealed class HeldOwnership : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public HeldOwnership(string root)
        {
            using var held = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, CacheMutexNames.DownloadQueueOwner(root));
                mutex.WaitOne();
                held.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            _thread.Start();
            held.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }
}
