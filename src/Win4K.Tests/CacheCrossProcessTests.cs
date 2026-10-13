using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// The browser and the saver share one cache directory. Two <see cref="VideoCache"/>
/// instances on one root stand in for the two processes: the named mutex, the FileShare.None
/// partials and the index watcher all behave the same across threads as across processes.
/// </summary>
public class CacheCrossProcessTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];

    public CacheCrossProcessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-xp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings() => new() { CacheEnabled = true, CacheDirectory = _root, CacheBudgetGb = 20 };

    private VideoCache Cache(FakeOrigin origin, TimeProvider? time = null)
    {
        var cache = new VideoCache(Settings(), origin.Client(), time)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        _disposables.Add(cache);
        return cache;
    }

    // ---- partials -------------------------------------------------------------------------

    [Fact]
    public async Task A_partial_left_by_one_instance_is_resumed_by_the_next_with_a_range_request()
    {
        var origin = new FakeOrigin();
        var clip = origin.Add("ia:resume", "https://archive.example/resume%20me.mp4", 64 * 1024);
        origin.CutOnce(clip, 20_000);

        // First "session": the connection drops and there are no retries left.
        var first = Cache(origin);
        first.RetryDelays = [];
        Assert.Equal(DownloadOutcome.Failed, await first.FetchAsync(clip));
        Assert.Equal(20_000, new FileInfo(first.PartialPathFor(clip.Url)).Length);
        first.Dispose();

        // Second "session": construction no longer clears partials, so it picks up at 20,000.
        var second = Cache(origin);
        Assert.True(File.Exists(second.PartialPathFor(clip.Url)), "a recent partial must survive startup");
        Assert.Equal(DownloadOutcome.Done, await second.FetchAsync(clip));

        var last = origin.Requests.Last();
        Assert.Equal(20_000, last.RangeFrom);
        Assert.Equal(origin.Body(clip), await File.ReadAllBytesAsync(second.PathFor(clip.Url)));
        Assert.False(File.Exists(second.PartialPathFor(clip.Url)));
    }

    [Fact]
    public async Task A_partial_locked_by_another_instance_is_skipped_without_a_request()
    {
        var origin = new FakeOrigin();
        var clip = origin.Add("ia:locked", "https://archive.example/locked.mp4", 4096);
        var owner = Cache(origin);
        var other = Cache(origin);

        // The owner is mid-download: it holds the partial exclusively.
        await using (new FileStream(owner.PartialPathFor(clip.Url), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.Equal(DownloadOutcome.Locked, await other.FetchAsync(clip));
            Assert.Empty(origin.Requests);
        }

        // Released: now it is fair game, and the empty leftover is simply restarted.
        Assert.Equal(DownloadOutcome.Done, await other.FetchAsync(clip));
    }

    [Fact]
    public async Task A_locked_partial_puts_a_queued_download_into_waiting()
    {
        var origin = new FakeOrigin();
        var clip = origin.Add("ia:wait", "https://archive.example/wait.mp4", 4096);
        var cache = Cache(origin);
        using var queue = new DownloadQueue(cache, new DownloadQueueOptions { DownloadDelay = TimeSpan.Zero, LockedRetryDelay = TimeSpan.FromHours(1) });

        await using var held = new FileStream(cache.PartialPathFor(clip.Url), FileMode.Create, FileAccess.Write, FileShare.None);
        queue.Enqueue(clip, DownloadPriority.DownloadAhead);
        queue.Start();

        await Eventually.True(() => queue.Get(clip.Id)?.State == DownloadState.Waiting, "the job to back off");
        Assert.Empty(origin.Requests);
    }

    [Fact]
    public void Stale_partials_are_removed_after_seven_days_and_recent_or_locked_ones_kept()
    {
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        var partial = Path.Combine(_root, "partial");
        var videos = Path.Combine(_root, "videos");
        Directory.CreateDirectory(partial);
        Directory.CreateDirectory(videos);

        string Make(string name, TimeSpan age)
        {
            var path = Path.Combine(partial, name);
            File.WriteAllBytes(path, new byte[128]);
            File.SetLastWriteTimeUtc(path, now - age);
            return path;
        }

        var stale = Make("stale.aaaa1111.mp4", TimeSpan.FromDays(8));
        var recent = Make("recent.bbbb2222.mp4", TimeSpan.FromDays(6));
        var lockedStale = Make("locked.cccc3333.mp4", TimeSpan.FromDays(30));
        var finished = Make("finished.dddd4444.mp4", TimeSpan.FromMinutes(5));
        File.WriteAllBytes(Path.Combine(videos, "finished.dddd4444.mp4"), new byte[64]);

        using (new FileStream(lockedStale, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            using var cache = new VideoCache(Settings(), new FakeOrigin().Client(), clock);
        }

        Assert.False(File.Exists(stale), "an 8-day-old orphan should be removed");
        Assert.True(File.Exists(recent), "a 6-day-old partial should be kept for Range resume");
        Assert.True(File.Exists(lockedStale), "a partial another process holds is never ours to delete");
        Assert.False(File.Exists(finished), "a partial whose clip finished is a leftover");
    }

    // ---- the index ------------------------------------------------------------------------

    [Fact]
    public async Task Two_instances_writing_the_index_concurrently_lose_no_entries()
    {
        var origin = new FakeOrigin();
        var a = Cache(origin);
        var b = Cache(origin);

        var clips = Enumerable.Range(0, 30)
                              .Select(i => origin.Add($"ia:c{i}", $"https://archive.example/c{i}.mp4", 2048 + i, i))
                              .ToList();

        var work = clips.Select((clip, i) => Task.Run(() => (i % 2 == 0 ? a : b).FetchAsync(clip)));
        var outcomes = await Task.WhenAll(work);

        Assert.All(outcomes, o => Assert.Equal(DownloadOutcome.Done, o));

        // Read the file itself: a lost update would show up as a missing entry here.
        var onDisk = CacheIndex.Load(_root);
        Assert.Equal(30, onDisk.Entries.Count);
        Assert.All(clips, c => Assert.Equal(c.Id, onDisk.Entries[VideoCache.KeyFor(c.Url)].ClipKey));
    }

    [Fact]
    public async Task A_write_by_one_instance_raises_Changed_in_the_other_but_not_in_itself()
    {
        var origin = new FakeOrigin();
        var clip = origin.Add("ia:watch", "https://archive.example/watch.mp4", 4096);
        var writer = Cache(origin);
        var reader = Cache(origin);

        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Changed += (_, _) => seen.TrySetResult();
        var writerChanges = 0;
        writer.Changed += (_, _) => Interlocked.Increment(ref writerChanges);

        Assert.Equal(DownloadOutcome.Done, await writer.FetchAsync(clip));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(reader.Entries(), e => e.ClipKey == clip.Id);

        // The writer's own save echoes back through its watcher; that must not count twice.
        await Task.Delay(VideoCache.ChangeDebounce * 4);
        Assert.Equal(1, Volatile.Read(ref writerChanges));
    }

    [Fact]
    public void A_v1_index_still_loads_with_no_clip_keys_and_escaped_urls()
    {
        var videos = Path.Combine(_root, "videos");
        Directory.CreateDirectory(videos);
        File.WriteAllBytes(Path.Combine(videos, "LA 01.abcd1234.mp4"), new byte[100]);

        // Exactly the shape v1 wrote, including an unescaped URL from before the escaping fix.
        File.WriteAllText(CacheIndex.PathFor(_root), """
            {"version":1,"entries":{"LA 01.abcd1234.mp4":{"key":"LA 01.abcd1234.mp4","url":"https://archive.org/download/x/LA 01.mp4","title":"LA 01","sizeBytes":100,"cachedUtc":"2026-09-01T00:00:00Z","lastPlayedUtc":"2026-09-02T00:00:00Z"}}}
            """);

        using var cache = new VideoCache(Settings(), new FakeOrigin().Client());

        var entry = Assert.Single(cache.Entries());
        Assert.Null(entry.ClipKey);
        Assert.Equal("https://archive.org/download/x/LA%2001.mp4", entry.Url);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), entry.LastPlayedUtc.ToUniversalTime());

        // Resolving the clip adopts it under its key, and the file is written back as v2.
        var clip = new Clip("ia:x/LA 01.mp4", "LA 01", "Test", TimeOfDay.Unknown,
                            new Uri("https://archive.org/download/x/LA%2001.mp4"), false);
        File.Move(Path.Combine(videos, "LA 01.abcd1234.mp4"), cache.PathFor(clip.Url));

        using var again = new VideoCache(Settings(), new FakeOrigin().Client());
        again.Resolve(clip);
        var json = JsonDocument.Parse(File.ReadAllText(CacheIndex.PathFor(_root)));
        Assert.Equal(CacheIndex.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(clip.Id, Assert.Single(again.Entries()).ClipKey);
    }
}
