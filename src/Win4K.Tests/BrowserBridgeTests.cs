using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Media;
using Win4K.ScreenSaver;
using Win4K.ScreenSaver.Browser;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// The browser bridge against real services (cache, queue, pins, previews) in a temp root, with
/// a fake host and a recorded post callback: every page â†’ host message, every host â†’ page
/// shape the contract (docs/browser-bridge-contract.md) promises.
/// </summary>
public class BrowserBridgeTests : IDisposable
{
    private const long MB = 1024 * 1024;

    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _posted = [];
    private readonly object _gate = new(); // Monitor: re-entrant, as the UI thread's dispatch is
    private readonly FakeHost _host = new();
    private readonly FixtureHttp _http = new();
    private int _saves;

    public BrowserBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-bridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- fixture ----------------------------------------------------------------------------

    private static ClipInfo Apple => new()
    {
        Key = "apple:A1",
        SourceId = "apple",
        CatalogId = "apple",
        Title = "Hawaii Coastline",
        Category = "Landscapes",
        SizeBytes = 700 * MB,
        Width = 3840,
        Height = 2160,
        Codec = "hevc",
        TimeOfDay = TimeOfDay.Day,
        Captions = [new ClipCaption(0, "Volcanic coast")],
        Urls = new ClipUrls(Uhd4KSdr: "https://sylvan.example/a_sdr.mov", Uhd4KHdr: "https://sylvan.example/a_hdr.mov",
                            Hd1080Sdr: "https://sylvan.example/a_1080.mov"),
    };

    private static ClipInfo Archive => new()
    {
        Key = "ia:Fire/Fire 66 - 4k.mp4",
        SourceId = "archive-org",
        CatalogId = "archive-org",
        Title = "Fire 66 - 4k",
        Category = "Mixed",
        SizeBytes = 600 * MB,
        AddedUtc = Now.AddDays(-5),
        Subjects = ["fire"],
        ThumbnailUrl = "https://img.example/fire.jpg",
        Urls = new ClipUrls(Uhd4KSdr: "https://archive.example/Fire%2066%20-%204k.mp4"),
    };

    private static ClipInfo Nasa => new()
    {
        Key = "nasa:earth",
        SourceId = "nasa",
        CatalogId = "nasa",
        Title = "Earth",
        Category = "Space",
        SizeBytes = 100 * MB,
        AddedUtc = Now.AddDays(-90),
        Urls = new ClipUrls(Uhd4KSdr: "https://nasa.example/earth.mp4"),
    };

    private ClipInfo Local => new()
    {
        Key = "local:" + Path.Combine(_root, "clip.mp4"),
        SourceId = "local",
        CatalogId = "local:" + _root,
        Title = "clip",
        LocalPath = Path.Combine(_root, "clip.mp4"),
    };

    private List<ClipInfo> Catalogue => [Apple, Archive, Nasa, Local];

    private AppSettings NewSettings(double budgetGb = 20) => new()
    {
        CacheDirectory = Path.Combine(_root, "cache"),
        CacheBudgetGb = budgetGb,
        Quality = VideoQuality.Uhd4KSdr, // Auto would follow this machine's display
    };

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private sealed record Built(BrowserBridge Bridge, AppSettings Settings, VideoCache Cache, DownloadQueue Queue, MediaPreviewService Previews);

    private Built Create(
        AppSettings? settings = null,
        Func<IReadOnlyList<ClipInfo>>? catalogue = null,
        Func<IProgress<CatalogRefreshProgress>, CancellationToken, Task<CatalogRefreshReport>>? refresh = null,
        Action<AppSettings>? save = null,
        Func<bool>? refreshDue = null)
    {
        settings ??= NewSettings();
        var cache = Track(new VideoCache(settings, watchForChanges: false));
        var queue = Track(new DownloadQueue(cache)); // never started: jobs stay Queued
        var previews = Track(new MediaPreviewService(settings, cache: null, new MediaPreviewOptions
        {
            Directory = Path.Combine(_root, "previews"),
            Http = _http.Client(),
            Runner = new FakeFfmpeg(),
            AutoDetectFfmpeg = false,
            CaFile = () => "ca.pem",
            InputFileFor = _ => null,
            Workers = 1,
            SaveDebounce = TimeSpan.FromMilliseconds(10),
        }));

        var bridge = Track(new BrowserBridge(settings, cache, queue, previews, _host, Record, new BrowserBridgeOptions
        {
            Dispatch = action => { lock (_gate) action(); },
            Save = save ?? (_ => Interlocked.Increment(ref _saves)),
            LoadCatalog = (_, _) => Task.FromResult(catalogue?.Invoke() ?? Catalogue),
            RefreshCatalogs = refresh,
            RefreshDue = refreshDue ?? (() => false),
            // Long enough never to fire inside a test: flushes are explicit.
            SaveDebounce = TimeSpan.FromHours(1),
            DeltaInterval = TimeSpan.FromHours(1),
            PreviewBackgroundFill = false,
        }));
        return new Built(bridge, settings, cache, queue, previews);
    }

    private async Task<Built> CreateReady(AppSettings? settings = null)
    {
        var built = Create(settings);
        await built.Bridge.InitializeAsync();
        Send(built.Bridge, new { type = "ready" });
        return built;
    }

    private void Record(string json)
    {
        lock (_gate)
            _posted.Add(json);
    }

    private void Send(BrowserBridge bridge, object message)
    {
        lock (_gate)
            bridge.OnPageMessage(JsonSerializer.Serialize(message));
    }

    private List<JsonElement> Messages(string? type = null)
    {
        lock (_gate)
        {
            return _posted.Select(j => JsonDocument.Parse(j).RootElement.Clone())
                          .Where(m => type is null || m.GetProperty("type").GetString() == type)
                          .ToList();
        }
    }

    private void ClearPosted()
    {
        lock (_gate)
            _posted.Clear();
    }

    private JsonElement LastState(string type = "stateDelta") => Messages(type).Last();

    private static JsonElement ClipState(JsonElement delta, string key) => delta.GetProperty("clips").GetProperty(key);

    private async Task<JsonElement> WaitFor(Func<JsonElement, bool> match, string type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = Messages(type).Where(match).ToList();
            if (found.Count > 0)
                return found[0];
            await Task.Delay(20);
        }
        throw new TimeoutException($"no {type} message arrived");
    }

    // ---- handshake ---------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_posted_before_ready_then_settings_stats_catalog_and_full_state_arrive_in_order()
    {
        var built = Create();
        await built.Bridge.InitializeAsync();
        Assert.Empty(Messages());

        Send(built.Bridge, new { type = "ready" });

        var types = Messages().Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Equal(["settings", "catalog", "stateDelta", "cacheStats"], types);

        var catalog = Messages("catalog").Single();
        Assert.Equal(4, catalog.GetProperty("clips").GetArrayLength());
        Assert.Equal(4, LastState().GetProperty("clips").EnumerateObject().Count());
    }

    [Fact]
    public async Task Ready_before_the_catalogue_loads_gets_settings_now_and_the_catalogue_when_it_lands()
    {
        var built = Create();
        Send(built.Bridge, new { type = "ready" });
        Assert.Equal(["settings", "cacheStats"], Messages().Select(m => m.GetProperty("type").GetString()));

        await built.Bridge.InitializeAsync();
        Assert.Single(Messages("catalog"));
        Assert.Equal(4, LastState().GetProperty("clips").EnumerateObject().Count());
    }

    [Fact]
    public async Task A_second_ready_after_a_reload_resends_everything_and_unloaded_pages_get_nothing()
    {
        var built = await CreateReady();
        built.Bridge.PageUnloaded();
        ClearPosted();

        Send(built.Bridge, new { type = "setRotation", keys = new[] { Nasa.Key }, value = true });
        Assert.Empty(Messages());

        Send(built.Bridge, new { type = "ready" });
        Assert.Single(Messages("catalog"));
        Assert.True(ClipState(LastState(), Nasa.Key).GetProperty("inRotation").GetBoolean());
    }

    // ---- catalog -----------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_clips_carry_the_contract_fields_and_static_tags_only()
    {
        await CreateReady();
        var clips = Messages("catalog").Single().GetProperty("clips").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);

        var apple = clips[Apple.Key];
        Assert.Equal("apple", apple.GetProperty("sourceId").GetString());
        Assert.Equal("Hawaii Coastline", apple.GetProperty("title").GetString());
        Assert.Equal("Day", apple.GetProperty("timeOfDay").GetString());
        Assert.True(apple.GetProperty("hasHdr").GetBoolean());
        Assert.Equal(3840, apple.GetProperty("width").GetInt32());
        Assert.Equal(JsonValueKind.Null, apple.GetProperty("creator").ValueKind);
        Assert.Equal(JsonValueKind.Null, apple.GetProperty("addedUtc").ValueKind);
        Assert.Equal("Volcanic coast", apple.GetProperty("captions")[0].GetProperty("text").GetString());
        Assert.Equal(0, apple.GetProperty("captions")[0].GetProperty("atSeconds").GetDouble());
        Assert.Contains(apple.GetProperty("tags").EnumerateArray(),
                        t => t.GetProperty("facet").GetString() == "Resolution" && t.GetProperty("value").GetString() == "4K");
        Assert.Equal(["apple"], apple.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));

        var ia = clips[Archive.Key];
        Assert.EndsWith("Z", ia.GetProperty("addedUtc").GetString());
        Assert.Equal(["archive-org"], ia.GetProperty("sourceIds").EnumerateArray().Select(s => s.GetString()));

        Assert.True(clips[Local.Key].GetProperty("isLocal").GetBoolean());

        // State tags travel in stateDelta, never in the catalogue â€” not even New.
        Assert.DoesNotContain(clips.Values.SelectMany(c => c.GetProperty("tags").EnumerateArray()),
                              t => t.GetProperty("facet").GetString() == "State");
    }

    [Fact]
    public async Task Initialising_prepares_settings_so_new_clips_of_auto_add_sources_join_the_rotation_and_saves_at_once()
    {
        var built = await CreateReady();

        // A fresh install auto-adds Apple and local folders.
        Assert.Contains(Apple.Key, built.Settings.RotationKeys);
        Assert.Contains(Local.Key, built.Settings.RotationKeys);
        Assert.DoesNotContain(Archive.Key, built.Settings.RotationKeys);
        Assert.Equal(4, built.Settings.KnownKeys.Count);
        Assert.Equal(1, _saves);
    }

    // ---- stateDelta --------------------------------------------------------------------------

    [Fact]
    public async Task Full_state_maps_rotation_cache_health_and_new()
    {
        var settings = NewSettings();
        settings.FailedClips[Nasa.Key] = 3;
        settings.FailedClips[Archive.Key] = 2;

        // On disk before the cache opens, so it is adopted into the index.
        Directory.CreateDirectory(Path.Combine(settings.CacheDirectory!, "videos"));
        File.WriteAllBytes(Path.Combine(settings.CacheDirectory!, "videos", VideoCache.KeyFor(new Uri(Apple.Urls.Uhd4KSdr!))), new byte[1024]);

        await CreateReady(settings);
        var state = LastState();

        var apple = ClipState(state, Apple.Key);
        Assert.True(apple.GetProperty("inRotation").GetBoolean());
        Assert.True(apple.GetProperty("cached").GetBoolean());
        Assert.False(apple.GetProperty("pinned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, apple.GetProperty("download").ValueKind);
        Assert.Equal("Ok", apple.GetProperty("health").GetString());
        Assert.False(apple.GetProperty("isNew").GetBoolean()); // no AddedUtc

        Assert.Equal("Unhealthy", ClipState(state, Nasa.Key).GetProperty("health").GetString());
        Assert.Equal("Ok", ClipState(state, Archive.Key).GetProperty("health").GetString()); // 2 sessions is not 3

        // New = AddedUtc within 30 days, regardless of KnownKeys (which now holds every key).
        Assert.True(ClipState(state, Archive.Key).GetProperty("isNew").GetBoolean());
        Assert.False(ClipState(state, Nasa.Key).GetProperty("isNew").GetBoolean());
        Assert.False(ClipState(state, Local.Key).GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Deltas_carry_only_clips_whose_state_changed()
    {
        var settings = NewSettings();
        Directory.CreateDirectory(Path.Combine(settings.CacheDirectory!, "videos"));
        File.WriteAllBytes(Path.Combine(settings.CacheDirectory!, "videos", VideoCache.KeyFor(new Uri(Apple.Urls.Uhd4KSdr!))), new byte[1024]);
        var built = await CreateReady(settings);
        ClearPosted();

        // A quality change rechecks every clip, but only Apple's rendition changes with it:
        // archive.org, NASA and local clips play the same file at any quality.
        Send(built.Bridge, new { type = "saveSettings", patch = new { quality = "Uhd4KHdr" } });

        var delta = LastState();
        Assert.Equal([Apple.Key], delta.GetProperty("clips").EnumerateObject().Select(p => p.Name));
        Assert.False(ClipState(delta, Apple.Key).GetProperty("cached").GetBoolean());

        // Repeating an edit that changes nothing sends no delta at all.
        ClearPosted();
        Send(built.Bridge, new { type = "setRotation", keys = new[] { Apple.Key }, value = true });
        Assert.Empty(Messages("stateDelta"));
    }

    [Fact]
    public async Task Last_played_comes_from_the_cache_index_and_previewing_is_not_a_play()
    {
        var settings = NewSettings();
        Directory.CreateDirectory(Path.Combine(settings.CacheDirectory!, "videos"));
        var appleFile = VideoCache.KeyFor(new Uri(Apple.Urls.Uhd4KSdr!));
        File.WriteAllBytes(Path.Combine(settings.CacheDirectory!, "videos", appleFile), new byte[1024]);

        var built = await CreateReady(settings);
        var stamped = built.Cache.Entries().Single().LastPlayedUtc; // adoption stamps it
        var state = LastState();

        var applePlayed = ClipState(state, Apple.Key).GetProperty("lastPlayedUtc");
        Assert.Equal(DateTime.SpecifyKind(stamped, DateTimeKind.Utc), applePlayed.GetDateTime().ToUniversalTime());
        Assert.EndsWith("Z", applePlayed.GetString());
        Assert.Equal(JsonValueKind.Null, ClipState(state, Nasa.Key).GetProperty("lastPlayedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, ClipState(state, Local.Key).GetProperty("lastPlayedUtc").ValueKind);

        // The browser's preview panel plays from the cache without stamping a play.
        await Task.Delay(30);
        Send(built.Bridge, new { type = "selectClip", key = Apple.Key });
        Assert.Equal(Path.Combine(built.Cache.VideosDirectory, appleFile), _host.Shown.Last().Url);
        Assert.Equal(stamped, built.Cache.Entries().Single().LastPlayedUtc);

        // A real play by the saver (another process, so another VideoCache) does, and the page
        // hears about it when the index watcher reloads (Refresh is what the watcher calls).
        ClearPosted();
        using (var saverCache = new VideoCache(settings, watchForChanges: false))
            saverCache.Resolve(ClipResolver.PlayableClip(Apple, settings.Quality)!);
        built.Cache.Refresh();
        lock (_gate)
            built.Bridge.FlushState();
        Assert.True(built.Cache.Entries().Single().LastPlayedUtc > stamped);
        var delta = LastState();
        Assert.Equal([Apple.Key], delta.GetProperty("clips").EnumerateObject().Select(p => p.Name));
        Assert.True(ClipState(delta, Apple.Key).GetProperty("lastPlayedUtc").GetDateTime().ToUniversalTime() >
                    DateTime.SpecifyKind(stamped, DateTimeKind.Utc));
    }

    // ---- rotation ----------------------------------------------------------------------------

    [Fact]
    public async Task SetRotation_edits_the_rotation_sends_a_delta_and_saves_debounced()
    {
        var built = await CreateReady();
        var savesBefore = _saves;
        ClearPosted();

        Send(built.Bridge, new { type = "setRotation", keys = new[] { Archive.Key, Nasa.Key }, value = true });
        Send(built.Bridge, new { type = "setRotation", keys = new[] { Apple.Key }, value = false });

        Assert.Contains(Archive.Key, built.Settings.RotationKeys);
        Assert.Contains(Nasa.Key, built.Settings.RotationKeys);
        Assert.DoesNotContain(Apple.Key, built.Settings.RotationKeys);

        var deltas = Messages("stateDelta");
        Assert.Equal(2, deltas.Count);
        Assert.Equal([Archive.Key, Nasa.Key], deltas[0].GetProperty("clips").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(ClipState(deltas[1], Apple.Key).GetProperty("inRotation").GetBoolean());

        // Debounced: nothing written yet, then one write for both edits.
        Assert.Equal(savesBefore, _saves);
        built.Bridge.FlushSave();
        Assert.Equal(savesBefore + 1, _saves);
        built.Bridge.FlushSave();
        Assert.Equal(savesBefore + 1, _saves);
    }

    [Fact]
    public async Task SetAutoAdd_with_includeExisting_adds_the_source_and_puts_its_clips_in_rotation()
    {
        var built = await CreateReady();
        ClearPosted();

        Send(built.Bridge, new { type = "setAutoAdd", sourceId = "archive-org", value = true, includeExisting = true });

        Assert.Contains("archive-org", built.Settings.AutoAddSources);
        Assert.Contains(Archive.Key, built.Settings.RotationKeys);
        Assert.True(ClipState(LastState(), Archive.Key).GetProperty("inRotation").GetBoolean());

        var sources = Messages("settings").Last().GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.True(sources["archive-org"].GetProperty("autoAdd").GetBoolean());
        Assert.Equal(1, sources["archive-org"].GetProperty("clipCount").GetInt32());

        // Without includeExisting only future clips are affected.
        Send(built.Bridge, new { type = "setAutoAdd", sourceId = "nasa", value = true });
        Assert.Contains("nasa", built.Settings.AutoAddSources);
        Assert.DoesNotContain(Nasa.Key, built.Settings.RotationKeys);

        Send(built.Bridge, new { type = "setAutoAdd", sourceId = "archive-org", value = false, includeExisting = true });
        Assert.DoesNotContain("archive-org", built.Settings.AutoAddSources);
        Assert.DoesNotContain(Archive.Key, built.Settings.RotationKeys);
    }

    // ---- pins --------------------------------------------------------------------------------

    [Fact]
    public async Task SetPinned_over_budget_is_refused_then_succeeds_after_raising_the_limit()
    {
        var built = await CreateReady(NewSettings(budgetGb: 1));
        ClearPosted();

        Send(built.Bridge, new { type = "setPinned", keys = new[] { Apple.Key, Archive.Key }, value = true });

        var refused = Messages("pinRefused").Single();
        Assert.Equal([Apple.Key, Archive.Key], refused.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(2, refused.GetProperty("requiredGb").GetInt32());    // 1,300 MB rounds up
        Assert.Equal(1.0, refused.GetProperty("budgetGb").GetDouble());
        Assert.Equal(0.3, refused.GetProperty("shortfallGb").GetDouble());
        Assert.Equal(1300 * MB, refused.GetProperty("requiredBytes").GetInt64());
        Assert.Empty(built.Settings.KeptOffline);
        Assert.Empty(Messages("stateDelta"));

        // The page's "Raise limit to 2 GB?" â†’ yes.
        Send(built.Bridge, new { type = "saveSettings", patch = new { cacheBudgetGb = refused.GetProperty("requiredGb").GetInt32() } });
        Assert.Equal(2, built.Settings.CacheBudgetGb);
        Assert.Equal(2.0, Messages("settings").Last().GetProperty("values").GetProperty("cacheBudgetGb").GetDouble());

        ClearPosted();
        Send(built.Bridge, new { type = "setPinned", keys = new[] { Apple.Key, Archive.Key }, value = true });

        Assert.Empty(Messages("pinRefused"));
        Assert.Equal([Apple.Key, Archive.Key], built.Settings.KeptOffline.Order());
        var apple = ClipState(LastState(), Apple.Key);
        Assert.True(apple.GetProperty("pinned").GetBoolean());
        Assert.Equal("Queued", apple.GetProperty("download").GetProperty("state").GetString());
        Assert.Equal(700 * MB, apple.GetProperty("download").GetProperty("totalBytes").GetInt64());
        Assert.Equal(DownloadPriority.Pin, built.Queue.Get(Apple.Key)!.Priority);
        Assert.Equal(2, Messages("cacheStats").Last().GetProperty("pendingPins").GetInt32());

        ClearPosted();
        Send(built.Bridge, new { type = "setPinned", keys = new[] { Apple.Key }, value = false });
        apple = ClipState(LastState(), Apple.Key);
        Assert.False(apple.GetProperty("pinned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, apple.GetProperty("download").ValueKind);
        Assert.DoesNotContain(Apple.Key, built.Settings.KeptOffline);
    }

    [Fact]
    public async Task Download_progress_is_coalesced_into_one_delta_per_flush()
    {
        var built = await CreateReady();
        Send(built.Bridge, new { type = "setPinned", keys = new[] { Nasa.Key }, value = true });
        ClearPosted();

        // Queue events arrive from worker threads; each only marks the clip dirty.
        built.Queue.Cancel(Nasa.Key);
        Assert.Empty(Messages("stateDelta"));

        built.Bridge.FlushState();
        var delta = Messages("stateDelta").Single();
        Assert.Equal([Nasa.Key], delta.GetProperty("clips").EnumerateObject().Select(p => p.Name));
    }

    // ---- settings ----------------------------------------------------------------------------

    [Fact]
    public async Task SaveSettings_applies_a_patch_ignores_unknown_fields_and_echoes_the_stored_values()
    {
        var built = await CreateReady();
        ClearPosted();

        Send(built.Bridge, new
        {
            type = "saveSettings",
            patch = new
            {
                shuffle = false,
                useTimeOfDay = false,
                multiMonitorMode = "SpanAll",
                cacheEnabled = false,
                cacheBudgetGb = 0.2,          // clamped to 1
                autoAddSources = new[] { "nasa", "bogus-source" },
                shortcutDesktop = true,
                hotkey = "ctrl+alt+KeyK",
                previewCapabilities = new { ffmpegAvailable = true }, // read-only
                somethingElse = 42,
            },
        });

        var s = built.Settings;
        Assert.False(s.Shuffle);
        Assert.False(s.UseTimeOfDay);
        Assert.Equal(MultiMonitorMode.SpanAll, s.MultiMonitorMode);
        Assert.False(s.CacheEnabled);
        Assert.Equal(1, s.CacheBudgetGb);
        Assert.Contains("nasa", s.AutoAddSources);
        Assert.DoesNotContain("apple", s.AutoAddSources);

        Assert.Equal(new ShortcutOptions(true, false, "CTRL+ALT+K"), _host.Applied.Single());

        var values = Messages("settings").Last().GetProperty("values");
        Assert.False(values.GetProperty("shuffle").GetBoolean());
        Assert.Equal("SpanAll", values.GetProperty("multiMonitorMode").GetString());
        Assert.Equal(["nasa"], values.GetProperty("autoAddSources").EnumerateArray().Select(v => v.GetString()));
        Assert.True(values.GetProperty("shortcutDesktop").GetBoolean());
        Assert.Equal("CTRL+ALT+K", values.GetProperty("hotkey").GetString());
        Assert.False(values.GetProperty("previewCapabilities").GetProperty("ffmpegAvailable").GetBoolean());
        Assert.False(values.TryGetProperty("enabledCatalogIds", out _)); // retired in v2
        Assert.Equal(Rotation.SourceIds, values.GetProperty("availableSources").EnumerateArray().Select(v => v.GetString()!));
    }

    [Fact]
    public async Task An_unusable_hotkey_is_refused_with_a_notice_and_shortcuts_are_left_alone()
    {
        var built = await CreateReady();
        Send(built.Bridge, new { type = "saveSettings", patch = new { hotkey = "K" } });

        Assert.Empty(_host.Applied);
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
        Assert.Equal(ShortcutManager.DefaultHotkey, Messages("settings").Last().GetProperty("values").GetProperty("hotkey").GetString());
    }

    [Fact]
    public async Task Changing_local_folders_reloads_the_catalogue()
    {
        var loads = 0;
        var built = Create(catalogue: () =>
        {
            loads++;
            return Catalogue;
        });
        await built.Bridge.InitializeAsync();
        Send(built.Bridge, new { type = "ready" });

        Send(built.Bridge, new { type = "saveSettings", patch = new { localFolders = new[] { @"D:\Clips", @"d:\clips", " " } } });
        await built.Bridge.Background;

        Assert.Equal([@"D:\Clips"], built.Settings.LocalFolders);
        Assert.Equal(2, loads);
        Assert.Equal(2, Messages("catalog").Count);
    }

    [Fact]
    public async Task Dispose_writes_pending_settings()
    {
        var built = await CreateReady();
        var before = _saves;
        Send(built.Bridge, new { type = "setRotation", keys = new[] { Nasa.Key }, value = true });

        built.Bridge.Dispose();
        Assert.Equal(before + 1, _saves);
    }

    [Fact]
    public void SaveMerged_keeps_failures_and_known_keys_the_saver_wrote_meanwhile()
    {
        var path = Path.Combine(_root, "settings.json");
        var onDisk = new AppSettings();
        onDisk.FailedClips["ia:x"] = 3;
        onDisk.FailedClips["apple:A1"] = 1;
        onDisk.KnownKeys.Add("nasa:new");
        onDisk.Save(path);

        var browser = new AppSettings { Shuffle = false };
        browser.FailedClips["apple:A1"] = 2;
        browser.RotationKeys.Add("apple:A1");
        BrowserBridge.SaveMerged(browser, path);

        var saved = AppSettings.Load(path);
        Assert.False(saved.Shuffle);
        Assert.Equal(3, saved.FailedClips["ia:x"]);
        Assert.Equal(2, saved.FailedClips["apple:A1"]);
        Assert.Contains("nasa:new", saved.KnownKeys);
        Assert.Contains("apple:A1", saved.RotationKeys);
    }

    // ---- preview panel, previews -------------------------------------------------------------

    [Fact]
    public async Task SelectClip_plays_the_playable_rendition_in_the_host_and_null_stops_it()
    {
        var built = await CreateReady();

        Send(built.Bridge, new { type = "selectClip", key = Archive.Key });
        var (clip, url) = _host.Shown.Last();
        Assert.Equal(Archive.Key, clip!.Id);
        Assert.Equal("https://archive.example/Fire%2066%20-%204k.mp4", url); // streamed, still escaped

        Send(built.Bridge, new { type = "selectClip", key = (string?)null });
        Assert.Null(_host.Shown.Last().Clip);

        Send(built.Bridge, new { type = "selectClip", key = "nope:unknown" });
        Assert.Null(_host.Shown.Last().Clip);

        Send(built.Bridge, new { type = "panelRect", x = 10.5, y = 20, width = 640, height = 360, visible = true });
        Assert.Equal(new PanelRect(10.5, 20, 640, 360, true), _host.Rects.Single());
    }

    [Fact]
    public async Task RequestPreviews_answers_decided_previews_at_once_and_the_rest_via_previewReady()
    {
        _http.Respond("https://img.example/fire.jpg", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'])
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
            },
        });
        var built = await CreateReady();
        ClearPosted();

        Send(built.Bridge, new { type = "requestPreviews", keys = new[] { Archive.Key }, kinds = "Both" });

        // No ffmpeg here: the hover clip is decided at once.
        var hover = Messages("previewReady").Single();
        Assert.Equal("Hover", hover.GetProperty("kind").GetString());
        Assert.Equal("Unavailable", hover.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, hover.GetProperty("url").ValueKind);
        Assert.StartsWith("#", hover.GetProperty("placeholderColor").GetString());

        // The still comes from the catalogue thumbnail, on a worker.
        var still = await WaitFor(m => m.GetProperty("kind").GetString() == "Still", "previewReady");
        Assert.Equal("Ready", still.GetProperty("status").GetString());
        var url = still.GetProperty("url").GetString()!;
        Assert.StartsWith($"https://{MediaPreviewService.VirtualHost}/{MediaPreviewService.HashFor(Archive.Key)}", url);
        Assert.Contains("?v=", url);

        // Asked again, a Ready still is answered immediately.
        ClearPosted();
        Send(built.Bridge, new { type = "requestPreviews", keys = new[] { Archive.Key }, kinds = "Still" });
        Assert.Equal(url, Messages("previewReady").Single().GetProperty("url").GetString());
    }

    // ---- refresh, start now, odds and ends ---------------------------------------------------

    [Fact]
    public async Task RefreshCatalogs_streams_progress_then_sends_the_new_catalogue_and_prepares_again()
    {
        var added = Apple with { Key = "apple:NEW", Title = "New aerial" };
        var refreshed = false;
        var built = Create(
            catalogue: () => refreshed ? [.. Catalogue, added] : Catalogue,
            refresh: (progress, _) =>
            {
                progress.Report(new CatalogRefreshProgress("nasa", "items", 1, 9));
                progress.Report(new CatalogRefreshProgress("nasa", "items", 9, 9));
                refreshed = true;
                return Task.FromResult(new CatalogRefreshReport(
                [
                    new CatalogRefreshOutcome("nasa", true, 9, 0, 0, 9, 1, 0, null, [], null),
                ]));
            });
        await built.Bridge.InitializeAsync();
        Send(built.Bridge, new { type = "ready" });
        ClearPosted();

        Send(built.Bridge, new { type = "refreshCatalogs" });
        await built.Bridge.Background;

        var progress = Messages("refreshProgress");
        // Per-item reports are coalesced to the latest per source.
        Assert.Equal(2, progress.Count);
        Assert.True(progress[0].GetProperty("running").GetBoolean());
        Assert.Equal(9, progress[0].GetProperty("done").GetInt32());
        Assert.False(progress[1].GetProperty("running").GetBoolean());
        Assert.True(progress[1].GetProperty("ok").GetBoolean());
        Assert.Equal(1, progress[1].GetProperty("sources")[0].GetProperty("added").GetInt32());

        Assert.Equal(5, Messages("catalog").Single().GetProperty("clips").GetArrayLength());
        Assert.Contains("apple:NEW", built.Settings.RotationKeys); // Apple auto-adds
        Assert.True(ClipState(LastState(), "apple:NEW").GetProperty("inRotation").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opening_refreshes_catalogues_in_the_background_only_when_due(bool due)
    {
        var calls = 0;
        var built = Create(
            refresh: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new CatalogRefreshReport([]));
            },
            refreshDue: () => due);

        await built.Bridge.InitializeAsync();
        await built.Bridge.Background;

        Assert.Equal(due ? 1 : 0, calls);
    }

    [Fact]
    public async Task A_failed_refresh_reports_the_error_and_keeps_the_catalogue()
    {
        var built = Create(refresh: (_, _) => throw new HttpRequestException("offline"));
        await built.Bridge.InitializeAsync();
        Send(built.Bridge, new { type = "ready" });
        ClearPosted();

        Send(built.Bridge, new { type = "refreshCatalogs" });
        await built.Bridge.Background;

        var failed = Messages("refreshProgress").Single();
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal("offline", failed.GetProperty("error").GetString());
        Assert.Empty(Messages("catalog"));
    }

    [Fact]
    public async Task StartNow_goes_to_the_host_and_a_refusal_becomes_a_notice()
    {
        var built = await CreateReady();
        Send(built.Bridge, new { type = "startNow" });
        Assert.Equal(1, _host.StartNowCalls);
        Assert.Empty(Messages("notice"));

        _host.StartNowResult = false;
        Send(built.Bridge, new { type = "startNow" });
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
    }

    [Fact]
    public async Task Folder_messages_use_the_host_dialogs()
    {
        var built = await CreateReady();
        var folder = Path.Combine(_root, "picked");
        _host.PickResult = folder;

        Send(built.Bridge, new { type = "pickFolder", purpose = "localFolder" });
        await built.Bridge.Background;
        Assert.Equal([folder], built.Settings.LocalFolders);

        Send(built.Bridge, new { type = "pickFolder", purpose = "cacheDirectory" });
        Assert.Equal(folder, built.Settings.CacheDirectory);
        Assert.Contains(Messages("notice"), n => n.GetProperty("text").GetString()!.Contains("cache folder"));

        Send(built.Bridge, new { type = "openFolder", which = "cache" });
        Assert.Equal(built.Cache.Root, _host.Opened.Single());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"noType\":true}")]
    [InlineData("{\"type\":\"noSuchMessage\"}")]
    [InlineData("{\"type\":\"setRotation\",\"keys\":\"not-an-array\"}")]
    [InlineData("{\"type\":\"saveSettings\",\"patch\":{\"quality\":17,\"cacheBudgetGb\":\"lots\"}}")]
    [InlineData("{\"type\":\"cancelPreviews\",\"keys\":[\"x\"]}")]
    [InlineData("{\"type\":\"retryDownloads\"}")]
    [InlineData("{\"type\":\"log\",\"level\":\"warn\",\"text\":\"hello from the page\"}")]
    public async Task Malformed_unknown_and_fire_and_forget_messages_never_throw(string json)
    {
        var built = await CreateReady();
        var before = built.Settings.CacheBudgetGb;
        lock (_gate)
            built.Bridge.OnPageMessage(json);
        Assert.Equal(before, built.Settings.CacheBudgetGb);
    }

    [Fact]
    public async Task A_message_posted_as_a_json_string_is_unwrapped()
    {
        var built = await CreateReady();
        lock (_gate)
            built.Bridge.OnPageMessage(JsonSerializer.Serialize(JsonSerializer.Serialize(new { type = "setRotation", keys = new[] { Nasa.Key }, value = true })));
        Assert.Contains(Nasa.Key, built.Settings.RotationKeys);
    }

    [Fact]
    public async Task Purge_deletes_the_rolling_cache_and_says_so()
    {
        var settings = NewSettings();
        Directory.CreateDirectory(Path.Combine(settings.CacheDirectory!, "videos"));
        File.WriteAllBytes(Path.Combine(settings.CacheDirectory!, "videos", VideoCache.KeyFor(new Uri(Nasa.Urls.Uhd4KSdr!))), new byte[1024]);
        var built = await CreateReady(settings);

        Send(built.Bridge, new { type = "purgeCache" });
        await built.Bridge.Background;

        Assert.False(built.Cache.IsCached(new Uri(Nasa.Urls.Uhd4KSdr!)));
        Assert.Contains("1", Messages("notice").Single().GetProperty("text").GetString());
    }

    // ---- pure helpers ------------------------------------------------------------------------

    [Fact]
    public void Panel_rect_converts_css_pixels_by_zoom_and_dpi()
    {
        var rect = new PanelRect(10, 20, 320, 180, true);

        Assert.Equal(new Rectangle(10, 20, 320, 180), rect.ToDevicePixels(1.0, 96));
        Assert.Equal(new Rectangle(15, 30, 480, 270), rect.ToDevicePixels(1.0, 144));   // 150 % display
        Assert.Equal(new Rectangle(19, 38, 600, 337), rect.ToDevicePixels(1.25, 144));  // plus 125 % zoom

        Assert.Equal(Rectangle.Empty, (rect with { Visible = false }).ToDevicePixels(1, 96));
        Assert.Equal(Rectangle.Empty, new PanelRect(0, 0, 0, 100, true).ToDevicePixels(1, 96));
        Assert.Equal(Rectangle.Empty, new PanelRect(double.NaN, 0, 10, 10, true).ToDevicePixels(1, 96));
    }

    [Theory]
    [InlineData("CTRL+ALT+S", "CTRL+ALT+S")]
    [InlineData("ctrl+alt+s", "CTRL+ALT+S")]
    [InlineData("Alt+Ctrl+KeyS", "CTRL+ALT+S")]
    [InlineData("Shift+Ctrl+Digit1", "CTRL+SHIFT+D1")]
    [InlineData("Ctrl+Alt+F12", "CTRL+ALT+F12")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("S", null)]
    [InlineData("Ctrl+Alt", null)]
    [InlineData("Ctrl+65", null)]
    [InlineData("Ctrl+NoSuchKey", null)]
    [InlineData("Ctrl+A+B", null)]
    public void Hotkeys_from_the_page_normalise_to_shortcut_form(string? input, string? expected)
    {
        Assert.Equal(expected, ShortcutManager.NormalizeHotkey(input));
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeHost : IBrowserHost
    {
        public List<(Clip? Clip, string? Url)> Shown { get; } = [];
        public List<PanelRect> Rects { get; } = [];
        public List<ShortcutOptions> Applied { get; } = [];
        public List<string> Opened { get; } = [];
        public ShortcutOptions Shortcuts { get; set; } = new(false, false, ShortcutManager.DefaultHotkey);
        public int StartNowCalls { get; private set; }
        public bool StartNowResult { get; set; } = true;
        public string? PickResult { get; set; }

        public void ShowClip(Clip? clip, string? playUrl) => Shown.Add((clip, playUrl));
        public void SetPanelRect(PanelRect rect) => Rects.Add(rect);

        public bool StartNow()
        {
            StartNowCalls++;
            return StartNowResult;
        }

        public ShortcutOptions ReadShortcuts() => Shortcuts;

        public bool ApplyShortcuts(ShortcutOptions options)
        {
            Applied.Add(options);
            Shortcuts = options;
            return true;
        }

        public string? PickFolder(string title, string? initialPath) => PickResult;
        public void OpenFolder(string path) => Opened.Add(path);
    }
}
