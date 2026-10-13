namespace Win4K.Core.Caching;

/// <summary>
/// The answer to "can these clips be kept offline?". The budget rule is
/// <c>pinnedBytes + rollingBytes ≤ budget</c>, and since rolling files are evicted to make
/// room, a pin fits exactly when every pinned clip together fits the budget.
/// </summary>
/// <param name="Ok">The pins fit (or there was nothing to add).</param>
/// <param name="RequiredBytes">Everything that would be pinned, current pins included.</param>
/// <param name="BudgetBytes">The current cache limit.</param>
/// <param name="Added">Keys that would be (or were) newly pinned.</param>
/// <param name="Skipped">Keys that cannot be pinned: local clips, or keys the catalogue does not know.</param>
public sealed record PinCheck(
    bool Ok,
    long RequiredBytes,
    long BudgetBytes,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Skipped)
{
    private const double GiB = 1024.0 * 1024 * 1024;

    public long ShortfallBytes => Math.Max(0, RequiredBytes - BudgetBytes);

    /// <summary>
    /// The limit that would make this fit, in whole GB rounded up — the X in
    /// "Raise limit to X GB?". Assign it to <see cref="AppSettings.CacheBudgetGb"/>.
    /// </summary>
    public int RequiredGb => (int)Math.Ceiling(RequiredBytes / GiB);
}

/// <summary>
/// Kept-offline clips ("pins"). The set lives in <see cref="AppSettings.KeptOffline"/> (clip
/// keys); this class keeps it, the budget, and the download queue in agreement:
///
///  * <see cref="CanPin"/> / <see cref="Pin"/> refuse anything that would push the pinned
///    total over the budget, reporting the limit that would fit (<see cref="PinCheck.RequiredGb"/>).
///    Sizes come from the catalogue (<see cref="ClipInfo.SizeBytes"/>), falling back to the
///    cached file, then to <see cref="UnknownClipBytes"/>.
///  * Pinning queues the download at <see cref="DownloadPriority.Pin"/>; unpinning cancels it
///    and leaves any cached file as ordinary rolling cache.
///  * <see cref="SyncPins"/> (call after loading the catalogue and after a quality change)
///    queues every pin whose current-quality rendition is missing. The old rendition stays
///    pinned until the new one lands, then is released (<see cref="VideoCache.ReleaseOtherRenditions"/>).
///
/// UI-free; meant to be driven from one thread (the browser's UI thread), because it edits the
/// settings object the caller also edits. It saves settings after every change.
/// </summary>
public sealed class PinManager
{
    /// <summary>Size assumed for a clip nobody has published a size for: about one Apple 4K clip.</summary>
    public const long UnknownClipBytes = 350L * 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly VideoCache _cache;
    private readonly DownloadQueue _queue;
    private readonly Action<AppSettings> _save;
    private Dictionary<string, ClipInfo> _catalog = new(StringComparer.Ordinal);

    /// <param name="save">Persists settings; defaults to <see cref="AppSettings.Save"/> at the standard path.</param>
    public PinManager(AppSettings settings, VideoCache cache, DownloadQueue queue, Action<AppSettings>? save = null)
    {
        _settings = settings;
        _cache = cache;
        _queue = queue;
        _save = save ?? (s => s.Save());
    }

    /// <summary>The catalogue pins are resolved against (<see cref="Catalogs.ClipLibrary.LoadAsync(AppSettings?, CancellationToken)"/>).</summary>
    public void SetCatalog(IEnumerable<ClipInfo> clips)
    {
        var map = new Dictionary<string, ClipInfo>(StringComparer.Ordinal);
        foreach (var clip in clips)
            map[clip.Key] = clip;
        _catalog = map;
    }

    public IReadOnlyCollection<string> Pinned => _settings.KeptOffline;

    public bool IsPinned(string key) => _settings.KeptOffline.Contains(key);

    /// <summary>
    /// The rendition a clip is pinned at: the user's quality for Apple's clips, 4K SDR for the
    /// harvested catalogues — the same choice the saver makes when it plays them, so the
    /// pinned file is the one it looks for. Null for local clips (nothing to download).
    /// </summary>
    public static Clip? ClipFor(ClipInfo info, VideoQuality quality)
    {
        if (info.IsLocal)
            return null;

        var apple = string.Equals(info.CatalogId, AppleAerialSource.SourceId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.SourceId, AppleAerialSource.SourceId, StringComparison.OrdinalIgnoreCase);
        return info.ToClip(apple ? AppleAerialSource.Resolve(quality) : VideoQuality.Uhd4KSdr);
    }

    /// <summary>The clip to download for a pinned key at the current quality, or null if unknown or local.</summary>
    public Clip? TargetClip(string key) =>
        _catalog.TryGetValue(key, out var info) ? ClipFor(info, _settings.Quality) : null;

    /// <summary>What keeping a clip offline costs, in bytes. Never zero.</summary>
    public long EstimatedBytes(string key)
    {
        var clip = TargetClip(key);
        if (clip is not null && _cache.CachedSize(clip.Url) is { } onDisk)
            return onDisk;
        if (clip?.SizeBytes is > 0)
            return clip.SizeBytes.Value;
        if (_catalog.TryGetValue(key, out var info) && info.SizeBytes is > 0)
            return info.SizeBytes.Value;

        var cached = _cache.EntriesFor(key).Select(e => e.SizeBytes).DefaultIfEmpty(0).Max();
        return cached > 0 ? cached : UnknownClipBytes;
    }

    /// <summary>Whether pinning <paramref name="keys"/> (on top of the current pins) fits the budget. Changes nothing.</summary>
    public PinCheck CanPin(IEnumerable<string> keys)
    {
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (_settings.KeptOffline.Contains(key))
                continue;
            if (TargetClip(key) is null)
                skipped.Add(key);
            else
                added.Add(key);
        }

        var required = _settings.KeptOffline.Concat(added).Sum(EstimatedBytes);
        var budget = _settings.CacheBudgetBytes;
        return new PinCheck(required <= budget || added.Count == 0, required, budget, added, skipped);
    }

    /// <summary>
    /// Keeps clips offline — one, or "all filtered" — if they fit. On refusal nothing changes;
    /// offer to raise the limit to <see cref="PinCheck.RequiredGb"/> and call again.
    /// </summary>
    public PinCheck Pin(IEnumerable<string> keys)
    {
        var check = CanPin(keys);
        if (!check.Ok || check.Added.Count == 0)
            return check;

        foreach (var key in check.Added)
            _settings.KeptOffline.Add(key);
        Save();

        foreach (var key in check.Added)
        {
            if (TargetClip(key) is { } clip)
                _queue.Enqueue(clip, DownloadPriority.Pin);
        }

        Log.Info($"Pins: kept {check.Added.Count} clip(s) offline; {VideoCache.FormatSize(check.RequiredBytes)} pinned " +
                 $"of a {VideoCache.FormatSize(check.BudgetBytes)} limit");
        return check;
    }

    /// <summary>
    /// Stops keeping clips offline: their pin downloads are cancelled (partials discarded) and
    /// any cached file becomes rolling cache, evicted in LRU order like any other.
    /// </summary>
    /// <returns>How many keys were pinned.</returns>
    public int Unpin(IEnumerable<string> keys)
    {
        var removed = keys.Distinct(StringComparer.Ordinal).Where(_settings.KeptOffline.Remove).ToList();
        if (removed.Count == 0)
            return 0;

        Save();
        foreach (var key in removed)
        {
            if (_queue.Get(key) is { Priority: DownloadPriority.Pin })
                _queue.Cancel(key);
        }

        Log.Info($"Pins: released {removed.Count} clip(s)");
        return removed.Count;
    }

    /// <summary>
    /// Queues every pinned clip that is not on disk at the current quality, and releases
    /// superseded renditions of those that are. Call once the catalogue is loaded, and after
    /// <see cref="AppSettings.Quality"/> changes.
    /// </summary>
    /// <returns>The current pins against the budget — not Ok if a quality change pushed them over.</returns>
    public PinCheck SyncPins()
    {
        foreach (var key in _settings.KeptOffline.ToList())
        {
            if (TargetClip(key) is { } clip)
                _queue.Enqueue(clip, DownloadPriority.Pin);
        }
        var check = CanPin([]);
        return check with { Ok = check.RequiredBytes <= check.BudgetBytes };
    }

    private void Save()
    {
        try
        {
            _save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Pins: could not save settings", ex);
        }
    }
}

/// <summary>
/// Pinned keys read live from settings.json, for a process that does not own the pins. The
/// saver runs for hours on a settings snapshot; this lets its evictions honour a pin made in
/// the browser meanwhile. Re-reads only when the file's timestamp moves.
/// </summary>
public static class PinnedKeySource
{
    public static Func<IReadOnlySet<string>> FromSettingsFile(AppSettings initial, string? path = null)
    {
        var gate = new Lock();
        IReadOnlySet<string> current = new HashSet<string>(initial.KeptOffline, StringComparer.Ordinal);
        var stamp = DateTime.MinValue;

        return () =>
        {
            lock (gate)
            {
                try
                {
                    var file = path ?? AppPaths.SettingsFile;
                    var now = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                    if (now != stamp && now != DateTime.MinValue)
                    {
                        current = new HashSet<string>(AppSettings.Load(file).KeptOffline, StringComparer.Ordinal);
                        stamp = now;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Pins: could not re-read settings: {ex.Message}");
                }
                return current;
            }
        };
    }
}
