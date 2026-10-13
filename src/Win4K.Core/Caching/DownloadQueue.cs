using System.Text.Json;
using System.Text.Json.Serialization;
using Win4K.Core.Playback;

namespace Win4K.Core.Caching;

/// <summary>Higher runs first. Within a priority, first come first served.</summary>
public enum DownloadPriority
{
    /// <summary>A clip that just played, cached so the next showing comes from disk.</summary>
    Background = 0,
    /// <summary>The next clips in the saver's playlist (<see cref="IDownloadAhead"/>).</summary>
    DownloadAhead = 1,
    /// <summary>A kept-offline clip. Persisted in queue.json; only the queue owner downloads these.</summary>
    Pin = 2,
}

public enum DownloadState
{
    Queued,
    /// <summary>Deferred: another process holds the partial file. Retried after a delay.</summary>
    Waiting,
    Downloading,
    Done,
    /// <summary>Gave up. A pinned clip stays in queue.json (unless the origin refused it) and is retried next session or on <see cref="DownloadQueue.RetryFailed"/>.</summary>
    Failed,
    Cancelled,
}

/// <summary>An immutable snapshot of one download, as the UI should show it.</summary>
public sealed record DownloadStatus(
    string ClipKey,
    string Title,
    string Url,
    DownloadPriority Priority,
    DownloadState State,
    long BytesReceived,
    long? TotalBytes,
    string? Error)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Min(1.0, (double)BytesReceived / TotalBytes.Value) : null;
}

public sealed class DownloadStatusEventArgs(DownloadStatus status) : EventArgs
{
    public DownloadStatus Status { get; } = status;
}

public sealed class DownloadQueueOptions
{
    /// <summary>Downloads running at once.</summary>
    public int Concurrency { get; init; } = VideoCache.MaxConcurrentDownloads;

    /// <summary>
    /// How many of those slots pins may take. The saver uses 1, so a long pin backlog never
    /// starves the download-ahead that keeps its playlist off the network.
    /// </summary>
    public int PinConcurrency { get; init; } = VideoCache.MaxConcurrentDownloads;

    /// <summary>Try to become the pin-queue owner. False means "never download pins here".</summary>
    public bool DrainPins { get; init; } = true;

    /// <summary>Delay for background downloads, and for download-ahead while the on-screen clip streams.</summary>
    public TimeSpan DownloadDelay { get; init; } = VideoCache.DownloadDelay;

    /// <summary>How often a non-owner checks whether the owner has gone.</summary>
    public TimeSpan OwnershipRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before retrying a clip whose partial another process holds.</summary>
    public TimeSpan LockedRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum gap between <see cref="DownloadQueue.Progress"/> events for one download.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Settle time for change notifications on queue.json.</summary>
    public TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Beyond the first upcoming clip, download-ahead only fetches clips at most this size.</summary>
    public long CheapBytes { get; init; } = 200L * 1024 * 1024;

    public TimeProvider? Time { get; init; }
}

/// <summary>
/// Every download in a process goes through here: user pins, the saver's download-ahead, and
/// background caching of clips that just played. Priority pin &gt; download-ahead &gt;
/// background, <see cref="DownloadQueueOptions.Concurrency"/> (2) at a time, with per-clip
/// progress and cancel. Retries, Range resume and error-page rejection are
/// <see cref="VideoCache.FetchAsync"/>'s. Jobs are keyed by clip key: one download per clip.
///
/// <para><b>Persistence.</b> Pin jobs are written to queue.json beside the cache index, so
/// pins resume after a restart; partial files survive too, so they resume mid-file.
/// Download-ahead and background jobs are ephemeral.</para>
///
/// <para><b>Two processes.</b> The browser and the saver can both run. queue.json is shared
/// state (read-modify-written under the cache-index mutex and watched for the other process's
/// writes), but only one process — the <i>owner</i>, holder of the named mutex
/// <see cref="CacheMutexNames.DownloadQueueOwner"/> — downloads pins. Whoever starts first
/// owns until it exits; a non-owner polls and takes over when the owner goes. In practice the
/// browser is open first and the idle saver only ever does download-ahead and background
/// work; when the saver starts alone it drains pins with one slot, leaving the other to
/// download-ahead. A non-owner still records new pins in queue.json and shows them queued.
/// Even if both tried the same file, the partial's FileShare.None lock makes the loser back
/// off (<see cref="DownloadState.Waiting"/>).</para>
///
/// <para><b>Threading.</b> Public methods are thread-safe. <see cref="JobChanged"/> and
/// <see cref="Progress"/> are raised on whichever thread made the change — a worker thread
/// for progress and completion, the caller's thread for Enqueue and Cancel — and never under
/// a lock. UI consumers must marshal. Handler exceptions are logged and swallowed.</para>
/// </summary>
public sealed class DownloadQueue : IDownloadAhead, IDisposable
{
    public const string FileName = "queue.json";

    private readonly VideoCache _cache;
    private readonly DownloadQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly List<Job> _running = [];
    private readonly HashSet<Task> _tasks = [];
    private readonly CancellationTokenSource _shutdown = new();

    private MutexLease? _lease;
    private ITimer? _ownershipTimer;
    private ITimer? _wakeTimer;
    private DateTimeOffset? _wakeAt;
    private DebouncedFileWatcher? _watcher;
    private long _seq;
    private bool _started;
    private volatile bool _disposed;

    public DownloadQueue(VideoCache cache, DownloadQueueOptions? options = null)
    {
        _cache = cache;
        _options = options ?? new DownloadQueueOptions();
        _time = _options.Time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when a download changes state (queued, waiting, downloading, done, failed,
    /// cancelled) or priority. See the class remarks for threading.
    /// </summary>
    public event EventHandler<DownloadStatusEventArgs>? JobChanged;

    /// <summary>Bytes received, at most once per <see cref="DownloadQueueOptions.ProgressInterval"/> per download.</summary>
    public event EventHandler<DownloadStatusEventArgs>? Progress;

    /// <summary>Raised when this process becomes the pin-queue owner.</summary>
    public event EventHandler? OwnershipChanged;

    /// <summary>True when this process downloads pins (holds the owner mutex).</summary>
    public bool OwnsPins => Volatile.Read(ref _lease) is not null;

    public string QueueFilePath => Path.Combine(_cache.Root, FileName);

    /// <summary>
    /// Starts dispatching: tries to take pin ownership, loads queue.json, and begins watching
    /// it. Jobs enqueued before Start wait for it (which is what lets tests line up a whole
    /// queue before the first one runs).
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
        }

        _watcher = new DebouncedFileWatcher(_cache.Root, FileName, _options.ChangeDebounce, ReloadPins);

        TryTakeOwnership();
        ReloadPins();

        if (!OwnsPins && _options.DrainPins)
        {
            Log.Info("Queue: another process owns the pin queue; this one will download-ahead only until it exits");
            _ownershipTimer = _time.CreateTimer(_ => TryTakeOwnership(), null,
                                                _options.OwnershipRetryInterval, _options.OwnershipRetryInterval);
        }

        Pump();
    }

    // ---- requests ------------------------------------------------------------------------

    /// <summary>
    /// Queues a clip. Idempotent: an already-cached, local, or (below pin priority)
    /// origin-refused clip is ignored, and a clip already queued at the same URL only has its
    /// priority raised. A request at a different URL (a quality change) replaces a job of
    /// equal or lower priority, cancelling it if it is running; its partial is kept.
    /// </summary>
    /// <param name="delay">Earliest start, from now. Null means immediately.</param>
    /// <returns>True if a job was added or changed.</returns>
    public bool Enqueue(Clip clip, DownloadPriority priority, TimeSpan? delay = null)
    {
        if (clip.IsLocal || _disposed || string.IsNullOrWhiteSpace(clip.Id))
            return false;

        if (_cache.IsCached(clip.Url))
        {
            if (priority == DownloadPriority.Pin)
                PinAlreadyCached(clip);
            return false;
        }

        if (priority != DownloadPriority.Pin && _cache.IsRejected(clip.Url))
            return false;

        // File first, then memory: a reload of queue.json landing in between then sees the
        // job in both places, never a memory job missing from the file (which it would drop).
        if (priority == DownloadPriority.Pin)
            WritePin(clip);

        var events = new List<DownloadStatus>();
        var notBefore = _time.GetUtcNow() + (delay ?? TimeSpan.Zero);
        var changed = false;

        lock (_gate)
        {
            if (_jobs.TryGetValue(clip.Id, out var existing))
            {
                if (SameUrl(existing.Clip, clip))
                {
                    if (priority > existing.Priority)
                    {
                        existing.Priority = priority;
                        changed = true;
                    }

                    if (existing.State is DownloadState.Queued or DownloadState.Waiting && notBefore < existing.NotBefore)
                        existing.NotBefore = notBefore;

                    if (existing.State == DownloadState.Failed && priority == DownloadPriority.Pin)
                    {
                        existing.State = DownloadState.Queued;
                        existing.Error = null;
                        existing.NotBefore = notBefore;
                        changed = true;
                    }

                    if (changed)
                        events.Add(existing.Status());
                }
                else if (priority >= existing.Priority)
                {
                    events.AddRange(DisplaceLocked(existing));
                    events.Add(AddLocked(clip, priority, notBefore).Status());
                    changed = true;
                }
            }
            else
            {
                events.Add(AddLocked(clip, priority, notBefore).Status());
                changed = true;
            }
        }

        RaiseAll(JobChanged, events);
        Pump();
        return changed;
    }

    /// <summary>Background-caches a clip that just started playing, after <see cref="DownloadQueueOptions.DownloadDelay"/>.</summary>
    public bool EnqueuePlayed(Clip clip) => Enqueue(clip, DownloadPriority.Background, _options.DownloadDelay);

    /// <summary>
    /// <see cref="IDownloadAhead"/>: the first upcoming clip always, later ones only when
    /// cheap. Queued download-ahead requests for clips the playlist no longer lists are
    /// dropped; one already running is left to finish.
    /// </summary>
    public void Request(IReadOnlyList<Clip> upcoming, bool currentIsStreaming)
    {
        if (!_cache.Enabled || _disposed)
            return;

        // Starting while the on-screen clip streams would compete with it for bandwidth, which
        // is the one thing the viewer can see; the normal background delay applies then.
        var delay = currentIsStreaming ? _options.DownloadDelay : TimeSpan.Zero;

        var wanted = new List<Clip>();
        for (var i = 0; i < upcoming.Count; i++)
        {
            var clip = upcoming[i];
            var cheap = clip.SizeBytes is > 0 && clip.SizeBytes <= _options.CheapBytes;
            if (i > 0 && !cheap)
                break;
            wanted.Add(clip);
        }

        var stale = new List<DownloadStatus>();
        lock (_gate)
        {
            foreach (var job in _jobs.Values.ToList())
            {
                if (job.Priority == DownloadPriority.DownloadAhead &&
                    job.State is DownloadState.Queued or DownloadState.Waiting &&
                    !wanted.Any(c => c.Id == job.Clip.Id))
                {
                    _jobs.Remove(job.Clip.Id);
                    job.State = DownloadState.Cancelled;
                    stale.Add(job.Status());
                }
            }
        }
        RaiseAll(JobChanged, stale);

        foreach (var clip in wanted)
            Enqueue(clip, DownloadPriority.DownloadAhead, delay);
    }

    /// <summary>
    /// Cancels a clip's download, queued or running. A pin is removed from queue.json too
    /// (the caller is responsible for <see cref="AppSettings.KeptOffline"/>; see
    /// <see cref="PinManager.Unpin"/>).
    /// </summary>
    /// <param name="discardPartial">Delete the partial file — right when the clip is no longer wanted.</param>
    /// <returns>False if nothing was queued for the clip.</returns>
    public bool Cancel(string clipKey, bool discardPartial = true)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(clipKey, out job);

        if (job is null)
            return false;

        if (job.Priority == DownloadPriority.Pin)
            RemovePin(job.Clip);

        DownloadStatus? status = null;
        var discardNow = false;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(clipKey, out var current) || !ReferenceEquals(current, job))
                return true; // a reload of queue.json already dropped it

            if (job.State == DownloadState.Downloading)
            {
                job.CancelRequested = true;
                job.DiscardPartial = discardPartial;
                job.Cts?.Cancel();
            }
            else
            {
                _jobs.Remove(clipKey);
                job.State = DownloadState.Cancelled;
                status = job.Status();
                discardNow = discardPartial;
            }
        }

        if (discardNow)
            _cache.DiscardPartial(job.Clip.Url);
        if (status is not null)
            RaiseAll(JobChanged, [status]);
        Pump();
        return true;
    }

    /// <summary>Re-queues every failed job (pins that failed transiently stay listed as Failed).</summary>
    public int RetryFailed()
    {
        var events = new List<DownloadStatus>();
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(j => j.State == DownloadState.Failed))
            {
                job.State = DownloadState.Queued;
                job.Error = null;
                job.NotBefore = _time.GetUtcNow();
                events.Add(job.Status());
            }
        }
        RaiseAll(JobChanged, events);
        Pump();
        return events.Count;
    }

    // ---- queries --------------------------------------------------------------------------

    /// <summary>Every job this process knows of, highest priority first.</summary>
    public IReadOnlyList<DownloadStatus> Snapshot()
    {
        lock (_gate)
        {
            return _jobs.Values.OrderByDescending(j => j.State == DownloadState.Downloading)
                        .ThenByDescending(j => j.Priority).ThenBy(j => j.Seq)
                        .Select(j => j.Status()).ToList();
        }
    }

    public DownloadStatus? Get(string clipKey)
    {
        lock (_gate)
            return _jobs.TryGetValue(clipKey, out var job) ? job.Status() : null;
    }

    /// <summary>Pin jobs not yet done (queued, waiting, downloading or failed).</summary>
    public int PendingPins
    {
        get
        {
            lock (_gate)
                return _jobs.Values.Count(j => j.Priority == DownloadPriority.Pin);
        }
    }

    // ---- dispatch -------------------------------------------------------------------------

    private void Pump()
    {
        var starting = new List<Job>();
        lock (_gate)
        {
            if (!_started || _disposed || _shutdown.IsCancellationRequested)
                return;

            var now = _time.GetUtcNow();
            DateTimeOffset? wake = null;
            var owner = _lease is not null;

            while (_running.Count < _options.Concurrency)
            {
                var runningPins = _running.Count(j => j.Priority == DownloadPriority.Pin);
                Job? best = null;
                foreach (var job in _jobs.Values)
                {
                    if (job.State is not (DownloadState.Queued or DownloadState.Waiting))
                        continue;
                    if (job.Priority == DownloadPriority.Pin && (!owner || runningPins >= _options.PinConcurrency))
                        continue;
                    if (job.NotBefore > now)
                    {
                        if (wake is null || job.NotBefore < wake)
                            wake = job.NotBefore;
                        continue;
                    }
                    if (best is null || job.Priority > best.Priority || (job.Priority == best.Priority && job.Seq < best.Seq))
                        best = job;
                }

                if (best is null)
                    break;

                best.State = DownloadState.Downloading;
                best.Cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _running.Add(best);
                starting.Add(best);
            }

            ScheduleWakeLocked(wake, now);
        }

        foreach (var job in starting)
        {
            RaiseAll(JobChanged, [StatusOf(job)]);
            var task = Task.Run(() => RunAsync(job));
            lock (_gate)
            {
                _tasks.RemoveWhere(t => t.IsCompleted);
                _tasks.Add(task);
            }
        }
    }

    private void ScheduleWakeLocked(DateTimeOffset? wake, DateTimeOffset now)
    {
        if (wake is null || (_wakeAt is { } pending && pending <= wake && pending > now))
            return;

        _wakeAt = wake;
        var due = wake.Value - now;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        _wakeTimer ??= _time.CreateTimer(_ =>
        {
            lock (_gate)
                _wakeAt = null;
            Pump();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // A little slack so the job is due by the time the timer fires.
        _wakeTimer.Change(due + TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
    }

    private async Task RunAsync(Job job)
    {
        DownloadOutcome? outcome = null;
        string? error = null;
        try
        {
            var allowance = job.Priority == DownloadPriority.Pin ? _cache.OtherRenditionBytes(job.Clip) : 0;
            outcome = await _cache.FetchAsync(job.Clip, job.Cts!.Token, new ProgressSink(this, job), allowance)
                                  .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user, by a replacement, or by shutdown; Complete tells them apart.
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"Queue: download of {job.Clip.Title} failed", ex);
        }

        try
        {
            Complete(job, outcome, error);
        }
        catch (Exception ex)
        {
            Log.Error($"Queue: completing {job.Clip.Title} failed", ex);
        }
    }

    private void Complete(Job job, DownloadOutcome? outcome, string? error)
    {
        bool isPin;
        lock (_gate)
            isPin = job.Priority == DownloadPriority.Pin;

        // queue.json first (see Enqueue). The job is still "running" in memory here, which a
        // concurrent reload never touches.
        if (isPin)
        {
            switch (outcome)
            {
                case DownloadOutcome.Done:
                    _cache.ReleaseOtherRenditions(job.Clip);
                    RemovePin(job.Clip);
                    break;
                case DownloadOutcome.Rejected:
                    RemovePin(job.Clip);
                    break;
            }
        }

        DownloadStatus? status = null;
        var discard = false;
        lock (_gate)
        {
            _running.Remove(job);
            job.Cts?.Dispose();
            job.Cts = null;

            var current = _jobs.TryGetValue(job.Clip.Id, out var j) && ReferenceEquals(j, job);
            var now = _time.GetUtcNow();

            switch (outcome)
            {
                case null when error is null:
                    job.State = DownloadState.Cancelled;
                    discard = job.CancelRequested && job.DiscardPartial;
                    if (current && (job.CancelRequested || job.Priority != DownloadPriority.Pin))
                        _jobs.Remove(job.Clip.Id);
                    else if (current)
                        job.State = DownloadState.Queued; // shutdown: a pin stays queued (and in queue.json)
                    break;

                case DownloadOutcome.Done:
                    job.State = DownloadState.Done;
                    if (current)
                        _jobs.Remove(job.Clip.Id);
                    break;

                case DownloadOutcome.Locked:
                    job.State = DownloadState.Waiting;
                    job.Error = "Another process is downloading this clip";
                    job.NotBefore = now + _options.LockedRetryDelay;
                    break;

                default:
                    job.State = DownloadState.Failed;
                    job.Error = error ?? outcome switch
                    {
                        DownloadOutcome.Rejected => "The origin refused this file",
                        DownloadOutcome.NoSpace => "Not enough room in the cache budget or on disk",
                        _ => "Download failed after retries",
                    };
                    // Pins that failed transiently stay listed (and in queue.json) for a retry;
                    // everything else is forgotten.
                    if (current && (!isPin || outcome == DownloadOutcome.Rejected))
                        _jobs.Remove(job.Clip.Id);
                    break;
            }

            status = job.Status();
        }

        if (discard)
            _cache.DiscardPartial(job.Clip.Url);

        if (status is not null && !_disposed)
            RaiseAll(JobChanged, [status]);

        Pump();
    }

    private void PinAlreadyCached(Clip clip)
    {
        _cache.ReleaseOtherRenditions(clip);
        RemovePin(clip, anyUrl: true);

        // A pin job for another rendition (say, a quality change being undone) is moot now.
        Job? other;
        lock (_gate)
            _jobs.TryGetValue(clip.Id, out other);
        if (other is not null && !SameUrl(other.Clip, clip))
            Cancel(clip.Id, discardPartial: true);
    }

    /// <summary>Removes a job being replaced by a request for another URL. Caller holds the gate.</summary>
    private List<DownloadStatus> DisplaceLocked(Job existing)
    {
        _jobs.Remove(existing.Clip.Id);
        if (existing.State == DownloadState.Downloading)
        {
            existing.CancelRequested = true;
            existing.DiscardPartial = false;
            existing.Cts?.Cancel();
            return []; // Complete reports it
        }

        existing.State = DownloadState.Cancelled;
        return [existing.Status()];
    }

    private Job AddLocked(Clip clip, DownloadPriority priority, DateTimeOffset notBefore)
    {
        var job = new Job(clip, priority, notBefore, ++_seq);
        _jobs[clip.Id] = job;
        return job;
    }

    private static bool SameUrl(Clip a, Clip b) =>
        string.Equals(a.Url.AbsoluteUri, b.Url.AbsoluteUri, StringComparison.Ordinal);

    private DownloadStatus StatusOf(Job job)
    {
        lock (_gate)
            return job.Status();
    }

    // ---- ownership ------------------------------------------------------------------------

    private void TryTakeOwnership()
    {
        if (!_options.DrainPins || _disposed || OwnsPins)
            return;

        var lease = MutexLease.TryAcquire(CacheMutexNames.DownloadQueueOwner(_cache.Root));
        if (lease is null)
            return;

        lock (_gate)
        {
            if (_disposed)
            {
                lease.Dispose();
                return;
            }
            _lease = lease;
        }

        _ownershipTimer?.Dispose();
        _ownershipTimer = null;

        Log.Info("Queue: this process now owns the pin queue");
        ReloadPins();
        Pump();

        try
        {
            OwnershipChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Queue: an OwnershipChanged handler threw", ex);
        }
    }

    // ---- queue.json -----------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed class QueueFile
    {
        public int Version { get; set; } = 1;
        public List<PersistedJob> Jobs { get; set; } = [];
    }

    internal sealed class PersistedJob
    {
        public string ClipKey { get; set; } = "";
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Category { get; set; }
        public long? SizeBytes { get; set; }
        public DateTime EnqueuedUtc { get; set; }

        public Clip? ToClip() =>
            Uri.TryCreate(Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(ClipKey)
                ? new Clip(ClipKey, string.IsNullOrWhiteSpace(Title) ? ClipKey : Title, Category ?? "",
                           TimeOfDay.Unknown, uri, IsLocal: false, SizeBytes)
                : null;
    }

    /// <summary>Reads queue.json. Callers hold the cache lock.</summary>
    private QueueFile ReadQueueFile()
    {
        try
        {
            if (SharedFile.Read(QueueFilePath) is not { } text)
                return new QueueFile();
            return JsonSerializer.Deserialize<QueueFile>(text, JsonOptions) ?? new QueueFile();
        }
        catch (Exception ex)
        {
            Log.Error("Queue: queue.json unreadable; starting empty", ex);
            return new QueueFile();
        }
    }

    private void UpdateQueueFile(Func<QueueFile, bool> change)
    {
        try
        {
            using (_cache.AcquireLock())
            {
                var file = ReadQueueFile();
                if (!change(file))
                    return;

                SharedFile.Write(QueueFilePath, JsonSerializer.Serialize(file, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Queue: could not update queue.json", ex);
        }
    }

    private void WritePin(Clip clip)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        UpdateQueueFile(file =>
        {
            var existing = file.Jobs.FirstOrDefault(j => j.ClipKey == clip.Id);
            if (existing is not null && existing.Url == clip.Url.AbsoluteUri)
                return false;

            file.Jobs.RemoveAll(j => j.ClipKey == clip.Id);
            file.Jobs.Add(new PersistedJob
            {
                ClipKey = clip.Id,
                Url = clip.Url.AbsoluteUri,
                Title = clip.Title,
                Category = clip.Category,
                SizeBytes = clip.SizeBytes,
                EnqueuedUtc = existing?.EnqueuedUtc ?? now,
            });
            return true;
        });
    }

    /// <param name="anyUrl">Remove the clip's pin whatever URL it was queued at.</param>
    private void RemovePin(Clip clip, bool anyUrl = false) =>
        UpdateQueueFile(file => file.Jobs.RemoveAll(j =>
            j.ClipKey == clip.Id && (anyUrl || j.Url == clip.Url.AbsoluteUri)) > 0);

    /// <summary>
    /// Merges queue.json into memory: pins added by the other process are queued here; queued
    /// pins the file no longer lists (finished or unpinned elsewhere) are dropped. Running
    /// jobs are never touched. Idempotent, so our own writes echoing back are harmless.
    /// </summary>
    private void ReloadPins()
    {
        if (_disposed)
            return;

        QueueFile file;
        try
        {
            using (_cache.AcquireLock())
                file = ReadQueueFile();
        }
        catch (Exception ex)
        {
            Log.Error("Queue: could not read queue.json", ex);
            return;
        }

        var listed = new Dictionary<string, PersistedJob>(StringComparer.Ordinal);
        foreach (var pj in file.Jobs)
            listed[pj.ClipKey] = pj;

        var events = new List<DownloadStatus>();
        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var pj in listed.Values)
            {
                var clip = pj.ToClip();
                if (clip is null)
                    continue;

                if (_jobs.TryGetValue(pj.ClipKey, out var existing))
                {
                    // Pinned elsewhere while we had it queued for download-ahead: same file, promote it.
                    if (existing.Priority != DownloadPriority.Pin && SameUrl(existing.Clip, clip))
                    {
                        existing.Priority = DownloadPriority.Pin;
                        events.Add(existing.Status());
                    }
                    continue;
                }

                if (_cache.IsCached(clip.Url))
                    continue;

                events.Add(AddLocked(clip, DownloadPriority.Pin, _time.GetUtcNow()).Status());
            }

            foreach (var job in _jobs.Values.ToList())
            {
                if (job.Priority != DownloadPriority.Pin ||
                    job.State is not (DownloadState.Queued or DownloadState.Waiting or DownloadState.Failed))
                    continue;

                if (listed.TryGetValue(job.Clip.Id, out var pj) && pj.Url == job.Clip.Url.AbsoluteUri)
                    continue;

                _jobs.Remove(job.Clip.Id);
                job.State = _cache.IsCached(job.Clip.Url) ? DownloadState.Done : DownloadState.Cancelled;
                events.Add(job.Status());
            }
        }

        RaiseAll(JobChanged, events);
        Pump();
    }

    // ---- shutdown -------------------------------------------------------------------------

    /// <summary>
    /// Cancels running downloads and waits for them to unwind. Partials and queue.json are
    /// kept, so pins resume next time. Dispose afterwards.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
            tasks = [.. _tasks];

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels running downloads (without waiting), stops watching, and releases pin
    /// ownership so the other process can take over.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _watcher?.Dispose();
        _ownershipTimer?.Dispose();
        _wakeTimer?.Dispose();

        MutexLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
        }
        lease?.Dispose();
    }

    // ---- plumbing -------------------------------------------------------------------------

    private void RaiseAll(EventHandler<DownloadStatusEventArgs>? handlers, IReadOnlyList<DownloadStatus> statuses)
    {
        if (handlers is null || statuses.Count == 0)
            return;

        foreach (var status in statuses)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DownloadStatusEventArgs>>())
            {
                try
                {
                    handler(this, new DownloadStatusEventArgs(status));
                }
                catch (Exception ex)
                {
                    Log.Error("Queue: an event handler threw", ex);
                }
            }
        }
    }

    private sealed class ProgressSink(DownloadQueue queue, Job job) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            DownloadStatus status;
            lock (queue._gate)
            {
                job.Received = value.BytesReceived;
                job.Total = value.TotalBytes;

                var now = queue._time.GetUtcNow();
                var last = value.TotalBytes is { } total && value.BytesReceived >= total;
                if (!last && now - job.LastProgress < queue._options.ProgressInterval)
                    return;
                job.LastProgress = now;
                status = job.Status();
            }
            queue.RaiseAll(queue.Progress, [status]);
        }
    }

    private sealed class Job(Clip clip, DownloadPriority priority, DateTimeOffset notBefore, long seq)
    {
        public Clip Clip { get; } = clip;
        public DownloadPriority Priority { get; set; } = priority;
        public DownloadState State { get; set; } = DownloadState.Queued;
        public DateTimeOffset NotBefore { get; set; } = notBefore;
        public long Seq { get; } = seq;
        public CancellationTokenSource? Cts { get; set; }
        public bool CancelRequested { get; set; }
        public bool DiscardPartial { get; set; }
        public long Received { get; set; }
        public long? Total { get; set; } = clip.SizeBytes;
        public string? Error { get; set; }
        public DateTimeOffset LastProgress { get; set; }

        public DownloadStatus Status() =>
            new(Clip.Id, Clip.Title, Clip.Url.AbsoluteUri, Priority, State, Received, Total, Error);
    }
}
