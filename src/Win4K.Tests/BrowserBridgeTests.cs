using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Media;
using Win4K.Core.Updates;
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
        Func<bool>? refreshDue = null,
        UpdateChecker? updateChecker = null,
        Func<UpdateInstaller>? createInstaller = null)
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
            UpdateChecker = updateChecker,
            CreateInstaller = createInstaller,
        }));
        return new Built(bridge, settings, cache, queue, previews);
    }

    private async Task<Built> CreateReady(AppSettings? settings = null, UpdateChecker? updateChecker = null, Func<UpdateInstaller>? createInstaller = null)
    {
        var built = Create(settings, updateChecker: updateChecker, createInstaller: createInstaller);
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
        // The hotkey is a setting of its own, handed to the listener after it is saved.
        Assert.Equal("CTRL+ALT+K", s.StartNowHotkey);
        Assert.Equal([("CTRL+ALT+K", (string?)ShortcutManager.DefaultHotkey)], _host.Hotkeys);

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
        Assert.Empty(_host.Hotkeys);
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
        Assert.Equal(ShortcutManager.DefaultHotkey, Messages("settings").Last().GetProperty("values").GetProperty("hotkey").GetString());
    }

    [Fact]
    public async Task The_hotkey_is_independent_of_the_shortcuts_and_can_be_turned_off()
    {
        var built = await CreateReady();
        _host.HotkeyProblem = "Ctrl+Alt+J is already used by another app";

        Send(built.Bridge, new { type = "saveSettings", patch = new { hotkey = "Ctrl+Alt+KeyJ" } });

        Assert.Empty(_host.Applied); // no shortcut is rewritten for a hotkey change
        Assert.Equal("CTRL+ALT+J", built.Settings.StartNowHotkey);
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());

        _host.HotkeyProblem = null;
        Send(built.Bridge, new { type = "saveSettings", patch = new { hotkey = "" } });

        Assert.Equal("", built.Settings.StartNowHotkey);
        Assert.Equal([("CTRL+ALT+J", (string?)ShortcutManager.DefaultHotkey), ("", "CTRL+ALT+J")], _host.Hotkeys);
        Assert.Equal(JsonValueKind.Null, Messages("settings").Last().GetProperty("values").GetProperty("hotkey").ValueKind);

        // The same hotkey again is not a change.
        Send(built.Bridge, new { type = "saveSettings", patch = new { hotkey = "" } });
        Assert.Equal(2, _host.Hotkeys.Count);
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
    public async Task Play_now_passes_the_clip_to_the_host_and_an_unknown_clip_falls_back_to_the_rotation()
    {
        var built = await CreateReady();

        Send(built.Bridge, new { type = "startNow", key = Apple.Key });
        Send(built.Bridge, new { type = "startNow", key = "apple:NOT-IN-THE-CATALOGUE" });
        Send(built.Bridge, new { type = "startNow" });

        Assert.Equal([Apple.Key, null, null], _host.StartNowKeys);
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

    // ---- export / import settings ------------------------------------------------------------

    private string PortableFile(string name, Action<AppSettings> shape, PortableShortcuts? shortcuts = null)
    {
        var settings = NewSettings();
        shape(settings);
        var path = Path.Combine(_root, name + SettingsPortability.FileExtension);
        SettingsPortability.WriteFile(SettingsPortability.Build(settings, Catalogue, shortcuts, "9.9", Now), path);
        return path;
    }

    private static string BackupPath => Path.Combine(AppPaths.Root, SettingsPortability.BackupFileName);

    [Fact]
    public async Task Exporting_writes_the_readable_file_where_the_dialog_said_and_cancelling_writes_nothing()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = await CreateReady();
        Send(built.Bridge, new { type = "setRotation", keys = new[] { Nasa.Key }, value = true });
        ClearPosted();

        _host.SavePath = null;
        Send(built.Bridge, new { type = "exportSettings" });
        Assert.Empty(Messages());

        _host.SavePath = Path.Combine(_root, "mine"); // typed without an extension
        Send(built.Bridge, new { type = "exportSettings" });

        var written = Path.Combine(_root, "mine" + SettingsPortability.FileExtension);
        Assert.True(SettingsPortability.TryParseFile(written, out var file, out var error), error);
        Assert.Contains(Nasa.Key, PortableSettings.KeysOf(file!.Rotation));
        Assert.Contains(file.Rotation!.SelectMany(g => g.Clips), c => c.Key == Nasa.Key && c.Title == "Earth");
        Assert.Equal(VideoQuality.Uhd4KSdr, file.Quality);
        Assert.NotNull(file.Shortcuts);
        Assert.Equal("info", Messages("notice").Single().GetProperty("level").GetString());
    }

    [Fact]
    public async Task Importing_previews_first_then_replaces_rotation_pins_and_settings_only_when_confirmed()
    {
        using var _ = AppPaths.UseRoot(_root);
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, "{ \"previous\": true }");
        var settings = NewSettings();
        settings.Shuffle = true;
        var built = await CreateReady(settings);
        var rotationBefore = built.Settings.RotationKeys.ToList();
        ClearPosted();

        var other = Path.Combine(_root, "other-cache");
        _host.OpenPath = PortableFile("in", s =>
        {
            s.Quality = VideoQuality.Hd1080Sdr;
            s.Shuffle = false;
            s.CacheBudgetGb = 30;
            s.AutoAddSources = new(StringComparer.Ordinal) { "nasa" };
            s.RotationKeys = new(StringComparer.Ordinal) { Apple.Key, Archive.Key, "apple:LATER" };
            s.KeptOffline = new(StringComparer.Ordinal) { Apple.Key };
            s.CacheDirectory = other;
            s.LocalFolders = [Path.Combine(_root, "videos")];
        }, new PortableShortcuts(true, false, ShortcutManager.DefaultHotkey));

        Send(built.Bridge, new { type = "importSettings" });

        var preview = Messages("importPreview").Single();
        Assert.Equal(rotationBefore.Count, preview.GetProperty("rotation").GetProperty("before").GetInt32());
        Assert.Equal(3, preview.GetProperty("rotation").GetProperty("after").GetInt32());
        Assert.Equal(1, preview.GetProperty("rotation").GetProperty("notInCatalogue").GetInt32());
        Assert.Equal(0, preview.GetProperty("pins").GetProperty("before").GetInt32());
        Assert.Equal(1, preview.GetProperty("pins").GetProperty("after").GetInt32());
        Assert.True(preview.GetProperty("pinsFit").GetBoolean());
        Assert.True(preview.GetProperty("hasMachine").GetBoolean());
        Assert.Equal(2, preview.GetProperty("machineChanges").GetArrayLength());
        Assert.Contains(preview.GetProperty("changes").EnumerateArray().Select(c => c.GetString()), c => c == "Quality: Uhd4KSdr → Hd1080Sdr");
        Assert.Equal("in" + SettingsPortability.FileExtension, preview.GetProperty("fileName").GetString());

        // Nothing has changed yet.
        Assert.Equal(rotationBefore.Order(), built.Settings.RotationKeys.Order());
        Assert.True(built.Settings.Shuffle);
        Assert.False(File.Exists(BackupPath));

        ClearPosted();
        Send(built.Bridge, new { type = "confirmImport", apply = true, includeMachine = false });

        Assert.Equal(new[] { Apple.Key, Archive.Key, "apple:LATER" }.Order(), built.Settings.RotationKeys.Order()); // the unknown key stays
        Assert.Equal(new[] { "nasa" }, built.Settings.AutoAddSources);
        Assert.Equal(VideoQuality.Hd1080Sdr, built.Settings.Quality);
        Assert.False(built.Settings.Shuffle);
        Assert.Equal(30, built.Settings.CacheBudgetGb);
        Assert.Equal(new[] { Apple.Key }, built.Settings.KeptOffline);
        Assert.NotNull(built.Queue.Get(Apple.Key)); // the pin is queued to download
        Assert.Equal(Path.Combine(_root, "cache"), built.Settings.CacheDirectory); // machine section skipped
        Assert.Empty(built.Settings.LocalFolders);
        Assert.Equal("{ \"previous\": true }", File.ReadAllText(BackupPath));
        Assert.Equal(new ShortcutOptions(true, false, ShortcutManager.DefaultHotkey), _host.Shortcuts);

        // The page is told live.
        Assert.True(ClipState(LastState(), Archive.Key).GetProperty("inRotation").GetBoolean());
        Assert.True(ClipState(LastState(), Apple.Key).GetProperty("pinned").GetBoolean());
        Assert.Equal(["nasa"], Messages("settings").Last().GetProperty("values").GetProperty("autoAddSources").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("Imported settings", Messages("notice").Last().GetProperty("text").GetString());
        Assert.Contains("1 not in this catalogue", Messages("notice").Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Importing_with_the_machine_option_applies_the_machine_section_too()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = await CreateReady();
        var other = Path.Combine(_root, "other-cache");
        var videos = Path.Combine(_root, "videos");
        _host.OpenPath = PortableFile("in", s => { s.CacheDirectory = other; s.LocalFolders = [videos]; s.FfmpegPath = @"C:\x\ffmpeg.exe"; });

        Send(built.Bridge, new { type = "importSettings" });
        Send(built.Bridge, new { type = "confirmImport", apply = true, includeMachine = true });
        await built.Bridge.Background;

        Assert.Equal(other, built.Settings.CacheDirectory);
        Assert.Equal([videos], built.Settings.LocalFolders);
        Assert.Equal(@"C:\x\ffmpeg.exe", built.Settings.FfmpegPath);
        Assert.Contains(Messages("notice"), n => n.GetProperty("text").GetString()!.Contains("cache folder"));
    }

    [Fact]
    public async Task Declining_the_import_changes_nothing_and_a_confirmation_with_nothing_pending_is_refused()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = await CreateReady();
        var before = built.Settings.RotationKeys.Order().ToList();
        _host.OpenPath = PortableFile("in", s => s.RotationKeys = new(StringComparer.Ordinal) { Nasa.Key });

        Send(built.Bridge, new { type = "importSettings" });
        Send(built.Bridge, new { type = "confirmImport", apply = false, includeMachine = false });
        Assert.Equal(before, built.Settings.RotationKeys.Order());
        Assert.False(File.Exists(BackupPath));

        ClearPosted();
        Send(built.Bridge, new { type = "confirmImport", apply = true, includeMachine = false }); // already answered
        Assert.Equal(before, built.Settings.RotationKeys.Order());
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
    }

    [Fact]
    public async Task A_file_that_is_not_a_settings_export_is_rejected_with_an_error_notice()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = await CreateReady();
        var path = Path.Combine(_root, "not-ours.json");
        File.WriteAllText(path, "{ \"version\": 2, \"quality\": \"Auto\" }");
        _host.OpenPath = path;
        ClearPosted();

        Send(built.Bridge, new { type = "importSettings" });

        Assert.Empty(Messages("importPreview"));
        var notice = Messages("notice").Single();
        Assert.Equal("error", notice.GetProperty("level").GetString());
        Assert.Contains("not a Win4KSS settings export", notice.GetProperty("text").GetString());

        // Cancelling the dialog is silent.
        _host.OpenPath = null;
        ClearPosted();
        Send(built.Bridge, new { type = "importSettings" });
        Assert.Empty(Messages());
    }

    [Fact]
    public async Task Import_waits_for_the_catalogue()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = Create(); // never initialised: no catalogue yet
        Send(built.Bridge, new { type = "ready" });
        _host.OpenPath = PortableFile("in", _ => { });
        ClearPosted();

        Send(built.Bridge, new { type = "importSettings" });

        Assert.Empty(Messages("importPreview"));
        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Pins_over_the_cache_limit_are_skipped_with_the_raise_limit_prompt_and_everything_else_is_imported()
    {
        using var _ = AppPaths.UseRoot(_root);
        var built = await CreateReady();
        _host.OpenPath = PortableFile("in", s =>
        {
            s.CacheBudgetGb = 1; // Apple 700 MB + Archive 600 MB do not fit
            s.Shuffle = false;
            s.RotationKeys = new(StringComparer.Ordinal) { Apple.Key };
            s.KeptOffline = new(StringComparer.Ordinal) { Apple.Key, Archive.Key };
        });

        Send(built.Bridge, new { type = "importSettings" });
        Assert.False(Messages("importPreview").Single().GetProperty("pinsFit").GetBoolean());
        ClearPosted();
        Send(built.Bridge, new { type = "confirmImport", apply = true, includeMachine = false });

        Assert.Empty(built.Settings.KeptOffline);                         // pins skipped
        Assert.Equal([Apple.Key], built.Settings.RotationKeys);           // the rotation went in
        Assert.False(built.Settings.Shuffle);                             // and the other settings
        var refused = Messages("pinRefused").Single();
        Assert.Equal(2, refused.GetProperty("keys").GetArrayLength());
        Assert.Equal(2, refused.GetProperty("requiredGb").GetInt32());
        Assert.Equal("warning", Messages("notice").Last().GetProperty("level").GetString());
    }

    // ---- updates -----------------------------------------------------------------------------

    private static ReleaseInfo AvailableRelease(string tag = "v99.0.0") =>
        new(tag, "name", "release notes", false, false,
            [new ReleaseAsset("Win4KSS-Setup-99.0.0.exe", 12, "https://example.test/Win4KSS-Setup-99.0.0.exe"),
             new ReleaseAsset("Win4KSS-Setup-99.0.0.exe.sha256", 90, "https://example.test/Win4KSS-Setup-99.0.0.exe.sha256")],
            null);

    private sealed class FakeUpdateSource(Func<ReleaseInfo?> respond) : IUpdateSource
    {
        public int Calls { get; private set; }
        public Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    [Fact]
    public async Task Opening_the_browser_checks_once_and_posts_updateStatus()
    {
        var source = new FakeUpdateSource(() => AvailableRelease());
        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source });

        var built = await CreateReady(settings, checker);

        var status = Messages("updateStatus").Single();
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal("99.0.0", status.GetProperty("latest").GetString());
        Assert.Equal(1, source.Calls);
        Assert.True(settings.LastUpdateAvailable);
    }

    [Fact]
    public async Task A_reload_replays_the_last_status_without_checking_again()
    {
        var source = new FakeUpdateSource(() => AvailableRelease());
        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source });
        var built = await CreateReady(settings, checker);
        Assert.Equal(1, source.Calls);
        ClearPosted();

        Send(built.Bridge, new { type = "ready" }); // a page reload

        Assert.Equal(1, source.Calls); // not re-checked
        var status = Messages("updateStatus").Single();
        Assert.True(status.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Disabling_the_toggle_skips_the_automatic_check_but_not_a_manual_one()
    {
        var source = new FakeUpdateSource(() => AvailableRelease());
        var settings = NewSettings();
        settings.AutoCheckForUpdates = false;
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source });

        var built = await CreateReady(settings, checker);
        Assert.Equal(0, source.Calls);
        Assert.Empty(Messages("updateStatus"));

        Send(built.Bridge, new { type = "checkUpdates" });
        await WaitFor(_ => true, "updateStatus");

        Assert.Equal(1, source.Calls); // "Check now" ignores the toggle
    }

    [Fact]
    public async Task Check_now_bypasses_the_24_hour_throttle()
    {
        var now = DateTime.UtcNow;
        var settings = NewSettings();
        settings.LastUpdateCheckUtc = now; // "already checked moments ago"
        settings.LastUpdateAvailable = false;
        var source = new FakeUpdateSource(() => AvailableRelease());
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = source, Time = new FixedTime(now) });

        // The automatic check on open still runs, but is throttled -- the real check never fires.
        var built = await CreateReady(settings, checker);
        Assert.Equal(0, source.Calls);
        Assert.False(Messages("updateStatus").Single().GetProperty("available").GetBoolean());
        ClearPosted();

        Send(built.Bridge, new { type = "checkUpdates" }); // bypasses the throttle
        await WaitFor(_ => true, "updateStatus");

        Assert.Equal(1, source.Calls);
        Assert.True(Messages("updateStatus").Single().GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Installing_with_nothing_available_refuses_with_a_notice()
    {
        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = new FakeUpdateSource(() => null) });
        var built = await CreateReady(settings, checker);
        ClearPosted();

        Send(built.Bridge, new { type = "installUpdate" });
        await WaitFor(_ => true, "notice");

        Assert.Equal("warning", Messages("notice").Single().GetProperty("level").GetString());
        Assert.Equal(0, _host.HandoffCalls);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_hash_mismatch_refuses_to_launch_and_never_hands_off_to_the_host()
    {
        var bytes = "fake installer bytes"u8.ToArray();
        var release = AvailableRelease();
        var exeUrl = release.Assets[0].DownloadUrl;
        var hashUrl = release.Assets[1].DownloadUrl;
        var wrongHash = Sha256Hex("not the right content"u8.ToArray()); // a real, but mismatching, SHA-256
        var http = new FixtureHttp()
            .Respond(exeUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })
            .Text(hashUrl, $"{wrongHash}  Win4KSS-Setup-99.0.0.exe\n");

        var settings = NewSettings();
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = new FakeUpdateSource(() => release with { Assets = [new ReleaseAsset(release.Assets[0].Name, bytes.Length, exeUrl), release.Assets[1]] }) });
        var launched = new List<string>();
        var built = await CreateReady(settings, checker,
            createInstaller: () => new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client(), Launch = launched.Add, TempDirectory = _root }));
        ClearPosted();

        Send(built.Bridge, new { type = "installUpdate" });
        await WaitFor(_ => true, "notice");

        Assert.Empty(launched);
        Assert.Equal(0, _host.HandoffCalls);
        Assert.Equal("error", Messages("notice").Last().GetProperty("level").GetString());
        var progress = Messages("updateInstallProgress").Last();
        Assert.Equal("Failed", progress.GetProperty("stage").GetString());
    }

    /// <summary>
    /// The real hazard this feature has to survive: install-scr.ps1's Stop-NonSaverInstances
    /// force-kills any non-/s Win4KSS process once the installer's [Run] step reaches it, and
    /// this browser is exactly such a process. On a verified install, the bridge must hand off
    /// to <see cref="IBrowserHost.PrepareForInstallerHandoff"/> — here wired to actually run the
    /// disposal sequence BrowserForm.Shutdown performs (bridge, then queue, then cache) — and
    /// queue.json plus settings.json must both come out the other side in a consistent, flushed
    /// state, not just "didn't throw". A pin queued through the ordinary setPinned path (so
    /// queue.json has a real resumable job in it) and a settings edit sitting in the debounce
    /// are both still unflushed the moment Install is clicked; neither may be lost.
    /// </summary>
    [Fact]
    public async Task A_verified_install_launches_then_hands_off_and_leaves_queue_and_settings_consistent()
    {
        var bytes = "a verified installer payload"u8.ToArray();
        var release = AvailableRelease();
        var exeUrl = release.Assets[0].DownloadUrl;
        var hashUrl = release.Assets[1].DownloadUrl;
        var http = new FixtureHttp()
            .Respond(exeUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })
            .Text(hashUrl, $"{Sha256Hex(bytes)}  Win4KSS-Setup-99.0.0.exe\n");

        var settingsPath = Path.Combine(_root, "settings.json");
        var settings = NewSettings();
        var fetchedRelease = release with { Assets = [new ReleaseAsset(release.Assets[0].Name, bytes.Length, exeUrl), release.Assets[1]] };
        var checker = new UpdateChecker(settings, new UpdateCheckerOptions { Source = new FakeUpdateSource(() => fetchedRelease) });
        var launched = new List<string>();

        // A real save delegate (writing to a temp settings.json) rather than the default
        // Interlocked.Increment fake -- this test cares whether the file on disk ends up
        // consistent, not just whether a save was attempted.
        var built = Create(settings, save: s => s.Save(settingsPath), updateChecker: checker,
            createInstaller: () => new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client(), Launch = launched.Add }));
        await built.Bridge.InitializeAsync();
        Send(built.Bridge, new { type = "ready" });

        // Let the automatic update check that opening the browser starts finish first: its own
        // save landing during the hand-off made this test flaky on a loaded machine.
        await built.Bridge.Background;

        // A pin download queued the ordinary way (queue.json now has a real, resumable job in
        // it) and a settings edit still sitting in the debounce -- exactly the two things the
        // hand-off must not lose or corrupt.
        Send(built.Bridge, new { type = "setPinned", keys = new[] { Apple.Key }, value = true });
        Assert.Equal(DownloadPriority.Pin, built.Queue.Get(Apple.Key)!.Priority);
        var newShuffle = !built.Settings.Shuffle;
        Send(built.Bridge, new { type = "saveSettings", patch = new { shuffle = newShuffle } });

        // Mirrors BrowserForm.Shutdown()'s order for the pieces that matter to data integrity:
        // bridge first (flushes the pending debounced save), then the queue, then the cache.
        _host.OnHandoff = () =>
        {
            built.Bridge.Dispose();
            built.Queue.Dispose();
            built.Cache.Dispose();
        };

        ClearPosted();
        Send(built.Bridge, new { type = "installUpdate" });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _host.HandoffCalls == 0)
            await Task.Delay(20);

        Assert.Equal(1, _host.HandoffCalls);
        Assert.Single(launched);
        Assert.True(File.Exists(launched[0])); // the verified installer was never deleted out from under us
        File.Delete(launched[0]); // ...cleaning it up once "launched" is this test's job, not the bridge's

        // queue.json: still valid JSON, and the pin job survived the disposal -- resumable
        // next run, exactly as BrowserForm.Shutdown's own comment on DownloadQueue.Dispose says
        // ("Partials and queue.json are kept, so pins resume next time").
        var queueJsonPath = Path.Combine(built.Cache.Root, DownloadQueue.FileName);
        var queueFile = JsonSerializer.Deserialize<DownloadQueue.QueueFile>(
            File.ReadAllText(queueJsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(queueFile);
        Assert.Contains(queueFile!.Jobs, j => j.ClipKey == Apple.Key);

        // settings.json: the debounced edit reached disk through this exact shutdown path.
        Assert.True(File.Exists(settingsPath));
        var reloaded = AppSettings.Load(settingsPath);
        Assert.Equal(newShuffle, reloaded.Shuffle);
        Assert.Contains(Apple.Key, reloaded.KeptOffline);
    }

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

    [Fact]
    public void Importing_shortcuts_treats_the_same_hotkey_in_another_spelling_as_no_change()
    {
        var current = new ShortcutOptions(true, true, "Alt+Ctrl+S"); // what a .lnk reports
        Assert.Equal(current, SettingsCli.Merge(current, new PortableShortcuts(true, true, "CTRL+ALT+S")));
        Assert.Equal(current, SettingsCli.Merge(current, null));
        Assert.Equal(current with { Hotkey = "CTRL+SHIFT+F5" }, SettingsCli.Merge(current, new PortableShortcuts(null, null, "Ctrl+Shift+F5")));
        Assert.Equal(current with { Hotkey = null }, SettingsCli.Merge(current, new PortableShortcuts(null, null, "")));
        Assert.Equal(current, SettingsCli.Merge(current, new PortableShortcuts(null, null, "garbage"))); // unusable: ignored
        Assert.Equal("CTRL+ALT+S", SettingsCli.ToPortable(current).Hotkey);
        Assert.Equal("", SettingsCli.ToPortable(current with { Hotkey = null }).Hotkey);
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
        public List<string?> StartNowKeys { get; } = [];
        public bool StartNowResult { get; set; } = true;
        public string? PickResult { get; set; }

        public void ShowClip(Clip? clip, string? playUrl) => Shown.Add((clip, playUrl));
        public void SetPanelRect(PanelRect rect) => Rects.Add(rect);

        public bool StartNow(string? startWithKey)
        {
            StartNowCalls++;
            StartNowKeys.Add(startWithKey);
            return StartNowResult;
        }

        public ShortcutOptions ReadShortcuts() => Shortcuts;

        public bool ApplyShortcuts(ShortcutOptions options)
        {
            Applied.Add(options);
            Shortcuts = options;
            return true;
        }

        public List<(string Hotkey, string? Previous)> Hotkeys { get; } = [];
        public string? HotkeyProblem { get; set; }

        public string? ApplyHotkey(string hotkey, string? previous)
        {
            Hotkeys.Add((hotkey, previous));
            return HotkeyProblem;
        }

        public string? PickFolder(string title, string? initialPath) => PickResult;
        public string? SavePath { get; set; }
        public string? OpenPath { get; set; }
        public string? PickSaveFile(string title, string suggestedName, string filter) => SavePath;
        public string? PickOpenFile(string title, string filter) => OpenPath;
        public void OpenFolder(string path) => Opened.Add(path);

        public int HandoffCalls { get; private set; }
        /// <summary>Set by a test to the real shutdown sequence (bridge/queue/cache disposal) BrowserForm.Shutdown would run, so the hand-off test can observe its effects.</summary>
        public Action? OnHandoff { get; set; }
        public void PrepareForInstallerHandoff()
        {
            HandoffCalls++;
            OnHandoff?.Invoke();
        }
    }
}
