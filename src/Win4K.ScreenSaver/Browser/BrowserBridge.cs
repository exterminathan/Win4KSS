using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Media;
using Win4K.Core.Playback;
using Win4K.Core.Updates;
// Win4K.Core.Caching already has its own DownloadProgress (cache downloads); alias the update one.
using UpdateDownloadProgress = Win4K.Core.Updates.DownloadProgress;

namespace Win4K.ScreenSaver.Browser;

public sealed class BrowserBridgeOptions
{
    /// <summary>
    /// Runs an action on the bridge's thread (the UI thread). Service events and timers arrive
    /// on background threads and are funnelled through this. Default: run inline, for tests.
    /// </summary>
    public Action<Action>? Dispatch { get; init; }

    /// <summary>Writes settings. Default: <see cref="BrowserBridge.SaveMerged"/> to settings.json.</summary>
    public Action<AppSettings>? Save { get; init; }

    /// <summary>Loads the catalogue. Default: <see cref="ClipLibrary.LoadAsync(AppSettings?, CancellationToken)"/>.</summary>
    public Func<AppSettings, CancellationToken, Task<IReadOnlyList<ClipInfo>>>? LoadCatalog { get; init; }

    /// <summary>Re-harvests catalogues. Default: a <see cref="CatalogRefresher"/> over every auto source.</summary>
    public Func<IProgress<CatalogRefreshProgress>, CancellationToken, Task<CatalogRefreshReport>>? RefreshCatalogs { get; init; }

    /// <summary>
    /// Whether the catalogues are stale enough to refresh on open (every 7 days). Default:
    /// <see cref="CatalogRefresher.IsDue"/>. Only the browser refreshes; the saver never does.
    /// </summary>
    public Func<bool>? RefreshDue { get; init; }

    public TimeProvider? Time { get; init; }

    /// <summary>Settings edits are written this long after the last one.</summary>
    public TimeSpan SaveDebounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Background state changes (download progress, cache changes) are batched into one stateDelta per interval: ~4 Hz.</summary>
    public TimeSpan DeltaInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Start <see cref="MediaPreviewService"/> with background fill once the catalogue is in.</summary>
    public bool PreviewBackgroundFill { get; init; } = true;

    /// <summary>
    /// Null (the default) means this bridge never checks for updates at all — no automatic
    /// check on open, and <c>checkUpdates</c>/<c>installUpdate</c> page messages are answered
    /// with a notice rather than doing anything. Only <see cref="BrowserForm"/> ever supplies
    /// one, gated by <c>Program.AllowsUpdateCheck</c> — see CLAUDE.md "Never during the saver".
    /// Tests that do not care about updates get this behaviour for free by leaving it null.
    /// </summary>
    public UpdateChecker? UpdateChecker { get; init; }
}

/// <summary>
/// The C# half of the clip browser: turns page messages into calls on the real services and
/// service state into page messages, exactly as docs/browser-bridge-contract.md describes.
/// Deliberately free of WinForms and WebView2 — <see cref="BrowserForm"/> feeds it
/// <c>WebMessageReceived</c> JSON through <see cref="OnPageMessage"/> and hands it
/// <c>PostWebMessageAsJson</c> as the post callback — so all of it is unit-testable.
///
/// <para><b>Threading.</b> Every member is called on one thread (the UI thread). Service events
/// (<see cref="VideoCache.Changed"/>, <see cref="DownloadQueue.JobChanged"/>/<see cref="DownloadQueue.Progress"/>,
/// <see cref="MediaPreviewService.PreviewUpdated"/>) and the bridge's own timers arrive on
/// background threads and are marshalled through <see cref="BrowserBridgeOptions.Dispatch"/>.</para>
///
/// <para><b>Handshake.</b> Nothing is posted until the page sends <c>ready</c>. Then: settings,
/// cacheStats, and — as soon as the catalogue has loaded — catalog followed by a full stateDelta.
/// A second <c>ready</c> (a reload) resends all of it; <see cref="PageUnloaded"/> stops posting
/// until the next one.</para>
///
/// <para><b>State.</b> stateDelta carries only clips whose state differs from what the page was
/// last sent. User actions flush at once; download progress and cache changes are coalesced
/// to one message per <see cref="BrowserBridgeOptions.DeltaInterval"/>.</para>
///
/// <para><b>Settings.</b> Edits apply to the live <see cref="AppSettings"/> immediately (no
/// OK/Cancel) and are written <see cref="BrowserBridgeOptions.SaveDebounce"/> after the last one.</para>
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    private const double GiB = 1024.0 * 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly VideoCache _cache;
    private readonly DownloadQueue _queue;
    private readonly MediaPreviewService _previews;
    private readonly IBrowserHost _host;
    private readonly Action<string> _post;
    private readonly Action<Action> _dispatch;
    private readonly Action<AppSettings> _save;
    private readonly Func<AppSettings, CancellationToken, Task<IReadOnlyList<ClipInfo>>> _loadCatalog;
    private readonly Func<IProgress<CatalogRefreshProgress>, CancellationToken, Task<CatalogRefreshReport>> _refresh;
    private readonly UpdateChecker? _updateChecker;
    private readonly TimeProvider _time;
    private readonly BrowserBridgeOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ITimer _deltaTimer;
    private readonly ITimer _saveTimer;

    private IReadOnlyList<ClipInfo> _catalog = [];
    private Dictionary<string, ClipInfo> _byKey = new(StringComparer.Ordinal);
    private Dictionary<string, int> _sourceCounts = new(StringComparer.Ordinal);
    private string? _catalogJson;
    private bool _catalogLoaded;
    private bool _previewsStarted;
    private bool _pageReady;
    private bool _refreshing;
    private string? _selectedKey;
    private ShortcutOptions? _shortcuts;
    private PortableSettings? _pendingImport;
    private UpdateCheckResult? _lastUpdateStatus;
    private ReleaseInfo? _lastRelease;
    private Task<UpdateCheckOutcome>? _updateCheckInFlight;
    private bool _installing;

    private readonly Dictionary<string, ClipStateDto> _sent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogRefreshProgress> _pendingProgress = new(StringComparer.Ordinal);
    private bool _allDirty;
    private bool _statsDirty;
    private bool _deltaScheduled;
    private bool _savePending;
    private bool _disposed;

    public BrowserBridge(
        AppSettings settings,
        VideoCache cache,
        DownloadQueue queue,
        MediaPreviewService previews,
        IBrowserHost host,
        Action<string> post,
        BrowserBridgeOptions? options = null)
    {
        _settings = settings;
        _cache = cache;
        _queue = queue;
        _previews = previews;
        _host = host;
        _post = post;
        _options = options ?? new BrowserBridgeOptions();
        _dispatch = _options.Dispatch ?? (action => action());
        _save = _options.Save ?? (s => SaveMerged(s));
        _loadCatalog = _options.LoadCatalog ?? ((s, ct) => ClipLibrary.LoadAsync(s, ct));
        _refresh = _options.RefreshCatalogs ?? ((p, ct) => new CatalogRefresher().RefreshAsync(null, p, ct));
        _updateChecker = _options.UpdateChecker;
        _time = _options.Time ?? TimeProvider.System;

        // PinManager saves after every change; route that through the debounce like every other edit.
        Pins = new PinManager(settings, cache, queue, _ => ScheduleSave());

        _deltaTimer = _time.CreateTimer(_ => Marshal(OnDeltaTimer), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _saveTimer = _time.CreateTimer(_ => Marshal(FlushSave), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _cache.Changed += OnCacheChanged;
        _queue.JobChanged += OnJobChanged;
        _queue.Progress += OnJobChanged;
        _queue.OwnershipChanged += OnOwnershipChanged;
        _previews.PreviewUpdated += OnPreviewUpdated;
    }

    public PinManager Pins { get; }

    public AppSettings Settings => _settings;

    public IReadOnlyList<ClipInfo> Catalog => _catalog;

    public bool PageReady => _pageReady;

    /// <summary>The most recent background operation a page message started (catalogue reload or refresh); for tests.</summary>
    public Task Background { get; private set; } = Task.CompletedTask;

    // ---- lifecycle ---------------------------------------------------------------------------

    /// <summary>
    /// Loads the catalogue, migrates/prepares settings against it (and saves at once if that
    /// changed anything), syncs pins, starts previews, and — if the page is ready — sends the
    /// catalogue. Call once after construction; the page can send <c>ready</c> before or after.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCatalogAsync(initial: true);

        var due = _options.RefreshDue ?? (() => new CatalogRefresher().IsDue());
        if (due())
        {
            Log.Info("Browser: catalogues are over 7 days old; refreshing in the background");
            Background = RefreshCatalogsAsync();
        }

        // Never for /s, /p, /now or /w: _updateChecker is only ever non-null here when
        // BrowserForm constructed one, which it only does for the clip browser. The toggle
        // (default on) governs this automatic path only; a manual "Check now" bypasses it.
        if (_updateChecker is not null && _settings.AutoCheckForUpdates)
            _ = CheckForUpdatesAsync(force: false);
    }

    /// <summary>The page is navigating away (reload, crash). Nothing is posted until its next <c>ready</c>.</summary>
    public void PageUnloaded()
    {
        _pageReady = false;
        _pendingImport = null; // the dialog that would answer it is gone
    }

    // ---- page → host -------------------------------------------------------------------------

    /// <summary>
    /// One message from the page (<c>WebMessageReceived</c>'s <c>WebMessageAsJson</c>). Never
    /// throws: a malformed or unknown message is logged and ignored.
    /// </summary>
    public void OnPageMessage(string json)
    {
        if (_disposed)
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;

            // postMessage("...") of a pre-serialised string arrives as a JSON string literal.
            if (message.ValueKind == JsonValueKind.String)
            {
                OnPageMessage(message.GetString() ?? "");
                return;
            }

            if (message.ValueKind != JsonValueKind.Object || Str(message, "type") is not { } type)
            {
                Log.Warn("Browser: page message without a type ignored");
                return;
            }

            Handle(type, message);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Browser: malformed page message ignored: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("Browser: page message handler failed", ex);
            Notice("error", "Something went wrong: " + ex.Message);
        }
    }

    private void Handle(string type, JsonElement m)
    {
        switch (type)
        {
            case "ready":
                OnReady();
                break;

            case "setRotation":
                SetRotation(Keys(m), Bool(m, "value") ?? true);
                break;

            case "setPinned":
                SetPinned(Keys(m), Bool(m, "value") ?? true);
                break;

            case "setAutoAdd":
                SetAutoAdd(Str(m, "sourceId"), Bool(m, "value") ?? true, Bool(m, "includeExisting") ?? false);
                break;

            case "selectClip":
                SelectClip(Str(m, "key"));
                break;

            case "panelRect":
                _host.SetPanelRect(new PanelRect(
                    Number(m, "x") ?? 0, Number(m, "y") ?? 0,
                    Number(m, "width") ?? 0, Number(m, "height") ?? 0,
                    Bool(m, "visible") ?? true));
                break;

            case "requestPreviews":
                RequestPreviews(Keys(m), Str(m, "kinds"));
                break;

            case "cancelPreviews":
                _previews.Cancel(Keys(m));
                break;

            case "saveSettings":
                if (m.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.Object)
                    ApplyPatch(patch);
                else
                    Log.Warn("Browser: saveSettings without a patch object ignored");
                break;

            case "refreshCatalogs":
                Background = RefreshCatalogsAsync();
                break;

            case "startNow":
                FlushSave(); // the saver about to start reads settings.json
                if (!_host.StartNow())
                    Notice("warning", "Windows did not accept the request to start the screen saver.");
                break;

            case "purgeCache":
                Background = PurgeCacheAsync();
                break;

            case "retryDownloads":
                _queue.RetryFailed();
                break;

            case "pickFolder":
                PickFolder(Str(m, "purpose"));
                break;

            case "openFolder":
                OpenFolder(Str(m, "which"));
                break;

            case "exportSettings":
                ExportSettings();
                break;

            case "importSettings":
                ImportSettings();
                break;

            case "confirmImport":
                ConfirmImport(Bool(m, "apply") ?? false, Bool(m, "includeMachine") ?? false);
                break;

            case "log":
                PageLog(Str(m, "level"), Str(m, "text"));
                break;

            case "checkUpdates":
                Background = CheckForUpdatesAsync(force: true);
                break;

            case "installUpdate":
                Background = InstallUpdateAsync();
                break;

            default:
                Log.Warn($"Browser: unknown page message '{type}' ignored");
                break;
        }
    }

    private void OnReady()
    {
        _pageReady = true;
        _sent.Clear();
        _pendingProgress.Clear();
        Log.Info("Browser: page ready");

        PostSettings();
        if (_catalogLoaded)
            SendCatalogAndFullState(); // ends with cacheStats
        else
            PostCacheStats();

        // A reload (or the page simply arriving after an automatic check already finished):
        // replay the last result rather than leaving the About tab blank until the next one.
        if (_lastUpdateStatus is { } status)
            Post(UpdateStatusMessage.From(status));
    }

    private void SetRotation(IReadOnlyList<string> keys, bool value)
    {
        if (keys.Count == 0)
            return;

        if (Rotation.SetInRotation(_settings, keys, value) > 0)
            ScheduleSave();
        MarkDirty(keys);
        FlushState();
    }

    private void SetPinned(IReadOnlyList<string> keys, bool value)
    {
        if (keys.Count == 0)
            return;

        if (value)
        {
            var check = Pins.CanPin(keys);
            if (!check.Ok)
            {
                Log.Info($"Browser: refused to keep {check.Added.Count} clip(s) offline: " +
                         $"{VideoCache.FormatSize(check.RequiredBytes)} over a {VideoCache.FormatSize(check.BudgetBytes)} limit");
                Post(PinRefusedMessage.From(keys, check, _settings.CacheBudgetGb));
                return;
            }
            Pins.Pin(keys);
        }
        else
        {
            Pins.Unpin(keys);
        }

        MarkDirty(keys);
        _statsDirty = true;
        FlushState();
    }

    private void SetAutoAdd(string? sourceId, bool value, bool includeExisting)
    {
        // Exact ids only: Rotation.Normalize maps anything unknown to aerial-manifests (it
        // treats it as a vendor feed), which is right for catalogue data, wrong for a typo.
        if (sourceId is null || !Rotation.SourceIds.Contains(sourceId))
        {
            Log.Warn($"Browser: setAutoAdd for unknown source '{sourceId}' ignored");
            return;
        }

        var changed = Rotation.SetAutoAdd(_settings, sourceId, value);
        if (includeExisting)
        {
            var keys = Rotation.KeysOf(_catalog, sourceId).ToList();
            changed |= Rotation.SetInRotation(_settings, keys, value) > 0;
            MarkDirty(keys);
        }

        if (changed)
            ScheduleSave();
        PostSettings();
        FlushState();
    }

    private void SelectClip(string? key)
    {
        _selectedKey = key;
        if (key is null || !_byKey.TryGetValue(key, out var info) ||
            ClipResolver.PlayableClip(info, _settings.Quality) is not { } clip)
        {
            _host.ShowClip(null, null);
            return;
        }

        // From the cache when it is there, so flicking through the grid does not pull hundreds
        // of megabytes per click; otherwise mpv streams it (escaped: Clip.PlayableUrl). Not
        // recorded as a play: previewing would reorder LRU and the page's "last played" sort.
        string url;
        try
        {
            url = _cache.Resolve(clip, recordPlay: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser: cache lookup for {clip} failed: {ex.Message}");
            url = clip.PlayableUrl;
        }
        _host.ShowClip(clip, url);
    }

    private void RequestPreviews(IReadOnlyList<string> keys, string? kinds)
    {
        if (keys.Count == 0)
            return;

        var kind = Enum.TryParse<PreviewKind>(kinds, ignoreCase: true, out var parsed) && parsed != PreviewKind.None
            ? parsed
            : PreviewKind.Both;

        // Whatever is already decided (Ready, Placeholder, Failed, Unavailable…) is answered now;
        // Pending ones arrive through PreviewUpdated when a worker finishes them.
        foreach (var state in _previews.Request(keys, kind))
        {
            if (state.Status != PreviewStatus.Pending)
                Post(PreviewReadyMessage.From(state));
        }
    }

    private async Task PurgeCacheAsync()
    {
        try
        {
            var removed = await Task.Run(_cache.PurgeUnpinned).ConfigureAwait(false);
            Marshal(() => Notice("info", $"Deleted {removed:N0} cached clip(s). Clips kept offline were not touched."));
        }
        catch (Exception ex)
        {
            Log.Error("Browser: purge failed", ex);
            Marshal(() => Notice("error", "Could not purge the cache: " + ex.Message));
        }
    }

    private void PickFolder(string? purpose)
    {
        switch (purpose)
        {
            case "localFolder":
            {
                var folder = _host.PickFolder("Choose a folder of video files", null);
                if (folder is null || _settings.LocalFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    return;
                _settings.LocalFolders = [.. _settings.LocalFolders, folder];
                ScheduleSave();
                PostSettings();
                Background = LoadCatalogAsync(initial: false);
                break;
            }

            case "cacheDirectory":
            {
                var folder = _host.PickFolder("Choose where to keep cached clips", _settings.ResolvedCacheDirectory);
                if (folder is null)
                    return;
                SetCacheDirectory(folder);
                PostSettings();
                break;
            }

            default:
                Log.Warn($"Browser: pickFolder with unknown purpose '{purpose}' ignored");
                break;
        }
    }

    private void OpenFolder(string? which)
    {
        var path = which switch
        {
            "cache" => _cache.Root,
            "logs" or "data" => AppPaths.Root,
            "previews" => _previews.Directory,
            _ => null,
        };
        if (path is null)
            Log.Warn($"Browser: openFolder '{which}' ignored");
        else
            _host.OpenFolder(path);
    }

    private static void PageLog(string? level, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (text.Length > 2000)
            text = text[..2000] + "…";

        switch (level)
        {
            case "error":
                Log.Error("Page: " + text);
                break;
            case "warn" or "warning":
                Log.Warn("Page: " + text);
                break;
            default:
                Log.Info("Page: " + text);
                break;
        }
    }

    // ---- export / import settings ------------------------------------------------------------

    private const string SettingsFileFilter = "Win4KSS settings (*.win4kss.json)|*.win4kss.json|JSON files (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>
    /// <c>exportSettings</c>: the host's Save dialog, then the readable file
    /// (<see cref="SettingsPortability"/>). Answered with a notice.
    /// </summary>
    private void ExportSettings()
    {
        var suggested = $"win4kss-settings-{_time.GetUtcNow():yyyy-MM-dd}{SettingsPortability.FileExtension}";
        var path = _host.PickSaveFile("Export settings", suggested, SettingsFileFilter);
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            path += SettingsPortability.FileExtension;

        try
        {
            var model = SettingsPortability.Build(_settings, _catalog, SettingsCli.ToPortable(CurrentShortcuts),
                typeof(BrowserBridge).Assembly.GetName().Version?.ToString(), _time.GetUtcNow().UtcDateTime);
            SettingsPortability.WriteFile(model, path);
            Log.Info($"Browser: exported settings to {path}");
            Notice("info", $"Exported settings to {path}: {PortableSettings.KeysOf(model.Rotation).Count:N0} clips in the rotation, " +
                           $"{PortableSettings.KeysOf(model.KeptOffline).Count:N0} kept offline.");
        }
        catch (Exception ex)
        {
            Log.Error("Browser: settings export failed", ex);
            Notice("error", "Could not export settings: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>importSettings</c>: the host's Open dialog, validate, then an <c>importPreview</c> for the
    /// page to confirm. Nothing changes until <c>confirmImport</c> arrives.
    /// </summary>
    private void ImportSettings()
    {
        _pendingImport = null;
        if (!_catalogLoaded)
        {
            Notice("warning", "The clip catalogue is still loading. Try importing again in a moment.");
            return;
        }

        var path = _host.PickOpenFile("Import settings", SettingsFileFilter);
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!SettingsPortability.TryParseFile(path, out var file, out var error))
        {
            Log.Warn($"Browser: settings import rejected ({path}): {error}");
            Notice("error", error ?? "That file could not be read.");
            return;
        }

        var plan = SettingsPortability.Plan(_settings, file!, _catalog, SettingsCli.ToPortable(CurrentShortcuts));

        // Would the file's kept-offline clips fit the (file's) cache limit?
        var wanted = PortableSettings.KeysOf(file!.KeptOffline).Where(k => Pins.TargetClip(k) is not null).ToList();
        var requiredBytes = wanted.Sum(Pins.EstimatedBytes);
        var budgetGb = file.CacheBudgetGb is { } gb && double.IsFinite(gb) ? Math.Clamp(gb, 1, 100_000) : _settings.CacheBudgetGb;

        _pendingImport = file;
        Log.Info($"Browser: settings import from {path} awaiting confirmation");
        Post(new ImportPreviewMessage(
            Path.GetFileName(path), file.AppVersion, file.ExportedUtc, file.FormatVersion,
            file.FormatVersion > SettingsPortability.FormatVersion,
            new ImportCountDto(plan.RotationBefore, plan.RotationAfter, plan.RotationAdded, plan.RotationRemoved, plan.RotationNotInCatalogue),
            new ImportCountDto(plan.PinsBefore, plan.PinsAfter, 0, 0, plan.PinsNotInCatalogue),
            plan.Changes, plan.HasMachine, plan.MachineChanges,
            requiredBytes <= budgetGb * GiB, Math.Ceiling(requiredBytes / GiB * 10) / 10, budgetGb));
    }

    /// <summary>
    /// <c>confirmImport</c>: apply (or drop) the file validated by the last <c>importSettings</c>.
    /// Backs settings.json up to settings.before-import.json first, then goes through the same
    /// primitives as <c>saveSettings</c>/<c>setRotation</c>/<c>setPinned</c> so the page updates live.
    /// </summary>
    private void ConfirmImport(bool apply, bool includeMachine)
    {
        var file = _pendingImport;
        _pendingImport = null;
        if (!apply)
            return;
        if (file is null)
        {
            Notice("warning", "There is no import waiting to be applied. Choose Import settings… again.");
            return;
        }

        // The backup must be what is live right now, not what the debounce last wrote.
        ScheduleSave();
        FlushSave();
        var backedUp = SettingsPortability.BackUp();

        var applied = SettingsPortability.Apply(_settings, file, includeMachine);
        var notes = new List<string>();

        // Kept offline goes through PinManager and the budget. Over budget: skip the pins, keep
        // everything else, and offer the standard "raise the limit" prompt.
        if (applied.PinKeys is { } pinKeys)
        {
            var resolvable = pinKeys.Where(k => Pins.TargetClip(k) is not null).ToHashSet(StringComparer.Ordinal);
            var required = resolvable.Sum(Pins.EstimatedBytes);
            if (required <= _settings.CacheBudgetBytes)
            {
                Pins.Unpin(_settings.KeptOffline.Where(k => !resolvable.Contains(k)).ToList());
                Pins.Pin(resolvable);
                if (pinKeys.Count > resolvable.Count)
                    notes.Add($"{pinKeys.Count - resolvable.Count:N0} kept-offline clip(s) are not in this catalogue and were skipped");
            }
            else
            {
                var missing = resolvable.Where(k => !_settings.KeptOffline.Contains(k)).ToList();
                var check = new PinCheck(false, required, _settings.CacheBudgetBytes, missing, []);
                Log.Info($"Browser: import skipped pins: {VideoCache.FormatSize(required)} over a {VideoCache.FormatSize(check.BudgetBytes)} limit");
                notes.Add("kept-offline clips were not changed: they do not fit the cache limit");
                if (missing.Count > 0)
                    Post(PinRefusedMessage.From(missing, check, _settings.CacheBudgetGb));
            }
        }

        if (file.Shortcuts is { } wantedShortcuts)
        {
            var current = CurrentShortcuts;
            var target = SettingsCli.Merge(current, wantedShortcuts);
            if (target != current)
            {
                if (!_host.ApplyShortcuts(target))
                    notes.Add("the shortcuts could not be updated (see win4k.log)");
                _shortcuts = _host.ReadShortcuts();
            }
        }

        if (applied.QualityChanged)
            Pins.SyncPins();
        if (applied.BudgetLowered)
            EvictInBackground();
        if (applied.CacheDirectoryChanged)
            Notice("info", "The imported cache folder is used the next time the browser or screen saver starts. Existing clips are not moved.");
        if (applied.FfmpegPathChanged)
            Notice("info", "The ffmpeg path takes effect the next time the browser opens.");

        ScheduleSave();
        FlushSave(); // other processes (the saver) read the rotation from disk
        _allDirty = true;
        _statsDirty = true;
        PostSettings();
        FlushState();

        if (applied.LocalFoldersChanged)
            Background = LoadCatalogAsync(initial: false);

        var unknown = _settings.RotationKeys.Count(k => !_byKey.ContainsKey(k));
        var text2 = $"Imported settings: {_settings.RotationKeys.Count:N0} clips in the rotation" +
                    (unknown > 0 ? $" ({unknown:N0} not in this catalogue yet; they stay and join once it lists them)" : "") +
                    $", {_settings.KeptOffline.Count:N0} kept offline." +
                    (notes.Count > 0 ? " Note: " + string.Join("; ", notes) + "." : "") +
                    (backedUp ? $" Your previous settings are in {SettingsPortability.BackupFileName}." : "");
        Log.Info("Browser: " + text2);
        Notice(notes.Count > 0 ? "warning" : "info", text2);
    }

    // ---- updates -------------------------------------------------------------------------------

    private static string AppVersionString() => typeof(BrowserBridge).Assembly.GetName().Version?.ToString() ?? "";

    /// <summary>
    /// Runs one update check (<see cref="_updateChecker"/>'s own 24h throttle applies unless
    /// <paramref name="force"/>), persists the outcome into <see cref="AppSettings"/>, and posts
    /// <c>updateStatus</c>. Concurrent callers (an automatic check still running when the user
    /// clicks "Check now") share the one in-flight task rather than racing two network calls.
    /// Never throws except for the bridge's own shutdown cancellation.
    /// </summary>
    private Task<UpdateCheckOutcome> CheckForUpdatesAsync(bool force)
    {
        if (_updateCheckInFlight is { IsCompleted: false } running)
            return running;

        var task = RunUpdateCheckAsync(force);
        _updateCheckInFlight = task;
        return task;
    }

    private async Task<UpdateCheckOutcome> RunUpdateCheckAsync(bool force)
    {
        try
        {
            var outcome = await _updateChecker!.CheckAsync(AppVersionString(), force, _shutdown.Token).ConfigureAwait(false);
            await RunOnBridgeThread(() =>
            {
                _lastUpdateStatus = outcome.Result;
                // Only replace a previously-known release with a fresh one; a throttled check
                // (Release null) must not blank out an install an earlier check already set up.
                if (outcome.Release is not null)
                    _lastRelease = outcome.Release;
                ScheduleSave(); // persists LastUpdateCheckUtc/LatestVersion/Available/Notes
                Post(UpdateStatusMessage.From(outcome.Result));
            }).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            _updateCheckInFlight = null;
        }
    }

    /// <summary>
    /// <c>installUpdate</c>: refreshes the check if nothing live has been fetched yet this
    /// session, then downloads, verifies, and — only on success — launches the installer and
    /// starts the hand-off shutdown (<see cref="IBrowserHost.PrepareForInstallerHandoff"/>).
    /// Never launches anything that did not verify. A second <c>installUpdate</c> while one is
    /// already running is ignored rather than racing two downloads to the same temp area.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        if (_updateChecker is null)
        {
            Marshal(() => Notice("error", "Updates are not available in this build."));
            return;
        }

        if (_installing)
            return;
        _installing = true;

        try
        {
            if (_lastRelease is null)
                await CheckForUpdatesAsync(force: true).ConfigureAwait(false);

            var release = _lastRelease;
            if (release is null || _lastUpdateStatus is not { Available: true })
            {
                Marshal(() => Notice("warning", "No update is available to install."));
                return;
            }

            var installer = new UpdateInstaller();
            var progress = new Progress<UpdateDownloadProgress>(p =>
                Marshal(() => Post(UpdateInstallProgressMessage.Downloading(p))));

            var result = await installer.DownloadAndVerifyAsync(
                release,
                onStage: stage => Marshal(() => Post(UpdateInstallProgressMessage.ForStage(stage))),
                onProgress: progress,
                cancellationToken: _shutdown.Token).ConfigureAwait(false);

            await RunOnBridgeThread(() =>
            {
                if (!result.Ok)
                {
                    var message = result.Message ?? "The update could not be verified and will not be installed.";
                    Log.Warn($"Browser: update install refused: {result.Failure} — {message}");
                    Post(UpdateInstallProgressMessage.Failed(message));
                    Notice("error", message);
                    return;
                }

                try
                {
                    Post(UpdateInstallProgressMessage.ForStage("Launching"));
                    installer.LaunchInstaller(result.FilePath!);
                    Post(UpdateInstallProgressMessage.ForStage("Done"));
                }
                catch (Exception ex)
                {
                    Log.Error("Browser: could not launch the downloaded installer", ex);
                    Post(UpdateInstallProgressMessage.Failed("Could not start the installer: " + ex.Message));
                    Notice("error", "Could not start the installer: " + ex.Message);
                    return;
                }

                // See the hand-off hazard on IBrowserHost.PrepareForInstallerHandoff: the
                // installer we just launched will, via install-scr.ps1's Stop-NonSaverInstances,
                // force-kill this very process shortly if it is still running — so it closes
                // itself cleanly right now instead of waiting to be killed.
                _host.PrepareForInstallerHandoff();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Browser: update install failed", ex);
            Marshal(() => Notice("error", "Could not install the update: " + ex.Message));
        }
        finally
        {
            _installing = false;
        }
    }

    // ---- settings ----------------------------------------------------------------------------

    /// <summary>
    /// Applies a partial <c>settings.values</c> object. Unknown and read-only fields are ignored;
    /// a value of the wrong type is skipped. The page gets a fresh <c>settings</c> back, so it
    /// always shows what was actually stored (normalised, clamped).
    /// </summary>
    private void ApplyPatch(JsonElement patch)
    {
        var reload = false;
        var qualityChanged = false;
        ShortcutOptions? shortcuts = null;

        foreach (var property in patch.EnumerateObject())
        {
            var v = property.Value;
            switch (property.Name)
            {
                case "quality" when Enum.TryParse<VideoQuality>(v.ValueKind == JsonValueKind.String ? v.GetString() : null, true, out var quality):
                    qualityChanged |= _settings.Quality != quality;
                    _settings.Quality = quality;
                    break;

                case "multiMonitorMode" when Enum.TryParse<MultiMonitorMode>(v.ValueKind == JsonValueKind.String ? v.GetString() : null, true, out var mode):
                    _settings.MultiMonitorMode = mode;
                    break;

                case "shuffle" when AsBool(v) is { } shuffle:
                    _settings.Shuffle = shuffle;
                    break;

                case "useTimeOfDay" when AsBool(v) is { } timeOfDay:
                    _settings.UseTimeOfDay = timeOfDay;
                    break;

                case "autoCheckForUpdates" when AsBool(v) is { } autoCheck:
                    _settings.AutoCheckForUpdates = autoCheck;
                    break;

                case "cacheEnabled" when AsBool(v) is { } enabled:
                    _settings.CacheEnabled = enabled;
                    break;

                case "cacheBudgetGb" when v.ValueKind == JsonValueKind.Number:
                    SetBudget(v.GetDouble());
                    break;

                case "cacheDirectory" when v.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    SetCacheDirectory(v.GetString());
                    break;

                case "localFolders" when v.ValueKind == JsonValueKind.Array:
                {
                    var folders = v.EnumerateArray()
                                   .Where(e => e.ValueKind == JsonValueKind.String)
                                   .Select(e => e.GetString()!.Trim())
                                   .Where(f => f.Length > 0)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList();
                    reload |= !folders.SequenceEqual(_settings.LocalFolders, StringComparer.OrdinalIgnoreCase);
                    _settings.LocalFolders = folders;
                    break;
                }

                case "ffmpegPath" when v.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                {
                    var path = string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim();
                    if (!string.Equals(path, _settings.FfmpegPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.FfmpegPath = path;
                        Notice("info", "The ffmpeg path takes effect the next time the browser opens.");
                    }
                    break;
                }

                case "autoAddSources" when v.ValueKind == JsonValueKind.Array:
                {
                    var wanted = v.EnumerateArray()
                                  .Where(e => e.ValueKind == JsonValueKind.String)
                                  .Select(e => e.GetString()!)
                                  .Where(Rotation.SourceIds.Contains)
                                  .ToHashSet(StringComparer.Ordinal);
                    foreach (var source in Rotation.SourceIds)
                        Rotation.SetAutoAdd(_settings, source, wanted.Contains(source));
                    break;
                }

                case "shortcutDesktop" when AsBool(v) is { } desktop:
                    shortcuts = (shortcuts ?? CurrentShortcuts) with { Desktop = desktop };
                    break;

                case "shortcutStartMenu" when AsBool(v) is { } startMenu:
                    shortcuts = (shortcuts ?? CurrentShortcuts) with { StartMenu = startMenu };
                    break;

                case "hotkey" when v.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                {
                    var hotkey = ShortcutManager.NormalizeHotkey(v.GetString());
                    if (hotkey is null)
                        Notice("warning", $"\"{v.GetString()}\" is not a usable hotkey: it needs Ctrl, Alt or Shift plus a key.");
                    else
                        shortcuts = (shortcuts ?? CurrentShortcuts) with { Hotkey = hotkey.Length == 0 ? null : hotkey };
                    break;
                }

                case "previewCapabilities":
                    break; // read-only

                default:
                    Log.Warn($"Browser: saveSettings field '{property.Name}' ignored (unknown, read-only or wrong type)");
                    break;
            }
        }

        if (shortcuts is not null && shortcuts != CurrentShortcuts)
        {
            if (!_host.ApplyShortcuts(shortcuts))
                Notice("warning", "Could not update the Start Screen Saver shortcuts; see win4k.log.");
            _shortcuts = _host.ReadShortcuts();
        }

        if (qualityChanged)
        {
            // Pins re-download at the new quality; cached/not-cached is per rendition.
            Pins.SyncPins();
            _allDirty = true;
        }

        ScheduleSave();
        PostSettings();
        _statsDirty = true;
        FlushState();

        if (reload)
            Background = LoadCatalogAsync(initial: false);
    }

    private ShortcutOptions CurrentShortcuts => _shortcuts ??= _host.ReadShortcuts();

    private void SetBudget(double gb)
    {
        if (!double.IsFinite(gb))
            return;

        gb = Math.Clamp(gb, 1, 100_000);
        var lowered = gb < _settings.CacheBudgetGb;
        _settings.CacheBudgetGb = gb;

        // A lower limit should free the space now rather than at the next download.
        if (lowered)
            EvictInBackground();
    }

    private void EvictInBackground()
    {
        Task.Run(() =>
        {
            try
            {
                _cache.EvictToBudget();
            }
            catch (Exception ex)
            {
                Log.Error("Browser: eviction after lowering the budget failed", ex);
            }
        });
    }

    private void SetCacheDirectory(string? folder)
    {
        var value = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        var current = string.IsNullOrWhiteSpace(_settings.CacheDirectory) ? null : _settings.CacheDirectory;
        if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.CacheDirectory = value;
        ScheduleSave();

        // VideoCache's root is fixed for its life (the cross-process mutex is named after it).
        Notice("info", "The new cache folder is used the next time the browser or screen saver starts. Existing clips are not moved.");
    }

    private void PostSettings()
    {
        if (!_pageReady)
            return;

        var shortcuts = CurrentShortcuts;
        var capabilities = _previews.Capabilities;
        var values = new SettingsValues(
            _settings.Quality,
            _settings.MultiMonitorMode,
            _settings.Shuffle,
            _settings.UseTimeOfDay,
            _settings.CacheEnabled,
            _settings.ResolvedCacheDirectory,
            _settings.CacheBudgetGb,
            _settings.LocalFolders.ToList(),
            _settings.FfmpegPath,
            _settings.AutoAddSources.Order(StringComparer.Ordinal).ToList(),
            Rotation.SourceIds.ToList(),
            new PreviewCapabilitiesDto(capabilities.FfmpegAvailable, capabilities.HoverClips, capabilities.FfmpegPath),
            shortcuts.Desktop,
            shortcuts.StartMenu,
            shortcuts.Hotkey,
            _settings.AutoCheckForUpdates);

        var sources = Rotation.SourceIds
            .Select(id => new SourceDto(id, SourceDisplayName(id), _sourceCounts.GetValueOrDefault(id), _settings.AutoAddSources.Contains(id)))
            .ToList();

        Post(new SettingsMessage(values, sources, About()));
    }

    private AboutDto About()
    {
        string mpv;
        try
        {
            mpv = MpvPlayer.ApiVersionString;
        }
        catch (Exception ex)
        {
            mpv = "not loaded — " + ex.Message;
        }

        return new AboutDto(
            AppVersionString(),
            mpv,
            AppPaths.Root,
            AppPaths.SettingsFile,
            AppPaths.LogFile,
            Path.Combine(AppPaths.Root, "mpv.log"),
            _cache.Root,
            _previews.Directory);
    }

    public static string SourceDisplayName(string sourceId) => sourceId switch
    {
        "apple" => "Apple TV aerials",
        "aerial-manifests" => "Aerial manifests (tvOS 26, Fire TV, community)",
        "archive-org" => "Internet Archive",
        "nasa" => "NASA",
        "local" => "Local folders",
        _ => TagBuilder.SourceName(sourceId),
    };

    /// <summary>Writes pending settings now, if there are any.</summary>
    public void FlushSave()
    {
        if (!_savePending)
            return;
        _savePending = false;
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SaveNow();
    }

    private void ScheduleSave()
    {
        _savePending = true;
        if (!_disposed)
            _saveTimer.Change(_options.SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    private void SaveNow()
    {
        var failedBefore = _settings.FailedClips.Count(kv => kv.Value >= ClipHealth.UnhealthyAfterSessions);
        try
        {
            _save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Browser: could not save settings", ex);
            Notice("error", "Could not save settings: " + ex.Message);
        }

        // SaveMerged may have pulled in failures the saver recorded meanwhile.
        if (_settings.FailedClips.Count(kv => kv.Value >= ClipHealth.UnhealthyAfterSessions) != failedBefore)
        {
            _allDirty = true;
            ScheduleDelta();
        }
    }

    /// <summary>
    /// The default save. The saver may have written settings.json since the browser loaded it —
    /// a clip failure (<see cref="AppSettings.FailedClips"/>, read-modify-written by
    /// <see cref="ClipHealth"/>) or newly known keys — so those are merged in from disk first
    /// rather than overwritten by this process's older copy. Everything else is the browser's to
    /// decide: it is the only editor.
    /// </summary>
    public static void SaveMerged(AppSettings settings, string? path = null)
    {
        var file = path ?? AppPaths.SettingsFile;
        if (File.Exists(file))
        {
            var disk = AppSettings.Load(file);
            foreach (var (key, count) in disk.FailedClips)
                settings.FailedClips[key] = Math.Max(count, settings.FailedClips.GetValueOrDefault(key));
            settings.KnownKeys.UnionWith(disk.KnownKeys);
        }
        settings.Save(path);
    }

    // ---- catalogue ---------------------------------------------------------------------------

    private async Task LoadCatalogAsync(bool initial)
    {
        IReadOnlyList<ClipInfo> catalogue;
        try
        {
            catalogue = await _loadCatalog(_settings, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Browser: catalogue load failed", ex);
            catalogue = [];
        }

        await RunOnBridgeThread(() => ApplyCatalog(catalogue, initial)).ConfigureAwait(false);
    }

    private void ApplyCatalog(IReadOnlyList<ClipInfo> catalogue, bool initial)
    {
        if (_disposed)
            return;

        // Migrate v1 settings, and merge clips new since the last run into the rotation. Saved
        // at once: other processes (the saver) read the rotation from disk.
        if (SettingsMigrator.Prepare(_settings, catalogue))
        {
            _savePending = false;
            SaveNow();
        }

        _catalog = catalogue;
        _byKey = new Dictionary<string, ClipInfo>(StringComparer.Ordinal);
        foreach (var clip in catalogue)
            _byKey.TryAdd(clip.Key, clip);

        _sourceCounts = Rotation.SourceIds.ToDictionary(
            id => id, id => Rotation.KeysOf(catalogue, id).Count(), StringComparer.Ordinal);

        _catalogJson = BridgeJson.Serialize(new CatalogMessage(catalogue.Select(ClipDto.From).ToList()));
        _catalogLoaded = true;

        Pins.SetCatalog(catalogue);
        var pins = Pins.SyncPins();
        if (!pins.Ok)
            Log.Warn($"Browser: kept-offline clips need {pins.RequiredGb} GB, over the {_settings.CacheBudgetGb} GB limit");

        _previews.SetCatalog(catalogue);
        if (!_previewsStarted)
        {
            _previewsStarted = true;
            _previews.Start(_options.PreviewBackgroundFill);
        }

        Log.Info($"Browser: catalogue {(initial ? "loaded" : "reloaded")}, {catalogue.Count} clips, " +
                 $"{_settings.RotationKeys.Count} in rotation");

        if (_selectedKey is not null && !_byKey.ContainsKey(_selectedKey))
            SelectClip(null);

        if (_pageReady)
        {
            SendCatalogAndFullState();
            PostSettings(); // source clip counts
        }
    }

    private void SendCatalogAndFullState()
    {
        if (_catalogJson is null)
            return;
        _post(_catalogJson);
        _sent.Clear();
        _allDirty = true;
        _statsDirty = true;
        FlushState();
    }

    /// <summary>
    /// Re-harvests every auto catalogue, streaming refreshProgress, then reloads the catalogue
    /// (re-running settings preparation, so new clips from auto-add sources join the rotation)
    /// and sends a fresh catalog. A second request while one runs is ignored.
    /// </summary>
    public async Task RefreshCatalogsAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;

        try
        {
            var progress = new MarshalledProgress(this);
            var report = await _refresh(progress, _shutdown.Token).ConfigureAwait(false);
            await RunOnBridgeThread(() =>
            {
                FlushProgress();
                Post(RefreshProgressMessage.Finished(report));
            }).ConfigureAwait(false);

            await LoadCatalogAsync(initial: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Browser: catalogue refresh failed", ex);
            await RunOnBridgeThread(() => Post(RefreshProgressMessage.Failed(ex.Message))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnBridgeThread(() => _refreshing = false).ConfigureAwait(false);
        }
    }

    private sealed class MarshalledProgress(BrowserBridge bridge) : IProgress<CatalogRefreshProgress>
    {
        // Harvesters report per item (1,000+ for archive.org); keep the latest per source and
        // let the delta timer send them at its ~4 Hz.
        public void Report(CatalogRefreshProgress value) => bridge.Marshal(() =>
        {
            bridge._pendingProgress[value.SourceId] = value;
            bridge.ScheduleDelta();
        });
    }

    private void FlushProgress()
    {
        if (_pendingProgress.Count == 0)
            return;
        var pending = _pendingProgress.Values.ToList();
        _pendingProgress.Clear();
        foreach (var progress in pending)
            Post(RefreshProgressMessage.Progress(progress));
    }

    // ---- state -------------------------------------------------------------------------------

    /// <summary>What the page should show for one clip right now.</summary>
    internal ClipStateDto StateOf(ClipInfo info, PlayHistory? history = null)
    {
        var cached = false;
        DateTime? lastPlayed = null;
        if (!info.IsLocal)
        {
            history ??= PlayHistory.From(_cache);
            var clip = ClipResolver.PlayableClip(info, _settings.Quality);
            if (clip is not null)
                cached = _cache.IsCached(clip.Url);
            lastPlayed = history.LastPlayed(info.Key, clip is null ? null : VideoCache.KeyFor(clip.Url));
        }

        return new ClipStateDto(
            Rotation.IsInRotation(_settings, info.Key),
            Pins.IsPinned(info.Key),
            cached,
            DownloadDto.From(_queue.Get(info.Key)),
            ClipHealth.IsUnhealthy(_settings, info.Key) ? ClipStateDto.Unhealthy : ClipStateDto.Ok,
            TagBuilder.IsNew(info, _time.GetUtcNow().UtcDateTime),
            lastPlayed);
    }

    /// <summary>
    /// When each clip was last played, from the cache index (<see cref="CacheEntry.LastPlayedUtc"/>,
    /// which is also stamped when a file is stored). Only cached clips have one: the index is
    /// the only play history there is. Read once per flush, not once per clip.
    /// </summary>
    internal sealed class PlayHistory
    {
        private readonly Dictionary<string, DateTime> _byClip = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _byFile = new(StringComparer.OrdinalIgnoreCase);

        public static PlayHistory From(VideoCache cache)
        {
            var history = new PlayHistory();
            foreach (var entry in cache.Entries())
            {
                if (entry.LastPlayedUtc == default)
                    continue;
                var played = entry.LastPlayedUtc.Kind switch
                {
                    DateTimeKind.Utc => entry.LastPlayedUtc,
                    DateTimeKind.Local => entry.LastPlayedUtc.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(entry.LastPlayedUtc, DateTimeKind.Utc),
                };
                // Files cached before index v2 have no clip key; they match by file name instead.
                if (!string.IsNullOrEmpty(entry.ClipKey))
                    KeepLatest(history._byClip, entry.ClipKey, played);
                KeepLatest(history._byFile, entry.Key, played);
            }
            return history;
        }

        /// <param name="fileKey">The current rendition's cache file name, if it has one.</param>
        public DateTime? LastPlayed(string clipKey, string? fileKey)
        {
            DateTime? latest = _byClip.TryGetValue(clipKey, out var byClip) ? byClip : null;
            if (fileKey is not null && _byFile.TryGetValue(fileKey, out var byFile) && (latest is null || byFile > latest))
                latest = byFile;
            return latest;
        }

        private static void KeepLatest(Dictionary<string, DateTime> map, string key, DateTime value)
        {
            if (!map.TryGetValue(key, out var existing) || value > existing)
                map[key] = value;
        }
    }

    private void MarkDirty(IEnumerable<string> keys) => _dirty.UnionWith(keys);

    /// <summary>
    /// Sends every pending change now: one stateDelta with the clips whose state differs from
    /// what the page last got, refresh progress, and cacheStats if the cache moved.
    /// </summary>
    public void FlushState()
    {
        _deltaScheduled = false;
        _deltaTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (!_pageReady)
        {
            // OnReady sends everything from scratch.
            _dirty.Clear();
            _allDirty = false;
            return;
        }

        FlushProgress();

        if (_catalogLoaded && (_allDirty || _dirty.Count > 0))
        {
            IEnumerable<ClipInfo> candidates = _allDirty
                ? _catalog
                : _dirty.Select(k => _byKey.GetValueOrDefault(k)).OfType<ClipInfo>();

            var changed = new Dictionary<string, ClipStateDto>(StringComparer.Ordinal);
            var history = PlayHistory.From(_cache);
            foreach (var info in candidates)
            {
                var state = StateOf(info, history);
                if (_sent.TryGetValue(info.Key, out var previous) && previous == state)
                    continue;
                _sent[info.Key] = state;
                changed[info.Key] = state;
            }

            _dirty.Clear();
            _allDirty = false;
            if (changed.Count > 0)
                Post(new StateDeltaMessage(changed));
        }

        if (_statsDirty)
        {
            _statsDirty = false;
            PostCacheStats();
        }
    }

    private void ScheduleDelta()
    {
        if (_deltaScheduled || _disposed)
            return;
        _deltaScheduled = true;
        _deltaTimer.Change(_options.DeltaInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnDeltaTimer()
    {
        if (_deltaScheduled)
            FlushState();
    }

    private void PostCacheStats()
    {
        if (!_pageReady)
            return;
        var stats = _cache.Stats();
        Post(new CacheStatsMessage(stats.PinnedBytes, stats.RollingBytes, stats.BudgetBytes, stats.FreeDiskBytes,
                                   stats.PinnedFiles, stats.RollingFiles, _queue.PendingPins, _queue.OwnsPins));
    }

    // ---- service events (background threads) -------------------------------------------------

    private void OnCacheChanged(object? sender, EventArgs e) => Marshal(() =>
    {
        // A file stored, evicted or purged — here or by the saver. Which clips is not said, so
        // recheck them all; only the ones that actually changed are sent.
        _allDirty = true;
        _statsDirty = true;
        ScheduleDelta();
    });

    private void OnJobChanged(object? sender, DownloadStatusEventArgs e) => Marshal(() =>
    {
        _dirty.Add(e.Status.ClipKey);
        if (e.Status.State is DownloadState.Done or DownloadState.Cancelled or DownloadState.Failed)
            _statsDirty = true;
        ScheduleDelta();
    });

    private void OnOwnershipChanged(object? sender, EventArgs e) => Marshal(() =>
    {
        _statsDirty = true;
        ScheduleDelta();
    });

    private void OnPreviewUpdated(object? sender, PreviewEventArgs e) => Marshal(() =>
    {
        if (e.State.Status != PreviewStatus.Pending)
            Post(PreviewReadyMessage.From(e.State));
    });

    // ---- plumbing ----------------------------------------------------------------------------

    private void Marshal(Action action)
    {
        if (_disposed)
            return;
        try
        {
            _dispatch(() =>
            {
                if (_disposed)
                    return;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("Browser: bridge callback failed", ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing.
        }
    }

    private Task RunOnBridgeThread(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            done.SetResult();
            return done.Task;
        }

        try
        {
            _dispatch(() =>
            {
                try
                {
                    if (!_disposed)
                        action();
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            done.TrySetResult();
        }
        return done.Task;
    }

    private void Post(HostMessage message)
    {
        if (!_pageReady || _disposed)
            return;
        _post(BridgeJson.Serialize(message));
    }

    private void Notice(string level, string text) => Post(new NoticeMessage(level, text));

    private static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) ? AsBool(v) : null;

    private static bool? AsBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static double? Number(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IReadOnlyList<string> Keys(JsonElement m)
    {
        if (!m.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            return [];
        return keys.EnumerateArray()
                   .Where(e => e.ValueKind == JsonValueKind.String)
                   .Select(e => e.GetString()!)
                   .Where(k => k.Length > 0)
                   .Distinct(StringComparer.Ordinal)
                   .ToList();
    }

    /// <summary>Stops listening, cancels background work, and writes any pending settings.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _cache.Changed -= OnCacheChanged;
        _queue.JobChanged -= OnJobChanged;
        _queue.Progress -= OnJobChanged;
        _queue.OwnershipChanged -= OnOwnershipChanged;
        _previews.PreviewUpdated -= OnPreviewUpdated;

        _shutdown.Cancel();
        _deltaTimer.Dispose();
        _saveTimer.Dispose();

        if (_savePending)
        {
            _savePending = false;
            SaveNow();
        }

        _disposed = true;
        _shutdown.Dispose();
    }
}
