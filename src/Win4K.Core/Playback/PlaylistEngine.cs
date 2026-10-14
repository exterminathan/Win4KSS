namespace Win4K.Core.Playback;

/// <summary>
/// Decides what plays next and keeps mpv's internal playlist topped up ahead of the current
/// clip, which is what makes transitions seamless.
///
/// cdima/aerial polled the player's state at 1 Hz and only set the next URL once it saw the
/// previous clip had stopped, which is where its ~1 second black gap between clips came from.
/// Here mpv is told about upcoming files while the current one is still playing, so it opens,
/// demuxes and decodes ahead (prefetch-playlist) and cuts straight over.
///
/// Reliability rules, each of which exists because its absence blacked out the saver:
///  * Entries are appended with append-play and <see cref="QueueAhead"/> are kept queued.
///    With one entry queued and plain "append", an entry that failed instantly (~1 ms) left
///    mpv idle before the next was appended, and append does not restart an idle player.
///  * End-file events are matched to clips by playlist entry id, never by counting, so the
///    engine's idea of "current" cannot drift from mpv's.
///  * Idle while running is treated as a stall and recovers immediately (then with backoff).
///  * A watchdog skips a clip that makes no progress for <see cref="WatchdogTimeout"/>.
///  * A failed clip is retried once after <see cref="RetryDelay"/>; a second failure benches
///    it for the session. <see cref="ConsecutiveFailureLimit"/> failures in a row lean on clips
///    already on disk, but only for a cooldown (<see cref="CachedOnlyCooldown"/>, doubling up to
///    <see cref="MaxCachedOnlyCooldown"/>): every clip in rotation stays a candidate, and the
///    cache is a convenience. Meanwhile download-ahead keeps fetching uncached rotation clips,
///    which join the on-disk pool as they land.
/// </summary>
public sealed class PlaylistEngine : IDisposable
{
    /// <summary>Entries kept queued in mpv behind the current one.</summary>
    public const int QueueAhead = 2;

    /// <summary>Consecutive failures before the session falls back to cached clips only.</summary>
    public const int ConsecutiveFailureLimit = 3;

    /// <summary>How many upcoming clips are offered to <see cref="DownloadAhead"/>.</summary>
    public const int DownloadAheadDepth = 2;

    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxStallBackoff = TimeSpan.FromSeconds(30);

    /// <summary>How long the first fallback to on-disk clips lasts before the network is tried again.</summary>
    public static readonly TimeSpan CachedOnlyCooldown = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxCachedOnlyCooldown = TimeSpan.FromMinutes(15);

    private readonly IMpvPlayer _player;
    private readonly TimeProvider _time;
    private readonly ITimer? _ticker;
    private readonly Lock _gate = new();

    // ---- rotation ----
    private List<Clip> _all = [];
    private List<Clip> _order = [];
    private int _cursor;

    // ---- what mpv holds, oldest first; the head is the current clip once it has started ----
    private readonly List<Entry> _queued = [];
    private Entry? _current;
    private bool _awaitingStart;

    // ---- failure policy (per session) ----
    private readonly List<(Clip Clip, DateTimeOffset Due)> _retries = [];
    private readonly Dictionary<string, int> _sessionFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bad = new(StringComparer.Ordinal);
    private int _consecutiveFailures;
    private readonly HashSet<string> _reportedUnhealthy = new(StringComparer.Ordinal);
    private bool _cachedOnly;
    private DateTimeOffset? _cachedOnlyUntil;
    private TimeSpan _cachedOnlyCooldown = CachedOnlyCooldown;
    private bool _noOfflineFallbackLogged;

    // ---- stall recovery ----
    private DateTimeOffset _lastProgress;
    private double? _lastPlaybackTime;
    private DateTimeOffset? _restartDue;
    private int _stallRestarts;

    private bool _started;
    private bool _disposed;

    private sealed class Entry(long id, Clip clip, string url)
    {
        public long Id { get; } = id;
        public Clip Clip { get; } = clip;
        public string Url { get; } = url;
        public bool IsStream => IsStreamUrl(Url);
        public bool Loaded { get; set; }
    }

    /// <param name="time">Clock for the watchdog and retry timing; tests pass a fake.</param>
    /// <param name="autoTick">
    /// Run <see cref="Tick"/> on a timer. Tests turn this off and call Tick themselves after
    /// advancing their clock, so nothing ever sleeps for real.
    /// </param>
    public PlaylistEngine(IMpvPlayer player, TimeProvider? time = null, bool autoTick = true)
    {
        _player = player;
        _time = time ?? TimeProvider.System;

        _player.StartFile += OnStartFile;
        _player.FileLoaded += OnFileLoaded;
        _player.EndFile += OnEndFile;
        _player.Idle += OnIdle;

        if (autoTick)
            _ticker = _time.CreateTimer(_ => SafeTick(), null, TickInterval, TickInterval);
    }

    /// <summary>
    /// Maps a clip to the URL actually handed to mpv. The saver points this at the cache so a
    /// cached clip plays from disk; by default clips stream from their origin.
    /// </summary>
    public Func<Clip, string> ResolveUrl { get; set; } = clip => clip.PlayableUrl;

    /// <summary>
    /// Whether a clip can play without the network. Decides the cached-only fallback; the
    /// saver points it at the cache. By default only local-folder clips qualify.
    /// </summary>
    public Func<Clip, bool> IsAvailableOffline { get; set; } = clip => clip.IsLocal;

    /// <summary>Fetches upcoming clips to disk ahead of time. Null disables download-ahead.</summary>
    public IDownloadAhead? DownloadAhead { get; set; }

    /// <summary>Raised when a clip has actually loaded and is about to appear.</summary>
    public event EventHandler<Clip>? ClipStarted;

    /// <summary>
    /// Raised when a clip fails for the second time and is benched for the session. The saver
    /// persists these so repeat offenders surface as unhealthy (see <see cref="ClipHealth"/>).
    /// </summary>
    public event EventHandler<Clip>? ClipFailedForSession;

    /// <summary>The active rotation (everything, or only cached clips after a fallback).</summary>
    public IReadOnlyList<Clip> Order
    {
        get
        {
            lock (_gate)
                return _order.ToList();
        }
    }

    public Clip? Current
    {
        get
        {
            lock (_gate)
                return _current?.Clip;
        }
    }

    public bool IsCachedOnly
    {
        get
        {
            lock (_gate)
                return _cachedOnly;
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
                return _consecutiveFailures;
        }
    }

    public bool IsBenched(Clip clip)
    {
        lock (_gate)
            return _bad.Contains(clip.Id);
    }

    // ---- control ------------------------------------------------------------------------

    public void Start(IReadOnlyList<Clip> clips, AppSettings? settings = null, Random? random = null)
    {
        settings ??= new AppSettings();

        var ordered = BuildOrder(clips, settings, DateTime.Now, random);
        var after = new List<Action>();

        lock (_gate)
        {
            _all = ordered;
            _order = ordered;
            _cursor = 0;
            _queued.Clear();
            _current = null;
            _retries.Clear();
            _sessionFailures.Clear();
            _bad.Clear();
            _reportedUnhealthy.Clear();
            _consecutiveFailures = 0;
            _cachedOnly = false;
            _cachedOnlyUntil = null;
            _cachedOnlyCooldown = CachedOnlyCooldown;
            _noOfflineFallbackLogged = false;
            _restartDue = null;
            _stallRestarts = 0;
            _started = ordered.Count > 0;

            if (!_started)
            {
                Log.Warn("Playlist: nothing to play");
                return;
            }

            Log.Info($"Playlist: {ordered.Count} clips in rotation");
            RestartLocked(after);
        }

        Raise(after);
    }

    /// <summary>Skips to the next clip immediately.</summary>
    public void Next()
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed) return;
            RestartLocked(after);
        }
        Raise(after);
    }

    /// <summary>
    /// Housekeeping: the watchdog, stall-recovery backoff, and retries falling due. Runs every
    /// <see cref="TickInterval"/> on a timer; public so tests can drive it deterministically.
    /// </summary>
    public void Tick()
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed) return;

            var now = _time.GetUtcNow();

            if (_cachedOnly && _cachedOnlyUntil is { } until && now >= until)
                LeaveCachedOnlyLocked();

            if (_restartDue is { } due)
            {
                if (now >= due)
                {
                    Log.Info("Playlist: restarting after stall backoff");
                    RestartLocked(after);
                }
            }
            else
            {
                var position = _player.PlaybackTime;
                if (position is not null && position != _lastPlaybackTime)
                {
                    _lastPlaybackTime = position;
                    _lastProgress = now;
                }
                else if (now - _lastProgress >= WatchdogTimeout)
                {
                    var stuck = _current ?? _queued.FirstOrDefault();
                    Log.Warn($"Playlist: watchdog — no progress for {(now - _lastProgress).TotalSeconds:0}s " +
                             $"on {stuck?.Clip.ToString() ?? "(nothing)"}; skipping");

                    if (stuck is not null)
                        RecordFailureLocked(stuck, "watchdog timeout", after);

                    if (_consecutiveFailures >= ConsecutiveFailureLimit && !_cachedOnly)
                        TrySwitchToCachedOnlyLocked();

                    RestartLocked(after);
                }
                else if (_queued.Count < 1 + QueueAhead && _retries.Any(r => r.Due <= now))
                {
                    TopUpLocked(after);
                }
            }
        }
        Raise(after);
    }

    // ---- mpv events ---------------------------------------------------------------------

    private void OnStartFile(object? sender, StartFileEventArgs e)
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed) return;

            var index = _queued.FindIndex(q => q.Id == e.PlaylistEntryId);
            if (index < 0)
            {
                Log.Info($"Playlist: ignoring start of untracked entry {e.PlaylistEntryId}");
                return;
            }

            if (index > 0)
            {
                // mpv moved past entries without reporting their end. Should not happen, but
                // forgetting them is the only way to stay in step with what it is playing.
                Log.Warn($"Playlist: entry {e.PlaylistEntryId} started ahead of {index} queued entries; dropping them");
                _queued.RemoveRange(0, index);
            }

            _current = _queued[0];
            _awaitingStart = false;
            _lastProgress = _time.GetUtcNow();
            _lastPlaybackTime = null;

            TopUpIfHealthyLocked(after);
        }
        Raise(after);
    }

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed || _current is null || _current.Loaded) return;

            _current.Loaded = true;
            _consecutiveFailures = 0;
            _stallRestarts = 0;
            if (_current.IsStream)
                _cachedOnlyCooldown = CachedOnlyCooldown;
            _lastProgress = _time.GetUtcNow();

            var clip = _current.Clip;
            Log.Info($"Playlist: now playing {clip} from {Describe(_current.Url)}");
            after.Add(() => ClipStarted?.Invoke(this, clip));
            RequestDownloadAheadLocked(after);
        }
        Raise(after);
    }

    private void OnEndFile(object? sender, EndFileEventArgs e)
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed) return;

            var entry = _queued.Find(q => q.Id == e.PlaylistEntryId);
            if (entry is null)
            {
                // Entries dropped by a replace (Next, restart) end with "stopped" long after
                // we stopped tracking them. Anything else is worth a line in the log.
                if (e.Reason is not (MpvEndFileReasonKind.Stopped or MpvEndFileReasonKind.Quit))
                    Log.Info($"Playlist: ignoring end of untracked entry {e.PlaylistEntryId} ({e.Reason})");
                return;
            }

            _queued.Remove(entry);
            if (_current == entry)
                _current = null;

            switch (e.Reason)
            {
                // We asked for these (or mpv expanded a playlist); nothing to recover.
                case MpvEndFileReasonKind.Stopped:
                case MpvEndFileReasonKind.Quit:
                case MpvEndFileReasonKind.Redirect:
                    return;

                case MpvEndFileReasonKind.Eof when entry.Loaded:
                    TopUpIfHealthyLocked(after);
                    return;
            }

            // An error, or an "end of file" from something that never loaded.
            var why = e.Reason == MpvEndFileReasonKind.Error ? $"mpv error {e.Error}" : "ended before loading";
            RecordFailureLocked(entry, why, after);

            if (_consecutiveFailures >= ConsecutiveFailureLimit)
            {
                if (!_cachedOnly && TrySwitchToCachedOnlyLocked())
                    RestartLocked(after);

                // Otherwise do not refill: everything is failing, and appending more would
                // spin through the whole rotation in milliseconds. Let the queue drain; idle
                // recovery takes over with a backoff.
                return;
            }

            TopUpLocked(after);
        }
        Raise(after);
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        var after = new List<Action>();
        lock (_gate)
        {
            if (!_started || _disposed) return;

            // A replace is in flight; this idle predates it (mpv reports idle at startup).
            if (_awaitingStart) return;

            // Recovery already scheduled.
            if (_restartDue is not null) return;

            var delay = _stallRestarts == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(Math.Pow(2, _stallRestarts), MaxStallBackoff.TotalSeconds));
            _stallRestarts++;

            Log.Warn($"Playlist: playlist stalled — mpv went idle with nothing left to play; " +
                     (delay == TimeSpan.Zero ? "restarting now" : $"restarting in {delay.TotalSeconds:0}s"));

            _queued.Clear();
            _current = null;

            if (delay == TimeSpan.Zero)
                RestartLocked(after);
            else
                _restartDue = _time.GetUtcNow() + delay;
        }
        Raise(after);
    }

    // ---- queue management (call with _gate held) ------------------------------------------

    /// <summary>Clears mpv and starts again from the next clip in rotation.</summary>
    private void RestartLocked(List<Action> after)
    {
        _restartDue = null;
        _queued.Clear();
        _current = null;
        _lastProgress = _time.GetUtcNow();
        _lastPlaybackTime = null;

        var first = TakeNextLocked();
        if (first is null)
        {
            Log.Warn("Playlist: nothing playable");
            return;
        }

        // replace clears mpv's playlist, so whatever was queued is gone with it.
        _awaitingStart = true;
        LoadLocked(first, LoadFileMode.Replace);
        TopUpLocked(after);
    }

    private void TopUpIfHealthyLocked(List<Action> after)
    {
        if (_consecutiveFailures < ConsecutiveFailureLimit)
            TopUpLocked(after);
    }

    private void TopUpLocked(List<Action> after)
    {
        while (_queued.Count < 1 + QueueAhead)
        {
            var clip = TakeNextLocked();
            if (clip is null)
                break;

            LoadLocked(clip, LoadFileMode.AppendPlay);
        }

        RequestDownloadAheadLocked(after);
    }

    private void LoadLocked(Clip clip, LoadFileMode mode)
    {
        string url;
        try
        {
            url = ResolveUrl(clip);
        }
        catch (Exception ex)
        {
            Log.Error($"Playlist: could not resolve {clip}; streaming it", ex);
            url = clip.PlayableUrl;
        }

        var id = _player.LoadFile(url, mode);
        var retry = _sessionFailures.ContainsKey(clip.Id) ? " (retry)" : "";
        // The URL itself is logged by the player's "mpv: loadfile" line just above this one.
        Log.Info($"Playlist: {(mode == LoadFileMode.Replace ? "playing" : "queued")} {clip}{retry} " +
                 $"from {Describe(url)} as entry {id}");

        if (id < 0)
            Log.Warn($"Playlist: mpv gave no entry id for {clip}; the watchdog will recover if it stalls");

        _queued.Add(new Entry(id, clip, url));
    }

    /// <summary>Next clip to hand to mpv: a retry that has fallen due, else the rotation.</summary>
    private Clip? TakeNextLocked()
    {
        var now = _time.GetUtcNow();
        for (var i = 0; i < _retries.Count; i++)
        {
            if (_retries[i].Due > now)
                continue;

            var clip = _retries[i].Clip;
            _retries.RemoveAt(i);
            if (!_bad.Contains(clip.Id))
                return clip;
            i--;
        }

        if (_cachedOnly)
            AddNewlyOfflineLocked();

        if (_order.Count == 0)
            return null;

        for (var i = 0; i < _order.Count; i++)
        {
            var clip = _order[_cursor];
            _cursor = (_cursor + 1) % _order.Count;
            if (!_bad.Contains(clip.Id))
                return clip;
        }

        // Every clip has been benched. A black screen is the one outcome that is never right,
        // so give them all another chance; idle backoff stops this becoming a busy loop.
        Log.Warn("Playlist: every clip in rotation has failed this session; trying them again");
        _bad.Clear();
        var fallback = _order[_cursor];
        _cursor = (_cursor + 1) % _order.Count;
        return fallback;
    }

    /// <summary>The clips <see cref="TakeNextLocked"/> would return next, without consuming them.</summary>
    private List<Clip> PeekUpcomingLocked(int count)
    {
        var upcoming = new List<Clip>(count);
        for (var i = 0; i < _order.Count && upcoming.Count < count; i++)
        {
            var clip = _order[(_cursor + i) % _order.Count];
            if (!_bad.Contains(clip.Id) && !upcoming.Contains(clip))
                upcoming.Add(clip);
        }
        return upcoming;
    }

    private void RequestDownloadAheadLocked(List<Action> after)
    {
        var downloader = DownloadAhead;
        if (downloader is null)
            return;

        // Leaning on the cache: the upcoming clips are already on disk, so fetch rotation clips
        // that are not. That is what grows the pool back toward the whole rotation.
        var upcoming = _cachedOnly ? PeekUncachedLocked(DownloadAheadDepth) : PeekUpcomingLocked(DownloadAheadDepth);
        if (upcoming.Count == 0)
            return;

        var streaming = (_current ?? _queued.FirstOrDefault())?.IsStream ?? false;
        after.Add(() => downloader.Request(upcoming, streaming));
    }

    // ---- failure policy -----------------------------------------------------------------

    private void RecordFailureLocked(Entry entry, string why, List<Action> after)
    {
        _consecutiveFailures++;

        var clip = entry.Clip;
        var failures = _sessionFailures[clip.Id] = _sessionFailures.GetValueOrDefault(clip.Id) + 1;

        if (failures == 1)
        {
            _retries.Add((clip, _time.GetUtcNow() + RetryDelay));
            Log.Warn($"Playlist: {clip} failed from {Describe(entry.Url)} ({why}); " +
                     $"will retry once after {RetryDelay.TotalSeconds:0}s. URL: {entry.Url}");
        }
        else if (_bad.Add(clip.Id))
        {
            Log.Warn($"Playlist: {clip} failed again ({why}); skipping it for now. URL: {entry.Url}");

            // Benching is cleared when the network is tried again, so a clip can be benched twice
            // in one session; ClipHealth counts sessions, so report it once.
            if (_reportedUnhealthy.Add(clip.Id))
                after.Add(() => ClipFailedForSession?.Invoke(this, clip));
        }
    }

    /// <summary>Uncached rotation clips after the one on screen, for download-ahead while leaning on the cache.</summary>
    private List<Clip> PeekUncachedLocked(int count)
    {
        var picked = new List<Clip>(count);
        if (_all.Count == 0)
            return picked;

        var currentId = (_current ?? _queued.FirstOrDefault())?.Clip.Id;
        var start = currentId is null ? 0 : _all.FindIndex(c => c.Id == currentId) + 1;
        for (var i = 0; i < _all.Count && picked.Count < count; i++)
        {
            var clip = _all[(start + i) % _all.Count];
            if (!_bad.Contains(clip.Id) && !SafeIsOffline(clip))
                picked.Add(clip);
        }
        return picked;
    }

    /// <summary>While leaning on the cache, rotation clips that have reached disk since join the pool.</summary>
    private void AddNewlyOfflineLocked()
    {
        var inOrder = _order.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var clip in _all)
        {
            if (!inOrder.Contains(clip.Id) && !_bad.Contains(clip.Id) && SafeIsOffline(clip))
            {
                _order.Add(clip);
                Log.Info($"Playlist: {clip} is on disk now; added to the cached rotation ({_order.Count} clips)");
            }
        }
    }

    /// <summary>The cooldown is over: every clip in rotation is a candidate again.</summary>
    private void LeaveCachedOnlyLocked()
    {
        var currentId = (_current ?? _queued.FirstOrDefault())?.Clip.Id;
        var position = currentId is null ? -1 : _all.FindIndex(c => c.Id == currentId);

        _cachedOnly = false;
        _cachedOnlyUntil = null;
        _order = _all;
        _cursor = _all.Count == 0 ? 0 : (position + 1) % _all.Count;
        _consecutiveFailures = 0;
        _noOfflineFallbackLogged = false;

        // The failures that got us here were most likely the network, not the clips.
        _bad.Clear();
        _sessionFailures.Clear();

        Log.Info($"Playlist: trying the network again — back to the full rotation ({_all.Count} clips)");
    }

    private bool TrySwitchToCachedOnlyLocked()
    {
        // Keep the shuffle: continue from where the rotation had got to.
        var rotated = _all.Skip(_cursor).Concat(_all.Take(_cursor));
        var offline = rotated.Where(c => !_bad.Contains(c.Id) && SafeIsOffline(c)).ToList();

        if (offline.Count == 0)
        {
            if (!_noOfflineFallbackLogged)
            {
                Log.Error($"Playlist: {ConsecutiveFailureLimit} consecutive failures and nothing is cached to fall " +
                          "back on; will keep retrying with backoff");
                _noOfflineFallbackLogged = true;
            }
            return false;
        }

        Log.Error($"Playlist: {ConsecutiveFailureLimit} consecutive failures — playing the {offline.Count} clips on disk " +
                  $"for {_cachedOnlyCooldown.TotalMinutes:0} min, then trying the network again");

        _cachedOnly = true;
        _cachedOnlyUntil = _time.GetUtcNow() + _cachedOnlyCooldown;
        _cachedOnlyCooldown = TimeSpan.FromTicks(Math.Min(_cachedOnlyCooldown.Ticks * 2, MaxCachedOnlyCooldown.Ticks));
        _order = offline;
        _cursor = 0;
        _consecutiveFailures = 0;
        _retries.RemoveAll(r => !offline.Contains(r.Clip));
        return true;
    }

    private bool SafeIsOffline(Clip clip)
    {
        try
        {
            return IsAvailableOffline(clip);
        }
        catch
        {
            return false;
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Log.Error("Playlist: tick failed", ex);
        }
    }

    private static void Raise(List<Action> actions)
    {
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // A throwing subscriber must not wedge the playlist.
                Log.Error("Playlist: event handler threw", ex);
            }
        }
    }

    private static bool IsStreamUrl(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string Describe(string url) => IsStreamUrl(url) ? "stream" : "disk";

    /// <summary>
    /// Builds the play order. Pure and deterministic given a seeded <paramref name="random"/>,
    /// so the ordering rules are directly testable.
    /// </summary>
    public static List<Clip> BuildOrder(
        IReadOnlyList<Clip> clips,
        AppSettings settings,
        DateTime now,
        Random? random = null)
    {
        if (clips.Count == 0)
            return [];

        // No selection filter here: the caller passes the rotation already chosen
        // (Rotation.Select), fallbacks included.
        var ordered = clips.ToList();

        if (settings.Shuffle)
        {
            var rng = random ?? Random.Shared;
            // Fisher-Yates. cdima/aerial used OrderBy(_ => rng.Next()) which is a comparison
            // sort over random keys — it works, but it is O(n log n) with duplicate-key bias.
            for (var i = ordered.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
            }
        }

        if (settings.UseTimeOfDay)
        {
            var target = ClipHeuristics.Now(now);

            // Stable OrderBy keeps the shuffle intact inside each group. Clips explicitly
            // marked for the wrong half of the day sink; unmarked clips (110 of Apple's 114)
            // sit in the middle and are effectively untouched.
            ordered = ordered
                .OrderBy(c => c.TimeOfDay == target ? 0 : c.TimeOfDay is TimeOfDay.Day or TimeOfDay.Night ? 2 : 1)
                .ToList();
        }

        return ordered;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _ticker?.Dispose();
        _player.StartFile -= OnStartFile;
        _player.FileLoaded -= OnFileLoaded;
        _player.EndFile -= OnEndFile;
        _player.Idle -= OnIdle;
    }
}
