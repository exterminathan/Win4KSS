using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Win4K.Core.Caching;

/// <summary>How a <see cref="VideoCache.FetchAsync"/> call ended. Cancellation throws instead.</summary>
public enum DownloadOutcome
{
    /// <summary>The clip is on disk (downloaded now, or already there).</summary>
    Done,
    /// <summary>Transient failures used up every retry. Worth trying again later.</summary>
    Failed,
    /// <summary>The origin refused the file (4xx, an HTML page). Remembered for the session.</summary>
    Rejected,
    /// <summary>No room: the budget (after evicting every unpinned clip) or the 1 GB free-space floor.</summary>
    NoSpace,
    /// <summary>Another process holds this clip's partial file, so it owns the download.</summary>
    Locked,
}

/// <summary>Bytes of one download so far. <see cref="TotalBytes"/> is null when the origin sent no length.</summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Min(1.0, (double)BytesReceived / TotalBytes.Value) : null;
}

/// <summary>
/// Disk usage split the way the budget rule sees it: <c>PinnedBytes + RollingBytes ≤ BudgetBytes</c>.
/// Pinned (kept-offline) files are never evicted; rolling files are evicted least recently
/// played first.
/// </summary>
public sealed record CacheStats(
    long PinnedBytes,
    int PinnedFiles,
    long RollingBytes,
    int RollingFiles,
    long BudgetBytes,
    long FreeDiskBytes)
{
    public long UsedBytes => PinnedBytes + RollingBytes;

    /// <summary>What the rolling cache may use: the budget left over after the pins.</summary>
    public long RollingAllowanceBytes => Math.Max(0, BudgetBytes - PinnedBytes);
}

/// <summary>
/// Downloads clips to disk so they play locally after the first viewing, within a size budget.
///
/// Layout under the cache directory:
///   videos\   completed files, safe to play
///   partial\  in-flight downloads, never played from; kept between sessions so a Range
///             request can resume them, and only removed once 7 days stale
///   cache-index.json
///   queue.json  persisted pin downloads (<see cref="DownloadQueue"/>)
///
/// Three things here are deliberate departures from cdima/aerial:
///
///  * Keys are content-addressed. It keyed on Path.GetFileName(url), so two sources with the
///    same file name collided and quietly served each other's video.
///  * There is a budget with LRU eviction. It had only a 1 GB free-space floor, which against
///    Apple's ~40 GB 4K catalogue means "fill the disk".
///  * Downloads resume. At ~346 MB per clip, restarting from zero on every interruption is
///    the difference between a cache that fills and one that never does.
///
/// Multi-process safety. The clip browser and the saver can run at once against one cache:
///
///  * Every read-modify-write of cache-index.json reloads it from disk inside the named mutex
///    <see cref="CacheMutexNames.CacheIndex"/>, so neither process overwrites the other's entries.
///  * A partial file is held open with FileShare.None for the whole attempt. Finding one
///    locked means another process is downloading that clip; we return
///    <see cref="DownloadOutcome.Locked"/> rather than fight over it.
///  * A FileSystemWatcher on cache-index.json reloads it when the other process writes it
///    and raises <see cref="Changed"/>. Our own writes are recognised by content and ignored.
///
/// Pins. A file is pinned when its <see cref="CacheEntry.ClipKey"/> is in
/// <see cref="PinnedKeys"/> (normally <see cref="AppSettings.KeptOffline"/>); when a pinned clip
/// has more than one rendition on disk (mid quality change) only the newest is protected.
/// </summary>
public sealed class VideoCache : IDisposable
{
    /// <summary>
    /// How long to wait after playback starts before beginning a background download.
    /// Downloading immediately competes for bandwidth with the clip currently streaming,
    /// which is the one thing the user can actually see.
    /// </summary>
    public static readonly TimeSpan DownloadDelay = TimeSpan.FromSeconds(10);

    /// <summary>Partials untouched for this long are orphans and are deleted on startup.</summary>
    public static readonly TimeSpan PartialMaxAge = TimeSpan.FromDays(7);

    /// <summary>Upper bound on simultaneous downloads in one process; <see cref="DownloadQueue"/> runs this many.</summary>
    public const int MaxConcurrentDownloads = 2;

    /// <summary>Settle time for change notifications on cache-index.json.</summary>
    public static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>Never fill the volume: keep this much clear regardless of the configured budget.</summary>
    private const long FreeSpaceFloor = 1L * 1024 * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _downloadSlot = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly Lock _indexGate = new();
    private readonly CrossProcessLock _lock;
    private readonly DebouncedFileWatcher? _watcher;

    /// <summary>
    /// Keys the origin has refused outright (a 404, an HTML error page). Background and
    /// download-ahead requests skip these for the life of the process rather than re-fetching
    /// on every request; an explicit fetch still tries.
    /// </summary>
    private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Published snapshot. Replaced wholesale, never mutated after publication.</summary>
    private CacheIndex _index = new();
    private string _fingerprint = "";

    private int _activeDownloads;
    private long _reservedBytes;
    private volatile bool _disposed;

    /// <param name="settings">Budget, directory and (by default) the pinned keys. Read live, not copied.</param>
    /// <param name="http">Defaults to the shared AppleTrust client.</param>
    /// <param name="time">Clock for LRU stamps and the 7-day partial cleanup; tests inject one.</param>
    /// <param name="watchForChanges">Watch cache-index.json for writes by another process.</param>
    public VideoCache(AppSettings settings, HttpClient? http = null, TimeProvider? time = null, bool watchForChanges = true)
    {
        _settings = settings;
        _http = http ?? Http.Client;
        _time = time ?? TimeProvider.System;
        PinnedKeys = () => _settings.KeptOffline;

        // Fixed for this instance's life: the mutex and watcher are tied to it.
        Root = settings.ResolvedCacheDirectory;

        Directory.CreateDirectory(VideosDirectory);
        Directory.CreateDirectory(PartialDirectory);

        _lock = new CrossProcessLock(CacheMutexNames.CacheIndex(Root));

        CleanOrphanPartials();
        Reconcile();

        if (watchForChanges)
            _watcher = new DebouncedFileWatcher(Root, CacheIndex.FileName, ChangeDebounce, ReloadFromDisk);
    }

    public string Root { get; }
    public string VideosDirectory => Path.Combine(Root, "videos");
    public string PartialDirectory => Path.Combine(Root, "partial");

    public bool Enabled => _settings.CacheEnabled;

    /// <summary>Number of downloads currently running in this process.</summary>
    public int ActiveDownloads => Volatile.Read(ref _activeDownloads);

    /// <summary>
    /// The clip keys whose files must never be evicted. Defaults to the settings object's
    /// <see cref="AppSettings.KeptOffline"/>; the saver points it at
    /// <see cref="PinnedKeySource.FromSettingsFile"/> so pins made in the browser while it
    /// runs are honoured too.
    /// </summary>
    public Func<IReadOnlySet<string>> PinnedKeys { get; set; }

    /// <summary>
    /// Raised after the index changes — a download stored, a file evicted or purged, or a
    /// write by another process picked up by the watcher.
    ///
    /// Always raised on a background (thread-pool or download) thread, never the UI thread;
    /// UI consumers must marshal (Control.BeginInvoke). Handlers should be quick; exceptions
    /// they throw are logged and swallowed.
    /// </summary>
    public event EventHandler? Changed;

    // ---- download policy (settable so tests run without real waits) ----------------------

    /// <summary>Backoff between attempts: one retry per entry, so 1 + Count attempts in all.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15)];

    /// <summary>Upper bound on a server's Retry-After, so one header cannot park a download for hours.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for response headers before calling the attempt timed out.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the body may go without delivering a byte before the attempt is abandoned.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait between attempts. Replaced in tests to record delays instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    // ---- keys and lookup ----------------------------------------------------------------

    /// <summary>
    /// Content-addressed cache key: readable stem plus a hash of the full URL, so two files
    /// that happen to share a name cannot collide.
    /// </summary>
    public static string KeyFor(Uri url)
    {
        var stem = Path.GetFileNameWithoutExtension(url.AbsolutePath);
        var extension = Path.GetExtension(url.AbsolutePath);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".mov";

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))).ToLowerInvariant()[..8];

        // Keep the stem short enough that the whole path stays well inside MAX_PATH even
        // under a deep user profile.
        if (stem.Length > 80)
            stem = stem[..80];

        foreach (var invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '_');

        return $"{stem}.{hash}{extension}";
    }

    public string PathFor(Uri url) => Path.Combine(VideosDirectory, KeyFor(url));

    public string PartialPathFor(Uri url) => Path.Combine(PartialDirectory, KeyFor(url));

    public bool IsCached(Uri url) => File.Exists(PathFor(url));

    /// <summary>Size of the cached file for this URL, or null when it is not cached.</summary>
    public long? CachedSize(Uri url)
    {
        try
        {
            var info = new FileInfo(PathFor(url));
            return info.Exists ? info.Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The URL to hand to the player: a local file when cached, otherwise the origin so it
    /// streams. Wired into <see cref="Playback.PlaylistEngine.ResolveUrl"/>.
    /// </summary>
    public string Resolve(Clip clip) => Resolve(clip, recordPlay: true);

    /// <inheritdoc cref="Resolve(Clip)"/>
    /// <param name="clip">The rendition to resolve.</param>
    /// <param name="recordPlay">
    /// Stamp the entry's <see cref="CacheEntry.LastPlayedUtc"/> (LRU order, and the clip
    /// browser's "last played" sort). False for the browser's preview panel: looking at a clip
    /// is not playing it.
    /// </param>
    public string Resolve(Clip clip, bool recordPlay)
    {
        if (clip.IsLocal)
            return clip.Url.LocalPath;

        var path = PathFor(clip.Url);
        if (File.Exists(path))
        {
            if (recordPlay)
                Touch(clip);
            return path;
        }

        return clip.PlayableUrl;
    }

    /// <summary>Stamps LRU time, and fills in the clip key for files cached before v2.</summary>
    private void Touch(Clip clip)
    {
        var key = KeyFor(clip.Url);
        var now = UtcNow;
        try
        {
            MutateIndex(index =>
            {
                if (!index.Entries.TryGetValue(key, out var entry))
                    return false;

                entry.LastPlayedUtc = now;
                if (entry.ClipKey is null && !string.IsNullOrWhiteSpace(clip.Id))
                    entry.ClipKey = clip.Id;
                if (string.IsNullOrEmpty(entry.Url))
                    entry.Url = clip.Url.AbsoluteUri;
                return true;
            });
        }
        catch (Exception ex)
        {
            // LRU order is a nicety; playback must never fail over it.
            Log.Warn($"Cache: could not record play of {clip.Title}: {ex.Message}");
        }
    }

    internal bool IsRejected(Uri url)
    {
        lock (_indexGate)
            return _rejected.Contains(KeyFor(url));
    }

    // ---- downloading ----------------------------------------------------------------------

    private enum AttemptResult
    {
        Done,
        /// <summary>Transient: a 5xx, a timeout, a dropped connection, a short body.</summary>
        Retry,
        /// <summary>The origin refused this file (4xx, an HTML page). Retrying will not help.</summary>
        Rejected,
        /// <summary>Not the origin's fault (no disk space); give up without remembering it.</summary>
        NoSpace,
        /// <summary>Another process holds the partial file.</summary>
        Locked,
    }

    /// <summary>
    /// <see cref="FetchAsync"/> reduced to "is it on disk now". Kept for callers that only
    /// need that; <see cref="DownloadQueue"/> uses the full outcome.
    /// </summary>
    public async Task<bool> DownloadAsync(Clip clip, CancellationToken ct = default, IProgress<double>? progress = null) =>
        await FetchAsync(clip, ct, progress is null ? null : new FractionProgress(progress)).ConfigureAwait(false)
        == DownloadOutcome.Done;

    /// <summary>
    /// Downloads one clip, resuming a partial file if there is one — including one left by
    /// an earlier session or by the other process. At most <see cref="MaxConcurrentDownloads"/>
    /// run at once in a process.
    ///
    /// Transient failures (5xx, 408, 429, timeouts, dropped connections) are retried with the
    /// <see cref="RetryDelays"/> backoff, honouring Retry-After; the partial file is kept, so
    /// each retry resumes with a Range request. HTML responses and bodies that do not match
    /// their Content-Length are never cached — that is how error pages used to end up being
    /// played as video.
    /// </summary>
    /// <param name="progress">Reported on the download thread, once per buffer (1 MB).</param>
    /// <param name="budgetAllowance">
    /// Bytes this download may go over budget by. A pinned clip re-downloading at a new quality
    /// passes the size of the rendition it will replace, which stays protected until the new
    /// one lands.
    /// </param>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled. The partial file stays.</exception>
    public async Task<DownloadOutcome> FetchAsync(
        Clip clip, CancellationToken ct = default, IProgress<DownloadProgress>? progress = null, long budgetAllowance = 0)
    {
        if (clip.IsLocal)
            return DownloadOutcome.Done;

        var key = KeyFor(clip.Url);
        var finalPath = Path.Combine(VideosDirectory, key);
        if (File.Exists(finalPath))
            return DownloadOutcome.Done;

        await _downloadSlot.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _activeDownloads);

        try
        {
            var attempts = RetryDelays.Count + 1;
            for (var attempt = 1; ; attempt++)
            {
                // The other process may have finished it while we waited for a slot or a retry.
                if (File.Exists(finalPath))
                    return DownloadOutcome.Done;

                var (result, retryAfter) = await AttemptAsync(clip, key, finalPath, attempt, attempts, ct, progress, budgetAllowance)
                                               .ConfigureAwait(false);
                switch (result)
                {
                    case AttemptResult.Done:
                        return DownloadOutcome.Done;

                    case AttemptResult.Rejected:
                        lock (_indexGate)
                            _rejected.Add(key);
                        return DownloadOutcome.Rejected;

                    case AttemptResult.NoSpace:
                        return DownloadOutcome.NoSpace;

                    case AttemptResult.Locked:
                        return DownloadOutcome.Locked;
                }

                if (attempt >= attempts)
                {
                    Log.Warn($"Cache: giving up on {clip.Title} after {attempts} attempts");
                    return DownloadOutcome.Failed;
                }

                var wait = RetryDelays[attempt - 1];
                if (retryAfter is { } asked && asked > wait)
                    wait = asked < MaxRetryAfter ? asked : MaxRetryAfter;

                Log.Info($"Cache: retrying {clip.Title} in {wait.TotalSeconds:0.#}s");
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
            _downloadSlot.Release();
        }
    }

    private async Task<(AttemptResult Result, TimeSpan? RetryAfter)> AttemptAsync(
        Clip clip, string key, string finalPath, int attempt, int attempts,
        CancellationToken ct, IProgress<DownloadProgress>? progress, long allowance)
    {
        var tag = $"Cache: GET {clip.Title} [{attempt}/{attempts}]";
        var partialPath = Path.Combine(PartialDirectory, key);

        // Take the partial exclusively before touching the network. If another process has
        // it, that process owns this download; resuming into the same file from two places
        // would interleave two byte streams into one corrupt video.
        FileStream partial;
        try
        {
            partial = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                                     BufferSize, useAsync: true);
        }
        catch (IOException ex) when (FileErrors.IsSharingViolation(ex))
        {
            Log.Info($"Cache: {clip.Title} is being downloaded elsewhere (partial file locked); skipping");
            return (AttemptResult.Locked, null);
        }

        var reservation = new Reservation();
        Transfer transfer;
        try
        {
            try
            {
                transfer = await TransferAsync(clip, tag, partial, ct, progress, allowance, reservation).ConfigureAwait(false);
            }
            catch
            {
                // Cancelled (or worse): keep a partial worth resuming, but not an empty file
                // we created a moment ago.
                await partial.DisposeAsync().ConfigureAwait(false);
                if (LengthOf(partialPath) == 0)
                    TryDelete(partialPath);
                throw;
            }
            finally
            {
                await partial.DisposeAsync().ConfigureAwait(false);
            }

            if (transfer.Result != AttemptResult.Done)
            {
                if (transfer.Discard || LengthOf(partialPath) == 0)
                    TryDelete(partialPath);
                return (transfer.Result, transfer.RetryAfter);
            }

            // Move into place only once complete, so a half-file can never be treated as a
            // cache hit. This is the one piece of cdima/aerial's design worth keeping intact.
            try
            {
                File.Move(partialPath, finalPath, overwrite: true);
            }
            catch (IOException ex)
            {
                Log.Warn($"Cache: could not move {clip.Title} into place: {ex.Message}");
                return (AttemptResult.Retry, null);
            }

            Store(clip, key, finalPath);
            return (AttemptResult.Done, null);
        }
        finally
        {
            if (reservation.Bytes > 0)
                Interlocked.Add(ref _reservedBytes, -reservation.Bytes);
        }
    }

    private sealed class Reservation
    {
        public long Bytes;
    }

    private readonly record struct Transfer(AttemptResult Result, TimeSpan? RetryAfter = null, bool Discard = false);

    private async Task<Transfer> TransferAsync(
        Clip clip, string tag, FileStream partial, CancellationToken ct,
        IProgress<DownloadProgress>? progress, long allowance, Reservation reservation)
    {
        var existing = partial.Length;
        partial.Seek(0, SeekOrigin.End);

        using var request = new HttpRequestMessage(HttpMethod.Get, clip.Url);
        if (existing > 0)
        {
            // Resume. sylvan supports range requests (206), which matters a great deal
            // when a single clip is ~346 MB.
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        // One token covers the whole attempt: first as the wait for headers, then re-armed
        // before every read as a stall detector. The shared HttpClient has no timeout of its
        // own (bodies are hundreds of MB), so without this a dead connection hangs forever.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(ResponseTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                                  .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"{tag} -> no response within {ResponseTimeout.TotalSeconds:0}s");
            return new Transfer(AttemptResult.Retry);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"{tag} -> {ex.Message}");
            return new Transfer(AttemptResult.Retry);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var length = response.Content.Headers.ContentLength;

            Log.Info($"{tag} -> {status} {response.StatusCode}" +
                     (existing > 0 ? $", resuming at {existing:N0}" : "") +
                     $", {mediaType ?? "no content-type"}, {(length is { } l ? $"{l:N0} bytes" : "unknown length")}");

            if (status == (int)HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Our partial is no use against what the server now has; start clean.
                return new Transfer(AttemptResult.Retry, Discard: true);
            }

            if (status >= 500 || status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests)
                return new Transfer(AttemptResult.Retry, RetryAfter(response));

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Cache: {clip.Title} refused by origin ({status}); not retrying");
                return new Transfer(AttemptResult.Rejected, Discard: true);
            }

            if (IsHtml(mediaType))
            {
                Log.Warn($"Cache: rejected {clip.Title}: origin sent {mediaType}, not video");
                return new Transfer(AttemptResult.Rejected, Discard: true);
            }

            if (existing > 0)
            {
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // A server that ignores our Range and replies 200 would otherwise append a
                    // whole second copy onto the partial file and produce a corrupt video.
                    Log.Warn($"Cache: server ignored Range for {clip.Title}; restarting download");
                    partial.SetLength(0);
                    existing = 0;
                }
                else if (response.Content.Headers.ContentRange?.From is { } from && from != existing)
                {
                    Log.Warn($"Cache: {clip.Title} resumed at {from:N0}, expected {existing:N0}; restarting");
                    return new Transfer(AttemptResult.Retry, Discard: true);
                }
            }

            var total = (length ?? 0) + existing;
            if (!TryReserve(total, allowance))
            {
                Log.Warn($"Cache: not enough room for {clip.Title} ({total:N0} bytes); skipping");
                return new Transfer(AttemptResult.NoSpace);
            }
            reservation.Bytes = total;

            long received = 0;
            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);

                var buffer = new byte[BufferSize];
                while (true)
                {
                    attemptCts.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, attemptCts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await partial.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    progress?.Report(new DownloadProgress(existing + received, total > 0 ? total : null));
                }

                await partial.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The partial stays, so the next attempt resumes where this one stalled.
                Log.Warn($"{tag} -> stalled after {received:N0} bytes");
                return new Transfer(AttemptResult.Retry);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                Log.Warn($"{tag} -> connection lost after {received:N0} bytes: {ex.Message}");
                return new Transfer(AttemptResult.Retry);
            }

            if (length is { } expected && received != expected)
            {
                // A body that disagrees with its own header is truncated or something else
                // entirely; neither may become a cache hit.
                Log.Warn($"Cache: rejected {clip.Title}: received {received:N0} bytes, Content-Length said {expected:N0}");
                return new Transfer(AttemptResult.Retry, Discard: true);
            }

            return new Transfer(AttemptResult.Done);
        }
    }

    private void Store(Clip clip, string key, string finalPath)
    {
        var size = new FileInfo(finalPath).Length;
        var now = UtcNow;

        MutateIndex(index =>
        {
            index.Entries[key] = new CacheEntry
            {
                Key = key,
                Url = clip.Url.AbsoluteUri,
                ClipKey = string.IsNullOrWhiteSpace(clip.Id) ? null : clip.Id,
                Title = clip.Title,
                SizeBytes = size,
                CachedUtc = now,
                LastPlayedUtc = now,
            };
            return true;
        });

        Log.Info($"Cache: stored {clip.Title} ({size:N0} bytes)");

        EvictToBudget();
        RaiseChanged();
    }

    private static bool IsHtml(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Locked by another download of the same clip; that one will finish or fail it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes a clip's partial file, unless another process is writing it. Used when a
    /// download is cancelled because the user no longer wants the clip.
    /// </summary>
    public void DiscardPartial(Uri url) => TryDelete(PartialPathFor(url));

    // ---- budget and eviction ---------------------------------------------------------------

    public long CurrentSizeBytes()
    {
        try
        {
            if (!Directory.Exists(VideosDirectory))
                return 0;

            return new DirectoryInfo(VideosDirectory).EnumerateFiles().Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public long FreeSpaceBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Makes room for a download of <paramref name="bytes"/> and reserves it, so two
    /// concurrent downloads cannot each see the same free space.
    /// </summary>
    private bool TryReserve(long bytes, long allowance)
    {
        if (bytes <= 0)
            return true;

        var others = Interlocked.Read(ref _reservedBytes);
        if (FreeSpaceBytes() - others - bytes < FreeSpaceFloor)
            return false;

        EvictToBudget(headroom: bytes + others);
        if (CurrentSizeBytes() + others + bytes > _settings.CacheBudgetBytes + Math.Max(0, allowance))
            return false;

        Interlocked.Add(ref _reservedBytes, bytes);
        return true;
    }

    /// <summary>
    /// File keys that must survive eviction: for each pinned clip, its newest rendition.
    /// During a quality change that is the old file until the new one lands, then the new one.
    /// </summary>
    private HashSet<string> ProtectedFiles(CacheIndex index)
    {
        IReadOnlySet<string> pinned;
        try
        {
            pinned = PinnedKeys();
        }
        catch (Exception ex)
        {
            Log.Warn($"Cache: could not read pinned clips: {ex.Message}");
            pinned = new HashSet<string>();
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pinned.Count == 0)
            return keep;

        foreach (var group in index.Entries.Values
                                   .Where(e => e.ClipKey is not null && pinned.Contains(e.ClipKey))
                                   .GroupBy(e => e.ClipKey!, StringComparer.Ordinal))
        {
            keep.Add(group.MaxBy(e => e.CachedUtc)!.Key);
        }
        return keep;
    }

    /// <summary>
    /// Drops least-recently-played clips until the cache fits its budget. Pinned files are
    /// never dropped: <c>pinnedBytes + rollingBytes ≤ budget</c> is met by evicting rolling
    /// files only, so if the pins alone exceed the budget the rolling cache is emptied and the
    /// pins stay.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    public int EvictToBudget(long headroom = 0)
    {
        var budget = _settings.CacheBudgetBytes;
        if (CurrentSizeBytes() + headroom <= budget)
            return 0;

        var removed = new List<string>();
        MutateIndex(index =>
        {
            var size = CurrentSizeBytes();
            var keep = ProtectedFiles(index);
            var candidates = index.Entries.Values
                                  .Where(e => !keep.Contains(e.Key))
                                  .OrderBy(e => e.LastPlayedUtc)
                                  .ThenBy(e => e.CachedUtc)
                                  .ToList();

            foreach (var entry in candidates)
            {
                if (size + headroom <= budget)
                    break;

                var path = Path.Combine(VideosDirectory, entry.Key);
                try
                {
                    if (File.Exists(path))
                    {
                        var length = new FileInfo(path).Length;
                        File.Delete(path);
                        size -= length;
                    }

                    index.Entries.Remove(entry.Key);
                    removed.Add(entry.Title);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Almost certainly the file currently being played. Skip it; the next pass
                    // will pick it up once playback has moved on.
                    Log.Warn($"Cache: could not evict {entry.Title} (in use)");
                }
            }

            return removed.Count > 0;
        });

        foreach (var title in removed)
            Log.Info($"Cache: evicted {title}");

        if (removed.Count > 0)
            RaiseChanged();

        return removed.Count;
    }

    /// <summary>
    /// Deletes every rendition of <paramref name="clip"/> except the one at its current URL,
    /// and makes sure that one is tagged with the clip key. Called once a pinned clip's
    /// re-download at a new quality has landed, so the old file is released only then.
    /// A rendition that cannot be deleted (it is playing) loses its clip key instead, which
    /// makes it ordinary rolling cache.
    /// </summary>
    /// <returns>Bytes freed.</returns>
    public long ReleaseOtherRenditions(Clip clip)
    {
        if (clip.IsLocal)
            return 0;

        var key = KeyFor(clip.Url);
        long freed = 0;
        var changed = MutateIndex(index =>
        {
            var dirty = false;
            if (index.Entries.TryGetValue(key, out var current) && current.ClipKey != clip.Id)
            {
                current.ClipKey = clip.Id;
                dirty = true;
            }

            foreach (var other in index.Entries.Values
                                       .Where(e => e.ClipKey == clip.Id &&
                                                   !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                                       .ToList())
            {
                var path = Path.Combine(VideosDirectory, other.Key);
                try
                {
                    if (File.Exists(path))
                    {
                        var length = new FileInfo(path).Length;
                        File.Delete(path);
                        freed += length;
                    }
                    index.Entries.Remove(other.Key);
                    Log.Info($"Cache: released {other.Title} ({other.Key}), superseded by {key}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    other.ClipKey = null;
                    Log.Warn($"Cache: {other.Key} is in use; left as rolling cache");
                }
                dirty = true;
            }

            return dirty;
        });

        if (changed)
            RaiseChanged();
        return freed;
    }

    /// <summary>Bytes held by renditions of this clip other than the one at its current URL.</summary>
    public long OtherRenditionBytes(Clip clip)
    {
        var key = KeyFor(clip.Url);
        lock (_indexGate)
        {
            return _index.Entries.Values
                         .Where(e => e.ClipKey == clip.Id && !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                         .Sum(e => e.SizeBytes);
        }
    }

    /// <summary>Pinned / rolling / free, as the budget rule sees them.</summary>
    public CacheStats Stats()
    {
        CacheIndex snapshot;
        lock (_indexGate)
            snapshot = _index;

        var keep = ProtectedFiles(snapshot);
        long pinned = 0, rolling = 0;
        int pinnedFiles = 0, rollingFiles = 0;
        foreach (var entry in snapshot.Entries.Values)
        {
            if (keep.Contains(entry.Key))
            {
                pinned += entry.SizeBytes;
                pinnedFiles++;
            }
            else
            {
                rolling += entry.SizeBytes;
                rollingFiles++;
            }
        }

        return new CacheStats(pinned, pinnedFiles, rolling, rollingFiles, _settings.CacheBudgetBytes, FreeSpaceBytes());
    }

    /// <summary>Deletes everything, pinned or not, and every partial nobody is writing.</summary>
    public void Purge() => PurgeWhere(_ => true, includePartials: true, "purged");

    /// <summary>Deletes the rolling cache, keeping pinned (kept-offline) clips.</summary>
    /// <returns>Files removed.</returns>
    public int PurgeUnpinned() => PurgeWhere(null, includePartials: false, "purged unpinned clips");

    /// <param name="select">Which entries to delete; null means "every unprotected one".</param>
    private int PurgeWhere(Func<CacheEntry, bool>? select, bool includePartials, string what)
    {
        var removed = 0;
        try
        {
            MutateIndex(index =>
            {
                var keep = select is null ? ProtectedFiles(index) : [];
                var all = select is not null;

                // Files on disk that the index does not know about go too on a full purge.
                var names = all
                    ? Directory.EnumerateFiles(VideosDirectory).Select(Path.GetFileName).OfType<string>()
                               .Concat(index.Entries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : index.Entries.Keys.ToList();

                foreach (var name in names)
                {
                    if (keep.Contains(name))
                        continue;
                    if (select is not null && index.Entries.TryGetValue(name, out var e) && !select(e))
                        continue;

                    try
                    {
                        File.Delete(Path.Combine(VideosDirectory, name));
                        index.Entries.Remove(name);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Cache: could not delete {name} (in use)");
                    }
                }
                return true;
            });

            if (includePartials)
            {
                foreach (var file in Directory.EnumerateFiles(PartialDirectory))
                    TryDelete(file);
            }

            RaiseChanged();
            Log.Info($"Cache: {what} ({removed} files)");
        }
        catch (Exception ex)
        {
            Log.Error("Cache: purge failed", ex);
        }
        return removed;
    }

    /// <summary>
    /// Removes partial files nobody will resume: untouched for <see cref="PartialMaxAge"/>, or
    /// left behind after their clip finished. Recent partials stay, so a download interrupted
    /// by the saver closing resumes next session with a Range request. A partial another
    /// process holds open is never touched.
    /// </summary>
    private void CleanOrphanPartials()
    {
        try
        {
            var cutoff = UtcNow - PartialMaxAge;
            foreach (var file in Directory.EnumerateFiles(PartialDirectory))
            {
                try
                {
                    var info = new FileInfo(file);
                    var finished = File.Exists(Path.Combine(VideosDirectory, info.Name));
                    if (!finished && info.LastWriteTimeUtc >= cutoff)
                        continue;

                    // Opened exclusively with delete-on-close: if another process is writing
                    // it this throws and the file survives; otherwise it is gone on dispose.
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                    }
                    Log.Info($"Cache: removed {(finished ? "leftover" : "stale")} partial {info.Name}");
                }
                catch (IOException)
                {
                    // In use by another process: not an orphan.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cache: could not clean partials: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes the index agree with the filesystem: forget entries whose file has gone, and
    /// adopt files that exist but are not indexed (a cache copied in by hand, say).
    /// </summary>
    private void Reconcile()
    {
        try
        {
            var now = UtcNow;
            MutateIndex(index =>
            {
                // Project to FileInfo first: keying on Path.GetFileName gives a string? key,
                // which ToDictionary will not accept.
                var onDisk = new DirectoryInfo(VideosDirectory)
                             .EnumerateFiles()
                             .ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);

                var dirty = false;
                foreach (var key in index.Entries.Keys.ToList())
                {
                    if (!onDisk.ContainsKey(key))
                    {
                        index.Entries.Remove(key);
                        dirty = true;
                    }
                }

                foreach (var (name, length) in onDisk)
                {
                    if (index.Entries.ContainsKey(name))
                        continue;

                    index.Entries[name] = new CacheEntry
                    {
                        Key = name,
                        Title = name,
                        SizeBytes = length,
                        CachedUtc = now,
                        LastPlayedUtc = now,
                    };
                    dirty = true;
                }

                return dirty;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Cache: reconcile failed", ex);
        }
    }

    // ---- the index, across processes -------------------------------------------------------

    /// <summary>
    /// The only way the index is written: inside the cross-process mutex, reload the file
    /// (the other process may have written it), apply <paramref name="change"/>, save if it
    /// reports a change, and publish the result as this instance's snapshot. Synchronous by
    /// design — the mutex must be released by the thread that took it.
    /// </summary>
    private bool MutateIndex(Func<CacheIndex, bool> change)
    {
        using (_lock.Acquire())
        {
            var (index, text) = CacheIndex.LoadWithText(Root);
            var changed = change(index);
            if (changed && index.SaveText(Root) is { } written)
                text = written;

            lock (_indexGate)
            {
                _index = index;
                _fingerprint = FileErrors.Fingerprint(text);
            }
            return changed;
        }
    }

    /// <summary>
    /// Re-reads the index after a write by another process. Idempotent: if the file is what
    /// this instance last read or wrote (our own save echoing back through the watcher),
    /// nothing happens and <see cref="Changed"/> is not raised.
    /// </summary>
    public void Refresh() => ReloadFromDisk();

    private void ReloadFromDisk()
    {
        if (_disposed)
            return;

        CacheIndex index;
        string text;
        using (_lock.Acquire())
            (index, text) = CacheIndex.LoadWithText(Root);

        var fingerprint = FileErrors.Fingerprint(text);
        lock (_indexGate)
        {
            if (fingerprint == _fingerprint)
                return;
            _index = index;
            _fingerprint = fingerprint;
        }

        RaiseChanged();
    }

    /// <summary>Takes the index mutex for a synchronous critical section (queue.json shares it).</summary>
    internal CrossProcessLock.Releaser AcquireLock() => _lock.Acquire();

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("Cache: a Changed handler threw", ex);
            }
        }
    }

    public IReadOnlyList<CacheEntry> Entries()
    {
        lock (_indexGate)
            return _index.Entries.Values.OrderByDescending(e => e.LastPlayedUtc).Select(e => e.Clone()).ToList();
    }

    /// <summary>Every cached rendition of one clip.</summary>
    public IReadOnlyList<CacheEntry> EntriesFor(string clipKey)
    {
        lock (_indexGate)
            return _index.Entries.Values.Where(e => e.ClipKey == clipKey).Select(e => e.Clone()).ToList();
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Stops watching for the other process's writes. The mutex handle is deliberately left
    /// to finalisation: a download unwinding after dispose may still need it to store its file.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
    }

    /// <summary>Formats a byte count the way Explorer does. Used by the settings UI.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private sealed class FractionProgress(IProgress<double> inner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            if (value.Fraction is { } fraction)
                inner.Report(fraction);
        }
    }
}
