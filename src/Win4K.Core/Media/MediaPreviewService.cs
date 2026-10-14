using System.Net;
using Win4K.Core.Caching;

namespace Win4K.Core.Media;

/// <summary>Tuning and test seams for <see cref="MediaPreviewService"/>. Every member has a production default.</summary>
public sealed class MediaPreviewOptions
{
    /// <summary>Concurrent generations (each may run one ffmpeg).</summary>
    public int Workers { get; init; } = 2;

    /// <summary>LRU cap on the previews folder, separate from the video cache budget.</summary>
    public long CapBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Background fill pauses once the folder reaches this fraction of <see cref="CapBytes"/>, so it never churns the LRU.</summary>
    public double BackgroundFillCapFraction { get; init; } = 0.9;

    /// <summary>A failed (or placeholder) preview is not attempted again until this long after the failure.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Hard limit on one ffmpeg run; it is killed (process tree) after this.</summary>
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait for response headers on a thumbnail or head-segment request.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whole-body limit for one head-segment download.</summary>
    public TimeSpan HeadDownloadTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Read uncached clips through a Range-fetched head segment (true) or hand ffmpeg the
    /// HTTPS URL directly (false, the default). Measured on archive.org (~1.3 MB/s): the head route took
    /// 22 s and 27 MB for one hover clip; ffmpeg's own input seek took 7.6 s and fetched only
    /// what it decoded. Both verify TLS against our trust (HttpClient/AppleTrust vs. the CA bundle
    /// with tls_verify=1). Tail-indexed files always go direct.
    /// </summary>
    public bool UseHeadSegments { get; init; }

    /// <summary>Head segment fetched when the clip's bitrate is unknown.</summary>
    public long DefaultHeadBytes { get; init; } = 20L * 1024 * 1024;

    public long MinHeadBytes { get; init; } = 4L * 1024 * 1024;
    public long MaxHeadBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxThumbnailBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Delay before index.json is written after a change; writes coalesce.</summary>
    public TimeSpan SaveDebounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The previews folder. Defaults to <see cref="AppPaths.PreviewsDirectory"/>.</summary>
    public string? Directory { get; init; }

    /// <summary>Defaults to the shared AppleTrust client (<see cref="Http.Client"/>).</summary>
    public HttpClient? Http { get; init; }

    public IProcessRunner? Runner { get; init; }
    public TimeProvider? Time { get; init; }

    /// <summary>
    /// Use exactly this ffmpeg, without checking that it exists (a test seam). Normally null:
    /// the path comes from <see cref="AppSettings.FfmpegPath"/> via <see cref="FfmpegLocator"/>.
    /// </summary>
    public string? FfmpegPath { get; init; }

    /// <summary>False disables the search for ffmpeg — "ffmpeg is not installed", for tests.</summary>
    public bool AutoDetectFfmpeg { get; init; } = true;

    /// <summary>PEM bundle for ffmpeg over HTTPS. Defaults to <see cref="CaBundle.Ensure"/>.</summary>
    public Func<string?>? CaFile { get; init; }

    /// <summary>
    /// Hand ffmpeg a 127.0.0.1 URL served by <see cref="LoopbackRelay"/> instead of the remote
    /// one, so ffmpeg itself never goes to the network: the bundled build's TLS cannot verify
    /// Apple's CDN, and a per-app firewall has never been asked about ffmpeg.exe. The clip
    /// browser turns this on; tests leave it off and see the remote URL.
    /// </summary>
    public bool RelayRemoteInputs { get; init; }

    /// <summary>
    /// A local file to read a clip's video from, or null to go to the network. Defaults to the
    /// clip's own path for folder clips, else any rendition already in the video cache (SDR first).
    /// </summary>
    public Func<ClipInfo, string?>? InputFileFor { get; init; }
}

/// <summary>
/// Grid-card previews for the clip browser: a 480 px still and a 4 s hover clip per clip,
/// generated on demand and in the background, kept in their own LRU-capped folder.
///
/// <para><b>Lifetime.</b> Only the browser constructs this; the saver never does, so nothing
/// here can compete with playback. Nothing runs until <see cref="Start"/>. One process should
/// own the folder at a time (the browser is single-window); index.json writes are atomic and
/// mutex-guarded, but two live instances would not merge each other's records.</para>
///
/// <para><b>Threading.</b> Every public member is thread-safe and non-blocking (no network, no
/// ffmpeg) except <see cref="GenerateAsync"/>. <see cref="PreviewUpdated"/> is raised on a
/// background worker thread, never the UI thread: WinForms consumers must marshal
/// (Control.BeginInvoke) before touching WebView2. Handler exceptions are logged and swallowed.</para>
///
/// <para><b>Stills</b>, first route that works: the catalogue thumbnail (fetched through
/// <see cref="Http.Client"/>, re-encoded to 480 px by ffmpeg, stored as-is without it) → a
/// keyframe from the cached file → a keyframe from a ~20 MB head segment fetched with a Range
/// request → <see cref="PreviewStatus.Placeholder"/> (no file; the UI tints a card by
/// <see cref="PreviewState.PlaceholderColor"/>).</para>
///
/// <para><b>Hover clips</b> need ffmpeg. Input is the cached file, else the head segment, else —
/// when the MP4 index sits at the tail (<see cref="Mp4Boxes"/>) — ffmpeg over HTTPS with our CA bundle.</para>
/// </summary>
public sealed class MediaPreviewService : IDisposable
{
    /// <summary>The host Phase E maps onto <see cref="Directory"/> (SetVirtualHostNameToFolderMapping).</summary>
    public const string VirtualHost = "previews.win4k";

    internal const string NoFfmpegSource = "no-ffmpeg";

    /// <summary>A thumbnail narrower than this is replaced by a video frame whenever one is cheap to take.</summary>
    internal const int SharpWidth = 360;

    private readonly MediaPreviewOptions _options;
    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly TimeProvider _time;
    private readonly Func<string?> _caFile;
    private readonly LoopbackRelay? _relay;
    private readonly Func<ClipInfo, string?> _inputFileFor;
    private readonly string? _ffmpeg;
    private readonly string _indexPath;
    private readonly CrossProcessLock _indexLock;
    private readonly Timer _saveTimer;

    private readonly Lock _gate = new();
    private readonly PreviewIndex _index;
    private Dictionary<string, ClipInfo> _catalog = new(StringComparer.Ordinal);
    private List<string> _order = [];
    private readonly LinkedList<WorkItem> _demand = new();
    private readonly Dictionary<WorkItem, LinkedListNode<WorkItem>> _demandNodes = new();
    private readonly Dictionary<WorkItem, CancellationTokenSource> _running = new();
    private int _bgStill;
    private int _bgHover;
    private bool _backgroundFill;

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task[] _workers = [];
    private int _disposed;

    internal readonly record struct WorkItem(string Key, PreviewKind Kind);

    /// <param name="settings">Read once, for <see cref="AppSettings.FfmpegPath"/>.</param>
    /// <param name="cache">Where to find already-downloaded clips; null reads every clip from the network.</param>
    public MediaPreviewService(AppSettings settings, VideoCache? cache = null, MediaPreviewOptions? options = null)
    {
        _options = options ?? new MediaPreviewOptions();
        _http = _options.Http ?? Http.Client;
        _runner = _options.Runner ?? new ProcessRunner();
        _time = _options.Time ?? TimeProvider.System;
        _caFile = _options.CaFile ?? (() => CaBundle.Ensure());
        if (_options.RelayRemoteInputs)
        {
            try
            {
                _relay = new LoopbackRelay(_http);
            }
            catch (Exception ex)
            {
                Log.Warn($"Previews: could not start the loopback relay ({ex.Message}); ffmpeg will fetch directly");
            }
        }
        _inputFileFor = _options.InputFileFor ?? (clip => DefaultInputFile(clip, cache));
        _ffmpeg = _options.FfmpegPath ?? (_options.AutoDetectFfmpeg ? FfmpegLocator.Locate(settings.FfmpegPath) : null);
        Capabilities = new PreviewCapabilities(_ffmpeg is not null, _ffmpeg);

        Directory = _options.Directory ?? AppPaths.PreviewsDirectory;
        System.IO.Directory.CreateDirectory(TempDirectory);
        _indexPath = Path.Combine(Directory, PreviewIndex.FileName);
        _indexLock = new CrossProcessLock(@"Local\Win4KSS.Previews." + CacheMutexNames.RootHash(Directory));
        _saveTimer = new Timer(_ => Flush());

        _index = PreviewIndex.Load(_indexPath);
        Reconcile();

        if (_ffmpeg is null)
            Log.Warn("Previews: ffmpeg not found — remote thumbnails and placeholders only, no hover clips");
    }

    // ---- public surface ------------------------------------------------------------------

    /// <summary>The previews folder: &lt;hash&gt;.jpg / .mp4 files plus index.json.</summary>
    public string Directory { get; }

    private string TempDirectory => Path.Combine(Directory, "tmp");

    public PreviewCapabilities Capabilities { get; }

    /// <summary>
    /// Raised when a preview finishes — Ready, Failed or Placeholder — and when one is evicted
    /// (Missing). Raised on a background thread; see the class remarks.
    /// </summary>
    public event EventHandler<PreviewEventArgs>? PreviewUpdated;

    /// <summary>The filename stem used for a clip's previews: 16 hex digits of SHA-1(key).</summary>
    public static string HashFor(string key) => PreviewIndex.HashFor(key);

    /// <summary>
    /// The clips previews can be made for; background fill walks them in this order. Call once
    /// the catalogue is loaded and again after a refresh. Keys not in it are ignored by
    /// <see cref="Request"/>.
    /// </summary>
    public void SetCatalog(IEnumerable<ClipInfo> clips)
    {
        var map = new Dictionary<string, ClipInfo>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var clip in clips)
        {
            if (map.TryAdd(clip.Key, clip))
                order.Add(clip.Key);
        }

        lock (_gate)
        {
            _catalog = map;
            _order = order;
            _bgStill = 0;
            _bgHover = 0;
        }
        Wake();
    }

    /// <summary>
    /// Starts the workers. Until this is called, requests only queue. Background fill (stills
    /// for every clip, then hover clips) runs whenever no on-demand work is waiting.
    /// </summary>
    public void Start(bool backgroundFill = true)
    {
        lock (_gate)
        {
            if (_workers.Length > 0 || _disposed != 0)
                return;
            _backgroundFill = backgroundFill;
            var stop = _stop.Token;
            _workers = Enumerable.Range(0, Math.Max(1, _options.Workers))
                                 .Select(_ => Task.Run(() => WorkerAsync(stop)))
                                 .ToArray();
        }
        Log.Info($"Previews: started ({_options.Workers} workers, background fill {(backgroundFill ? "on" : "off")}, " +
                 $"ffmpeg {(_ffmpeg ?? "missing")})");
        Wake();
    }

    /// <summary>Whether idle workers fill in previews for the whole catalogue. Can be toggled at any time.</summary>
    public bool BackgroundFill
    {
        get { lock (_gate) return _backgroundFill; }
        set
        {
            lock (_gate)
                _backgroundFill = value;
            if (value)
                Wake();
        }
    }

    /// <summary>
    /// Asks for previews of cards that just scrolled into view. They jump ahead of everything
    /// queued (the newest request first, in the order given, stills before hover clips) and
    /// count as an access for the LRU. Returns the current state of each requested preview at
    /// once — Ready ones can be shown immediately; the rest arrive via <see cref="PreviewUpdated"/>.
    /// A preview that failed within <see cref="MediaPreviewOptions.RetryAfter"/> is not retried.
    /// </summary>
    public IReadOnlyList<PreviewState> Request(IEnumerable<string> keys, PreviewKind kinds = PreviewKind.Both)
    {
        var keyList = keys.Distinct(StringComparer.Ordinal).ToList();
        var items = new List<WorkItem>();
        foreach (var kind in new[] { PreviewKind.Still, PreviewKind.Hover })
        {
            if (kinds.HasFlag(kind))
                items.AddRange(keyList.Select(k => new WorkItem(k, kind)));
        }

        List<PreviewState> states;
        lock (_gate)
        {
            var now = Now;
            foreach (var key in keyList)
            {
                if (_catalog.ContainsKey(key))
                    _index.GetOrAdd(key).LastAccessUtc = now;
            }

            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                RemoveDemandLocked(item);
                if (!_running.ContainsKey(item) && NeedsWorkLocked(item))
                    _demandNodes[item] = _demand.AddFirst(item);
            }

            states = items.Select(StateLocked).ToList();
        }
        ScheduleSave();
        Wake();
        return states;
    }

    /// <summary>
    /// For cards scrolled away: drops their queued on-demand work and cancels (kills ffmpeg for)
    /// any of it already running. No event is raised for the cancellation; a later
    /// <see cref="Request"/> returns the true state. Background fill may still reach them.
    /// </summary>
    public void Cancel(IEnumerable<string> keys)
    {
        var cancel = new List<CancellationTokenSource>();
        lock (_gate)
        {
            foreach (var key in keys)
            {
                foreach (var kind in new[] { PreviewKind.Still, PreviewKind.Hover })
                {
                    var item = new WorkItem(key, kind);
                    RemoveDemandLocked(item);
                    if (_running.TryGetValue(item, out var cts))
                        cancel.Add(cts);
                }
            }
        }
        foreach (var cts in cancel)
            TryCancel(cts);
    }

    /// <summary>The current state of one preview. <paramref name="kind"/> must be Still or Hover.</summary>
    public PreviewState Get(string key, PreviewKind kind)
    {
        RequireSingle(kind);
        lock (_gate)
            return StateLocked(new WorkItem(key, kind));
    }

    public PreviewStats Stats()
    {
        lock (_gate)
        {
            var records = _index.Entries.Values;
            return new PreviewStats(
                records.Count(r => r.Still?.Status == PreviewStatus.Ready),
                records.Count(r => r.Hover?.Status == PreviewStatus.Ready),
                records.Count(r => r.Still?.Status == PreviewStatus.Failed || r.Hover?.Status == PreviewStatus.Failed),
                _index.TotalBytes,
                _options.CapBytes,
                _demand.Count + _running.Count);
        }
    }

    /// <summary>
    /// Generates one preview now, on the caller's task, bypassing the queue (and
    /// <see cref="Start"/>). Returns the existing state if it is already Ready or inside its
    /// failure window, unless <paramref name="force"/>; Pending if a worker is already on it.
    /// </summary>
    public async Task<PreviewState> GenerateAsync(string key, PreviewKind kind, bool force = false, CancellationToken ct = default)
    {
        RequireSingle(kind);
        var item = new WorkItem(key, kind);
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_catalog.ContainsKey(key) || _running.ContainsKey(item) || (!force && !NeedsWorkLocked(item)) ||
                (kind == PreviewKind.Hover && _ffmpeg is null))
            {
                return StateLocked(item);
            }
            RemoveDemandLocked(item);
            cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
            _running[item] = cts;
        }

        await RunJobAsync(item, cts).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Get(key, kind);
    }

    /// <summary>A "#rrggbb" tint for a placeholder card: stable per category, muted, dark enough for white text.</summary>
    public static string PlaceholderColor(string? category)
    {
        var name = (string.IsNullOrWhiteSpace(category) ? "Other" : category.Trim()).ToLowerInvariant();
        uint hash = 2166136261;
        foreach (var c in name)
            hash = (hash ^ c) * 16777619;

        var hue = hash % 360 / 360.0;
        const double s = 0.35, l = 0.30;
        return HslToHex(hue, s, l);
    }

    /// <summary>Stops the workers (killing any ffmpeg) and writes index.json. Queued work is dropped.</summary>
    public async Task StopAsync()
    {
        Task[] workers;
        List<CancellationTokenSource> running;
        lock (_gate)
        {
            workers = _workers;
            running = _running.Values.ToList();
        }
        TryCancel(_stop);
        foreach (var cts in running)
            TryCancel(cts);

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            Log.Warn("Previews: workers did not stop within 15 s");
        }
        Flush();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Previews: stop failed", ex);
        }
        _relay?.Dispose();
        _saveTimer.Dispose();
        _indexLock.Dispose();
        _signal.Dispose();
        _stop.Dispose();
    }

    // ---- scheduling ----------------------------------------------------------------------

    /// <summary>
    /// The next piece of work, already marked running: on-demand first (newest request first),
    /// then background stills in catalogue order, then background hover clips. Null when idle.
    /// </summary>
    internal (WorkItem Item, CancellationTokenSource Cts)? TryTakeNext()
    {
        lock (_gate)
        {
            while (_demand.First is { } node)
            {
                var item = node.Value;
                RemoveDemandLocked(item);
                if (!_running.ContainsKey(item) && NeedsWorkLocked(item))
                    return (item, ClaimLocked(item));
            }

            if (!_backgroundFill || _index.TotalBytes >= _options.CapBytes * _options.BackgroundFillCapFraction)
                return null;

            while (_bgStill < _order.Count)
            {
                var item = new WorkItem(_order[_bgStill++], PreviewKind.Still);
                if (!_running.ContainsKey(item) && NeedsWorkLocked(item))
                    return (item, ClaimLocked(item));
            }

            while (_ffmpeg is not null && _bgHover < _order.Count)
            {
                var item = new WorkItem(_order[_bgHover++], PreviewKind.Hover);
                if (!_running.ContainsKey(item) && NeedsWorkLocked(item))
                    return (item, ClaimLocked(item));
            }
            return null;
        }
    }

    private CancellationTokenSource ClaimLocked(WorkItem item)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _running[item] = cts;
        return cts;
    }

    private void RemoveDemandLocked(WorkItem item)
    {
        if (_demandNodes.Remove(item, out var node))
            _demand.Remove(node);
    }

    private bool NeedsWorkLocked(WorkItem item)
    {
        if (!_catalog.TryGetValue(item.Key, out var clip))
            return false;
        if (item.Kind == PreviewKind.Hover && (_ffmpeg is null || (clip.LocalPath is null && clip.Urls.IsEmpty)))
            return false;

        var existing = _index.Entries.GetValueOrDefault(item.Key)?.Get(item.Kind);
        return existing?.Status switch
        {
            null or PreviewStatus.Missing => true,
            PreviewStatus.Failed or PreviewStatus.Placeholder =>
                Now - existing.UpdatedUtc >= _options.RetryAfter ||
                (existing.Source == NoFfmpegSource && _ffmpeg is not null),
            _ => false,
        };
    }

    private PreviewState StateLocked(WorkItem item)
    {
        var record = _index.Entries.GetValueOrDefault(item.Key);
        var existing = record?.Get(item.Kind);
        var color = PlaceholderColor(_catalog.GetValueOrDefault(item.Key)?.Category);

        var status = existing?.Status ?? PreviewStatus.Missing;
        if (status != PreviewStatus.Ready)
        {
            if (_running.ContainsKey(item) || _demandNodes.ContainsKey(item))
                status = PreviewStatus.Pending;
            else if (item.Kind == PreviewKind.Hover && _ffmpeg is null)
                status = PreviewStatus.Unavailable;
        }

        var ready = status == PreviewStatus.Ready;
        return new PreviewState(
            item.Key, item.Kind, status,
            ready ? existing!.File : null,
            ready ? existing!.UpdatedUtc.Ticks : 0,
            existing?.Source,
            status is PreviewStatus.Failed or PreviewStatus.Placeholder ? existing?.Error : null,
            color);
    }

    private void Wake()
    {
        // Enough permits for every worker; extra ones only cause a harmless empty pass.
        if (_signal.CurrentCount < Math.Max(1, _options.Workers))
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently.
            }
        }
    }

    private async Task WorkerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var next = TryTakeNext();
            if (next is null)
            {
                try
                {
                    await _signal.WaitAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                continue;
            }

            await RunJobAsync(next.Value.Item, next.Value.Cts).ConfigureAwait(false);
        }
    }

    // ---- generation ----------------------------------------------------------------------

    /// <summary>Runs one claimed item, records the outcome, and releases the claim.</summary>
    private async Task RunJobAsync(WorkItem item, CancellationTokenSource cts)
    {
        ClipInfo? clip;
        string hash;
        lock (_gate)
        {
            clip = _catalog.GetValueOrDefault(item.Key);
            hash = _index.GetOrAdd(item.Key).Hash;
        }

        var job = clip is null ? null : new Job(this, clip, hash);
        try
        {
            if (job is null)
                return;

            var result = await GuardedAsync(job, item.Kind, ct => item.Kind == PreviewKind.Still
                                                ? MakeStillAsync(job, ct)
                                                : MakeHoverAsync(job, ct), cts.Token).ConfigureAwait(false);
            if (result is not null)
                Record(item, result);

            if (cts.IsCancellationRequested || !job.HasLocalInput)
                return;

            // The input is on disk now. A still that had to download a head segment has already
            // paid for what the hover clip needs: make it now rather than fetch the same bytes later.
            if (item.Kind == PreviewKind.Still && job.DownloadedHead)
                await FollowUpAsync(job, new WorkItem(item.Key, PreviewKind.Hover), upgrade: false, cts.Token).ConfigureAwait(false);

            // And a hover clip made from a local file or head can replace a postage-stamp
            // thumbnail (archive.org's .thumbs are 160 px) with a sharp 480 px frame for ~1 s.
            if (item.Kind == PreviewKind.Hover && result?.Status == PreviewStatus.Ready)
                await FollowUpAsync(job, new WorkItem(item.Key, PreviewKind.Still), upgrade: true, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            job?.Dispose();
            Release(item, cts);
        }
    }

    /// <summary>
    /// Makes a second preview of the same clip inside a job that already has its input. With
    /// <paramref name="upgrade"/>, replaces a Ready but small thumbnail still, and records only a success.
    /// </summary>
    private async Task FollowUpAsync(Job job, WorkItem item, bool upgrade, CancellationToken parent)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            var wanted = upgrade ? IsSmallStillLocked(item.Key) : NeedsWorkLocked(item);
            if (wanted && !_running.ContainsKey(item))
            {
                RemoveDemandLocked(item);
                cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
                _running[item] = cts;
            }
        }
        if (cts is null)
            return;

        try
        {
            var result = await GuardedAsync(job, item.Kind, ct => item.Kind == PreviewKind.Still
                                                ? StillFromVideoAsync(job, ct)
                                                : MakeHoverAsync(job, ct), cts.Token).ConfigureAwait(false);
            if (result is not null && (!upgrade || result.Status == PreviewStatus.Ready))
                Record(item, result);
        }
        finally
        {
            Release(item, cts);
        }
    }

    private bool IsSmallStillLocked(string key) =>
        _ffmpeg is not null &&
        _index.Entries.GetValueOrDefault(key)?.Still is { Status: PreviewStatus.Ready, Source: "thumbnail", Width: { } width } &&
        width < SharpWidth;

    /// <returns>The outcome, or null when cancelled (nothing to record).</returns>
    private static async Task<PreviewItem?> GuardedAsync(Job job, PreviewKind kind, Func<CancellationToken, Task<PreviewItem>> make, CancellationToken ct)
    {
        try
        {
            return await make(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Previews: {kind} for {job.Clip.Key} failed: {ex.Message}");
            return new PreviewItem
            {
                Status = PreviewStatus.Failed,
                Error = Truncate(ex.Message, 300),
                UpdatedUtc = job.Owner.Now,
            };
        }
    }

    private void Release(WorkItem item, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(item, out var current) && ReferenceEquals(current, cts))
                _running.Remove(item);
        }
        cts.Dispose();
    }

    private async Task<PreviewItem> MakeStillAsync(Job job, CancellationToken ct)
    {
        string? error = null;

        if (!string.IsNullOrWhiteSpace(job.Clip.ThumbnailUrl))
        {
            try
            {
                var thumbnail = await StillFromThumbnailAsync(job, job.Clip.ThumbnailUrl, ct).ConfigureAwait(false);

                // A tiny thumbnail when the clip is already on disk: a real frame costs a second.
                if (thumbnail.Width < SharpWidth && _ffmpeg is not null && job.LocalInputPath() is not null)
                {
                    var frame = await StillFromVideoAsync(job, ct).ConfigureAwait(false);
                    if (frame.Status == PreviewStatus.Ready)
                        return frame;
                }
                return thumbnail;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = "thumbnail: " + ex.Message;
            }
        }

        if (_ffmpeg is null)
        {
            // Nothing else can be tried without ffmpeg. A failed thumbnail is a failure (retried
            // after the window); no thumbnail at all is a placeholder, retried as soon as ffmpeg appears.
            return error is null
                ? Item(PreviewStatus.Placeholder, null, NoFfmpegSource, "no thumbnail and ffmpeg is not installed")
                : Item(PreviewStatus.Failed, null, NoFfmpegSource, error);
        }

        var still = await StillFromVideoAsync(job, ct).ConfigureAwait(false);
        if (still.Status != PreviewStatus.Ready && error is not null)
            still.Error = Truncate(error + "; " + still.Error, 300);
        return still;
    }

    /// <summary>A keyframe from the clip's video: cached file, else head segment, else over HTTPS.</summary>
    private async Task<PreviewItem> StillFromVideoAsync(Job job, CancellationToken ct)
    {
        var input = await job.VideoInputAsync(ct).ConfigureAwait(false);
        if (input is null)
            return Item(PreviewStatus.Placeholder, null, null, "no video to take a frame from");

        var seek = PreviewCommands.SeekSeconds(job.Clip.DurationSeconds);
        return await RunAttemptsAsync(job, input, seek, ".jpg",
            (src, output, ss) => PreviewCommands.StillFromVideo(Input(src), output, ss, CaFileFor(src)),
            ct).ConfigureAwait(false);
    }

    /// <summary>What ffmpeg is actually given to read: remote URLs go through the relay when there is one.</summary>
    private string Input(string src) =>
        _relay is not null && PreviewCommands.IsRemote(src) ? _relay.UrlFor(src) : src;

    private string? CaFileFor(string src) =>
        _relay is null && PreviewCommands.IsRemote(src) ? _caFile() : null;

    private async Task<PreviewItem> MakeHoverAsync(Job job, CancellationToken ct)
    {
        if (_ffmpeg is null)
            return Item(PreviewStatus.Unavailable, null, NoFfmpegSource, "ffmpeg is not installed");

        var input = await job.VideoInputAsync(ct).ConfigureAwait(false);
        if (input is null)
            return Item(PreviewStatus.Failed, null, null, "no video URL");

        var seek = PreviewCommands.SeekSeconds(job.Clip.DurationSeconds);
        return await RunAttemptsAsync(job, input, seek, ".mp4",
            (src, output, ss) => PreviewCommands.Hover(Input(src), output, ss, CaFileFor(src)),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs ffmpeg against the input, falling back when the first attempt produces nothing:
    /// a head segment may be too short for the seek (→ the full file over HTTPS, then the head
    /// from 0 s); a clip of unknown, short duration may end before the seek (→ 0 s).
    /// </summary>
    private async Task<PreviewItem> RunAttemptsAsync(
        Job job, VideoInput input, double seek, string extension,
        Func<string, string, double, IReadOnlyList<string>> build, CancellationToken ct)
    {
        var attempts = new List<(string Source, string Input, double Seek)> { (input.Source, input.Path, seek) };
        if (input.Source == "head" && input.Url is not null)
            attempts.Add(("remote", input.Url, seek));
        if (seek > 0)
            attempts.Add((input.Source, input.Path, 0));

        string error = "no attempt";
        foreach (var (source, src, ss) in attempts)
        {
            var temp = job.TempFile(extension);
            var result = await _runner.RunAsync(_ffmpeg!, build(src, temp, ss), _options.ProcessTimeout, ct).ConfigureAwait(false);
            if (result.Succeeded && NonEmpty(temp))
                return Commit(job, temp, extension, source, PreviewCommands.Width);

            error = result.TimedOut
                ? $"ffmpeg timed out after {_options.ProcessTimeout.TotalSeconds:0} s"
                : $"ffmpeg exit {result.ExitCode}: {LastLine(result.StdErr)}";
            TryDelete(temp);
        }

        Log.Warn($"Previews: {extension} for {job.Clip.Key} failed: {error}");
        return Item(PreviewStatus.Failed, null, null, error);
    }

    private async Task<PreviewItem> StillFromThumbnailAsync(Job job, string url, CancellationToken ct)
    {
        var bytes = await FetchAsync(url, _options.MaxThumbnailBytes, ct).ConfigureAwait(false);
        var ext = SniffImage(bytes) ?? throw new InvalidDataException("not a JPEG, PNG or WebP image");
        var width = ImageWidth(bytes);

        if (_ffmpeg is not null)
        {
            var source = job.TempFile(ext);
            await File.WriteAllBytesAsync(source, bytes, ct).ConfigureAwait(false);
            var temp = job.TempFile(".jpg");
            var result = await _runner.RunAsync(_ffmpeg, PreviewCommands.StillFromImage(source, temp), _options.ProcessTimeout, ct)
                                      .ConfigureAwait(false);
            if (result.Succeeded && NonEmpty(temp))
                return Commit(job, temp, ".jpg", "thumbnail", width is { } w ? Math.Min(w, PreviewCommands.Width) : null);
            TryDelete(temp);
            Log.Warn($"Previews: re-encoding the thumbnail for {job.Clip.Key} failed ({LastLine(result.StdErr)}); storing it as-is");
        }

        // No ffmpeg (or it choked): keep the image as published. tvOS images are 900 px PNGs;
        // the browser scales them, at some cost in bytes.
        var stored = job.TempFile(ext);
        await File.WriteAllBytesAsync(stored, bytes, ct).ConfigureAwait(false);
        return Commit(job, stored, ext, "thumbnail", width);
    }

    /// <summary>Moves a finished temp file into place as &lt;hash&gt;&lt;ext&gt; and describes it.</summary>
    private PreviewItem Commit(Job job, string temp, string extension, string source, int? width)
    {
        var name = job.Hash + extension;
        var final = Path.Combine(Directory, name);
        MoveWithRetry(temp, final);
        var item = Item(PreviewStatus.Ready, name, source, null, new FileInfo(final).Length);
        item.Width = width;
        return item;
    }

    private PreviewItem Item(PreviewStatus status, string? file, string? source, string? error, long bytes = 0) => new()
    {
        Status = status,
        File = file,
        Bytes = bytes,
        Source = source,
        Error = error is null ? null : Truncate(error, 300),
        UpdatedUtc = Now,
    };

    private void Record(WorkItem item, PreviewItem result)
    {
        if (result.Status == PreviewStatus.Unavailable)
            return;

        string? stale = null;
        PreviewState state;
        lock (_gate)
        {
            var record = _index.GetOrAdd(item.Key);
            var old = record.Get(item.Kind);
            if (old?.File is { } oldFile && !string.Equals(oldFile, result.File, StringComparison.OrdinalIgnoreCase))
                stale = oldFile;
            // The version (cache-buster) must move on every rewrite, even within one clock tick.
            if (old is not null && result.UpdatedUtc <= old.UpdatedUtc)
                result.UpdatedUtc = old.UpdatedUtc.AddTicks(1);
            record.Set(item.Kind, result);
            record.LastAccessUtc = Now;
            state = StateLocked(item);
        }

        if (stale is not null)
            TryDelete(Path.Combine(Directory, stale));
        ScheduleSave();
        EnforceCap();
        Raise(state);
    }

    /// <summary>Evicts least-recently-accessed clips' previews until the folder fits the cap.</summary>
    private void EnforceCap()
    {
        var files = new List<string>();
        var states = new List<PreviewState>();
        lock (_gate)
        {
            var protect = _running.Keys.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _index.SelectEvictions(_options.CapBytes, protect))
            {
                var record = _index.Entries[key];
                foreach (var kind in new[] { PreviewKind.Still, PreviewKind.Hover })
                {
                    if (record.Get(kind) is { Status: PreviewStatus.Ready, File: { } file })
                    {
                        files.Add(Path.Combine(Directory, file));
                        record.Set(kind, null);
                        states.Add(StateLocked(new WorkItem(key, kind)));
                    }
                }
            }
        }

        if (files.Count == 0)
            return;
        foreach (var file in files)
            TryDelete(file);
        Log.Info($"Previews: evicted {files.Count} file(s) to stay under {VideoCache.FormatSize(_options.CapBytes)}");
        ScheduleSave();
        foreach (var state in states)
            Raise(state);
    }

    private void Raise(PreviewState state)
    {
        var handler = PreviewUpdated;
        if (handler is null)
            return;
        foreach (var target in handler.GetInvocationList().Cast<EventHandler<PreviewEventArgs>>())
        {
            try
            {
                target(this, new PreviewEventArgs(state));
            }
            catch (Exception ex)
            {
                Log.Error("Previews: a PreviewUpdated handler threw", ex);
            }
        }
    }

    // ---- inputs --------------------------------------------------------------------------

    /// <summary>Where a job reads video from. <see cref="Url"/> is the remote fallback for a head segment.</summary>
    internal sealed record VideoInput(string Source, string Path, string? Url);

    /// <summary>One clip's generation: lazily resolves (and downloads) its input once, and owns its temp files.</summary>
    private sealed class Job(MediaPreviewService owner, ClipInfo clip, string hash) : IDisposable
    {
        private readonly List<string> _temps = [];
        private VideoInput? _input;
        private bool _resolved;

        public MediaPreviewService Owner => owner;
        public ClipInfo Clip { get; } = clip;
        public string Hash { get; } = hash;
        public bool DownloadedHead => _input?.Source == "head";

        /// <summary>The input has been resolved to a file on disk (cached clip or downloaded head).</summary>
        public bool HasLocalInput => _input?.Source is "local" or "head";

        /// <summary>The cached or folder file for this clip, without touching the network.</summary>
        public string? LocalInputPath() => _input?.Source == "local" ? _input.Path : owner.LocalFileFor(Clip);

        public string TempFile(string extension)
        {
            var path = System.IO.Path.Combine(owner.TempDirectory, $"{Hash}.{Guid.NewGuid():N}"[..26] + extension);
            _temps.Add(path);
            return path;
        }

        public async Task<VideoInput?> VideoInputAsync(CancellationToken ct)
        {
            if (_resolved)
                return _input;

            _input = await owner.ResolveInputAsync(this, ct).ConfigureAwait(false);
            _resolved = true;
            return _input;
        }

        public void Dispose()
        {
            foreach (var temp in _temps)
                TryDelete(temp);
        }
    }

    private string? LocalFileFor(ClipInfo clip)
    {
        try
        {
            return _inputFileFor(clip);
        }
        catch (Exception ex)
        {
            Log.Warn($"Previews: cache lookup for {clip.Key} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<VideoInput?> ResolveInputAsync(Job job, CancellationToken ct)
    {
        var clip = job.Clip;
        var local = LocalFileFor(clip);
        if (local is not null)
            return new VideoInput("local", local, null);

        // The 1080p SDR rendition where there is one (Apple's): an eighth of the pixels to
        // decode and no HDR to wash out. Harvested sources fall back to their only rendition.
        if (clip.Urls.UrlFor(VideoQuality.Hd1080Sdr) is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;
        var url = uri.AbsoluteUri;
        if (!_options.UseHeadSegments)
            return new VideoInput("remote", url, null);

        var head = await FetchHeadAsync(job, url, HeadBytesFor(clip), ct).ConfigureAwait(false);
        if (head is null)
            return new VideoInput("remote", url, null);
        return new VideoInput("head", head, url);
    }

    /// <summary>
    /// Enough of the file to cover the seek plus the hover clip at the clip's average bitrate,
    /// with margin for the index; <see cref="MediaPreviewOptions.DefaultHeadBytes"/> when the
    /// bitrate is unknown.
    /// </summary>
    internal long HeadBytesFor(ClipInfo clip)
    {
        if (clip.SizeBytes is not > 0 || clip.DurationSeconds is not > 0)
            return _options.DefaultHeadBytes;

        var bytesPerSecond = clip.SizeBytes.Value / clip.DurationSeconds.Value;
        var seconds = PreviewCommands.SeekSeconds(clip.DurationSeconds) + PreviewCommands.HoverSeconds + 2;
        var wanted = (long)(bytesPerSecond * seconds * 1.25) + 1024 * 1024;
        return Math.Clamp(wanted, _options.MinHeadBytes, _options.MaxHeadBytes);
    }

    /// <summary>
    /// Fetches the first <paramref name="maxBytes"/> of a video with a Range request through our
    /// HttpClient (so TLS trust is AppleTrust's) into a temp file. Returns null when the head is
    /// useless on its own — the moov index lives at the tail — so the caller goes to ffmpeg over
    /// HTTPS instead.
    /// </summary>
    private async Task<string?> FetchHeadAsync(Job job, string url, long maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HttpTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureUsable(response, url);

        long? total = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        timeout.CancelAfter(_options.HeadDownloadTimeout);

        // Tail-indexed files (Apple's and Amazon's .mov) are recognised from the first 64 KB
        // and abandoned, rather than downloading 20 MB that ffmpeg cannot use.
        var headIsWhole = total is { } whole && whole <= maxBytes;
        var buffer = await ReadUpToAsync(response, maxBytes, timeout.Token,
            giveUp: headIsWhole ? null : prefix => Mp4Boxes.IndexIsBeyond(prefix.Span, maxBytes)).ConfigureAwait(false);
        var complete = buffer is not null && (buffer.Length < maxBytes || (total is { } t && buffer.Length >= t));

        if (buffer is null || (!complete && Mp4Boxes.LocateMoov(buffer) == MoovLocation.BeyondHead))
        {
            Log.Info($"Previews: {job.Clip.Key} keeps its index at the tail; ffmpeg will read it over HTTPS");
            return null;
        }

        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        var path = job.TempFile(string.IsNullOrEmpty(ext) || ext.Length > 5 ? ".mp4" : ext);
        await File.WriteAllBytesAsync(path, buffer, ct).ConfigureAwait(false);
        return path;
    }

    private async Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HttpTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureUsable(response, url);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"{response.Content.Headers.ContentLength} bytes is too large for a thumbnail");
        return (await ReadUpToAsync(response, maxBytes, timeout.Token).ConfigureAwait(false))!;
    }

    private static void EnsureUsable(HttpResponseMessage response, string url)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{url} returned an HTML page");
    }

    /// <param name="giveUp">Asked once, when 64 KB have arrived; true abandons the download (returns null).</param>
    private static async Task<byte[]?> ReadUpToAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct, Func<ReadOnlyMemory<byte>, bool>? giveUp = null)
    {
        const int DecideAfter = 64 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (memory.Length < maxBytes)
        {
            var want = (int)Math.Min(buffer.Length, maxBytes - memory.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            memory.Write(buffer, 0, read);

            if (giveUp is not null && memory.Length >= DecideAfter)
            {
                if (giveUp(memory.GetBuffer().AsMemory(0, (int)memory.Length)))
                    return null;
                giveUp = null;
            }
        }
        return memory.ToArray();
    }

    /// <summary>Pixel width of a JPEG or PNG from its header, or null for anything else.</summary>
    internal static int? ImageWidth(ReadOnlySpan<byte> bytes)
    {
        var type = SniffImage(bytes);
        if (type == ".png")
            return bytes.Length >= 24 ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) : null;
        if (type != ".jpg")
            return null;

        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2;
                continue;
            }
            // SOF0-SOF15 carry the frame size; C4 (DHT), C8 (JPG) and CC (DAC) share the range but do not.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..]);
            i += 2 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
        }
        return null;
    }

    /// <summary>Folder clips read their own file; others any rendition already in the video cache, SDR first.</summary>
    private static string? DefaultInputFile(ClipInfo clip, VideoCache? cache)
    {
        if (clip.LocalPath is not null)
            return File.Exists(clip.LocalPath) ? clip.LocalPath : null;
        if (cache is null)
            return null;

        var urls = clip.Urls;
        foreach (var raw in new[] { urls.Hd1080Sdr, urls.Hd1080H264, urls.Uhd4KSdr, urls.Hd1080Hdr, urls.Uhd4KHdr })
        {
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                continue;
            if (cache.CachedSize(uri) is > 0)
                return cache.PathFor(uri);
        }
        return null;
    }

    /// <summary>The extension for an image's bytes (".jpg", ".png", ".webp"), or null if it is none of them.</summary>
    internal static string? SniffImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ".png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }

    // ---- persistence ---------------------------------------------------------------------

    /// <summary>
    /// Brings the index and the folder into agreement at startup: drops records whose file is
    /// gone, deletes files no record claims, and clears temp leftovers from a killed run.
    /// </summary>
    private void Reconcile()
    {
        foreach (var temp in SafeFiles(TempDirectory))
            TryDelete(temp);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var record in _index.Entries.Values)
        {
            foreach (var kind in new[] { PreviewKind.Still, PreviewKind.Hover })
            {
                if (record.Get(kind) is not { Status: PreviewStatus.Ready } item)
                    continue;
                if (item.File is { } file && File.Exists(Path.Combine(Directory, file)))
                {
                    claimed.Add(file);
                }
                else
                {
                    record.Set(kind, null);
                    changed = true;
                }
            }
        }

        foreach (var path in SafeFiles(Directory))
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".jpg" or ".png" or ".webp" or ".mp4" && !claimed.Contains(name))
                TryDelete(path);
        }

        if (changed)
            Flush();
    }

    private void ScheduleSave()
    {
        try
        {
            _saveTimer.Change(_options.SaveDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposing; Dispose flushes.
        }
    }

    /// <summary>Writes index.json now (atomically, under a named mutex).</summary>
    public void Flush()
    {
        string text;
        lock (_gate)
            text = _index.Serialize();

        try
        {
            using (_indexLock.Acquire())
                SharedFile.Write(_indexPath, text);
        }
        catch (Exception ex)
        {
            Log.Error("Previews: could not write index.json", ex);
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    internal DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static void RequireSingle(PreviewKind kind)
    {
        if (kind is not (PreviewKind.Still or PreviewKind.Hover))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Still or Hover");
    }

    private static bool NonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void MoveWithRetry(string from, string to)
    {
        // WebView2 may be serving the old file; a replace then fails for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        try
        {
            return System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // In use or already gone; Reconcile sweeps it up next start.
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished meanwhile.
        }
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : Truncate(lines[^1], 200);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string HslToHex(double h, double s, double l)
    {
        double Channel(double t)
        {
            t = (t % 1 + 1) % 1;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{Byte(Channel(h + 1.0 / 3)):x2}{Byte(Channel(h)):x2}{Byte(Channel(h - 1.0 / 3)):x2}";
    }
}
