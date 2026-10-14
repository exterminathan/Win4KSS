using System.Text.Json;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Playback;

namespace Win4K.Tests;

/// <summary>
/// Settings v2: the v1 → v2 migration (from today's real settings.json shape), the per-clip
/// rotation, auto-add of new clips, the saver's fallbacks, and ClipResolver without SourceMode.
/// </summary>
public class SettingsV2Tests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsFile;

    public SettingsV2Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-v2-" + Guid.NewGuid().ToString("N")[..8]);
        _settingsFile = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- a small catalogue with one of everything ----------------------------------------

    private const string LocalFolder = @"C:\Videos\Aerials";
    private static readonly string LocalFile = Path.Combine(LocalFolder, "harbour.mp4");

    private static ClipInfo Apple(string guid) => new()
    {
        Key = ClipKey.ForApple(guid),
        SourceId = "apple",
        SourceIds = ["apple", "apple-tvos26"], // merged with the tvOS 26 listing, as in the real catalogue
        CatalogId = "apple",
        Title = guid,
        Urls = new ClipUrls(Uhd4KSdr: $"https://sylvan.apple.com/{guid}_4K_SDR.mov",
                            Uhd4KHdr: $"https://sylvan.apple.com/{guid}_4K_HDR.mov",
                            Hd1080Sdr: $"https://sylvan.apple.com/{guid}_1080_SDR.mov"),
    };

    private static ClipInfo TvOsOnly(string guid) => new()
    {
        Key = ClipKey.ForApple(guid),
        SourceId = "apple-tvos26",
        SourceIds = ["apple-tvos26"],
        CatalogId = "aerial-manifests",
        Title = guid,
        Urls = new ClipUrls(Uhd4KSdr: $"https://sylvan.apple.com/tvos/{guid}.mov"),
    };

    private static ClipInfo Amazon(string id) => new()
    {
        Key = "amazon:" + id,
        SourceId = "amazon-fireos8",
        SourceIds = ["amazon-fireos8"],
        CatalogId = "aerial-manifests",
        Title = id,
        Urls = new ClipUrls(Uhd4KSdr: $"https://amazon.example/{id}.mp4"),
    };

    private static ClipInfo Archive(string item, string file) => new()
    {
        Key = ClipKey.ForArchive(item, file),
        SourceId = "archive-org",
        SourceIds = ["archive-org"],
        CatalogId = "archive-org",
        Title = file,
        Urls = new ClipUrls(Uhd4KSdr: $"https://archive.org/download/{item}/{Uri.EscapeDataString(file)}"),
    };

    private static ClipInfo Nasa(string id) => new()
    {
        Key = ClipKey.ForNasa(id),
        SourceId = "nasa",
        SourceIds = ["nasa"],
        CatalogId = "nasa",
        Title = id,
        Urls = new ClipUrls(Uhd4KSdr: $"https://images-assets.nasa.gov/video/{id}/{id}~orig.mp4"),
    };

    private static ClipInfo Local(string path) => new()
    {
        Key = ClipKey.ForLocal(path),
        SourceId = "local",
        SourceIds = ["local"],
        CatalogId = LocalFolderSource.SourceIdPrefix + Path.GetDirectoryName(path),
        Title = Path.GetFileNameWithoutExtension(path),
        Category = "Local",
        LocalPath = path,
    };

    private static readonly ClipInfo A1 = Apple("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly ClipInfo A2 = Apple("AAAAAAAA-0000-0000-0000-000000000002");
    private static readonly ClipInfo T1 = TvOsOnly("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly ClipInfo Z1 = Amazon("content-z1");
    private static readonly ClipInfo I1 = Archive("Water614KRes", "Water 61 - 4K res.mp4");
    private static readonly ClipInfo I2 = Archive("Fire7345s4kRes", "Fire 66 - 45s - 4k res.mp4");
    private static readonly ClipInfo N1 = Nasa("322_SS101-CEO");
    private static readonly ClipInfo L1 = Local(LocalFile);

    private static IReadOnlyList<ClipInfo> Catalogue => [A1, A2, T1, Z1, I1, I2, N1, L1];

    private static string[] Keys(params ClipInfo[] clips) => clips.Select(c => c.Key).Order(StringComparer.Ordinal).ToArray();

    private static string[] Sorted(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Today's real settings.json, verbatim apart from the substituted fields.</summary>
    private static string V1Json(string sourceMode = "Apple", string enabledCatalogIds = "\"archive-org\"",
                                 string selectedClipIds = "", string localFolders = "") => $$"""
        {
          "version": 1,
          "sourceMode": "{{sourceMode}}",
          "quality": "Auto",
          "multiMonitorMode": "MainOnly",
          "shuffle": true,
          "useTimeOfDay": true,
          "cacheEnabled": true,
          "cacheDirectory": "C:\\Users\\Nathan\\AppData\\Local\\Win4KScreensavers\\cache",
          "cacheBudgetGb": 20,
          "selectedClipIds": [{{selectedClipIds}}],
          "localFolders": [{{localFolders}}],
          "enabledCatalogIds": [
            {{enabledCatalogIds}}
          ]
        }
        """;

    private AppSettings LoadV1(string json)
    {
        File.WriteAllText(_settingsFile, json);
        var settings = AppSettings.Load(_settingsFile);
        Assert.Equal(1, settings.Version);
        return settings;
    }

    // ---- v1 → v2 migration -----------------------------------------------------------------

    [Fact]
    public void Apple_only_migrates_to_apple_clips_and_apple_auto_add()
    {
        var settings = LoadV1(V1Json(enabledCatalogIds: ""));

        Assert.True(SettingsMigrator.MigrateToV2(settings, Catalogue));

        Assert.Equal(2, settings.Version);
        // Not T1: a tvOS-only clip came from aerial-manifests, which v1 had off.
        Assert.Equal(Keys(A1, A2), Sorted(settings.RotationKeys));
        Assert.Equal(["apple", "local"], Sorted(settings.AutoAddSources)); // local: on by default, no folders configured
        Assert.Equal(Keys([.. Catalogue]), Sorted(settings.KnownKeys));
    }

    [Fact]
    public void Apple_plus_archive_org_migrates_to_both_sources_clips()
    {
        // Exactly today's settings.json.
        var settings = LoadV1(V1Json());

        Assert.True(SettingsMigrator.MigrateToV2(settings, Catalogue));

        Assert.Equal(Keys(A1, A2, I1, I2), Sorted(settings.RotationKeys));
        Assert.Equal(["apple", "archive-org", "local"], Sorted(settings.AutoAddSources));
        Assert.Equal(Keys([.. Catalogue]), Sorted(settings.KnownKeys));
        Assert.Null(settings.SourceMode);
        Assert.Null(settings.SelectedClipIds);
        Assert.Null(settings.EnabledCatalogIds);
    }

    [Fact]
    public void Aerial_manifests_brings_its_apple_guids_even_with_apple_off()
    {
        // v1 resolved aerial-manifests' tvOS 26 feed, Apple GUIDs included, without the tarball.
        var settings = LoadV1(V1Json(sourceMode: "LocalFolders", enabledCatalogIds: "\"aerial-manifests\""));

        // No folders configured, so the library lists no local clips.
        SettingsMigrator.MigrateToV2(settings, Catalogue.Where(c => !c.IsLocal).ToList());

        Assert.Equal(Keys(A1, A2, T1, Z1), Sorted(settings.RotationKeys));
        Assert.Equal(["aerial-manifests", "local"], Sorted(settings.AutoAddSources));
    }

    [Fact]
    public void A_non_empty_selection_narrows_the_rotation_case_insensitively()
    {
        // v1 matched the selection case-insensitively, and only among enabled sources: N1 was
        // selected but NASA was off, so it never played and must not start playing now.
        var selected = $"\"{A1.Key.ToLowerInvariant()}\", \"{I2.Key}\", \"{N1.Key}\"";
        var settings = LoadV1(V1Json(selectedClipIds: selected));

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(A1, I2), Sorted(settings.RotationKeys));
        Assert.Equal(["apple", "archive-org", "local"], Sorted(settings.AutoAddSources));
    }

    [Fact]
    public void A_stale_selection_migrates_to_everything_v1_played()
    {
        // v1 ignored a selection that matched nothing rather than show a black screen.
        var settings = LoadV1(V1Json(selectedClipIds: "\"apple:GONE\""));

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(A1, A2, I1, I2), Sorted(settings.RotationKeys));
    }

    [Fact]
    public void Local_folders_in_both_mode_join_the_rotation_and_auto_add()
    {
        var folder = JsonSerializer.Serialize(LocalFolder);
        var settings = LoadV1(V1Json(sourceMode: "Both", enabledCatalogIds: "", localFolders: folder));

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(A1, A2, L1), Sorted(settings.RotationKeys));
        Assert.Equal(["apple", "local"], Sorted(settings.AutoAddSources));
        Assert.Equal([LocalFolder], settings.LocalFolders);
    }

    [Fact]
    public void Local_folders_switched_off_in_v1_stay_off()
    {
        // Folders configured but SourceMode Apple: their files never played, and auto-adding
        // new ones would be inconsistent with the existing ones staying out.
        var folder = JsonSerializer.Serialize(LocalFolder);
        var settings = LoadV1(V1Json(sourceMode: "Apple", enabledCatalogIds: "", localFolders: folder));

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(A1, A2), Sorted(settings.RotationKeys));
        Assert.Equal(["apple"], Sorted(settings.AutoAddSources));
        Assert.Contains(L1.Key, settings.KnownKeys);
    }

    [Fact]
    public void Local_folders_only_mode_migrates_to_local_clips()
    {
        var folder = JsonSerializer.Serialize(LocalFolder);
        var settings = LoadV1(V1Json(sourceMode: "LocalFolders", enabledCatalogIds: "", localFolders: folder));

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(L1), Sorted(settings.RotationKeys));
        Assert.Equal(["local"], Sorted(settings.AutoAddSources));
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        var settings = LoadV1(V1Json());
        Assert.True(SettingsMigrator.Prepare(settings, Catalogue));

        var rotation = Sorted(settings.RotationKeys);
        var autoAdd = Sorted(settings.AutoAddSources);
        var known = Sorted(settings.KnownKeys);

        Assert.False(SettingsMigrator.MigrateToV2(settings, Catalogue));
        Assert.False(SettingsMigrator.Prepare(settings, Catalogue));

        // And across a save and a reload.
        settings.Save(_settingsFile);
        var reloaded = AppSettings.Load(_settingsFile);
        Assert.False(SettingsMigrator.Prepare(reloaded, Catalogue));

        Assert.Equal(rotation, Sorted(reloaded.RotationKeys));
        Assert.Equal(autoAdd, Sorted(reloaded.AutoAddSources));
        Assert.Equal(known, Sorted(reloaded.KnownKeys));
    }

    [Fact]
    public void Migration_waits_for_a_catalogue()
    {
        var settings = LoadV1(V1Json());

        Assert.False(SettingsMigrator.MigrateToV2(settings, []));
        Assert.Equal(1, settings.Version);
        Assert.Equal(["archive-org"], settings.EnabledCatalogIds!);

        // An unmigrated v1 must not have every clip auto-added as "new" meanwhile.
        Assert.Equal((0, 0), Rotation.MergeNewKeys(settings, Catalogue));
        Assert.Empty(settings.RotationKeys);
    }

    [Fact]
    public void A_file_without_a_version_loads_as_v1()
    {
        var settings = LoadV1("""{ "sourceMode": "Apple", "enabledCatalogIds": ["nasa"] }""");

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Equal(Keys(A1, A2, N1), Sorted(settings.RotationKeys));
    }

    [Fact]
    public void Failure_counts_use_the_same_keys_as_the_rotation()
    {
        // A legacy archive id in failedClips migrates on load to the key the rotation uses.
        var json = V1Json().Replace("\"version\": 1,", "\"version\": 1, \"failedClips\": { \"ia:Water614KRes:Water 61 - 4K res.mp4\": 3 },");
        var settings = LoadV1(json);

        SettingsMigrator.MigrateToV2(settings, Catalogue);

        Assert.Contains(I1.Key, settings.RotationKeys);
        Assert.True(ClipHealth.IsUnhealthy(settings, I1.Key));
        // Unhealthy clips stay in the rotation; the playlist benches them per session.
        Assert.Contains(I1.Key, Rotation.Select(settings, Catalogue).Infos.Select(i => i.Key));
    }

    // ---- backup and round trip -------------------------------------------------------------

    [Fact]
    public void The_v1_file_is_backed_up_once_before_the_first_v2_save()
    {
        var original = V1Json();
        var settings = LoadV1(original);
        var backup = Path.Combine(_root, AppSettings.V1BackupFileName);

        SettingsMigrator.MigrateToV2(settings, Catalogue);
        Assert.False(File.Exists(backup)); // migrating alone writes nothing

        settings.Save(_settingsFile);
        Assert.Equal(original, File.ReadAllText(backup));

        // Later v2 saves leave the backup alone.
        settings.CacheBudgetGb = 50;
        settings.Save(_settingsFile);
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void An_existing_backup_is_never_overwritten()
    {
        var backup = Path.Combine(_root, AppSettings.V1BackupFileName);
        File.WriteAllText(backup, "the first v1 file");

        var settings = LoadV1(V1Json());
        SettingsMigrator.MigrateToV2(settings, Catalogue);
        settings.Save(_settingsFile);

        Assert.Equal("the first v1 file", File.ReadAllText(backup));
    }

    [Fact]
    public void A_fresh_install_writes_no_backup()
    {
        new AppSettings().Save(_settingsFile);
        new AppSettings().Save(_settingsFile);

        Assert.False(File.Exists(Path.Combine(_root, AppSettings.V1BackupFileName)));
    }

    [Fact]
    public void V2_round_trip_drops_the_retired_fields()
    {
        var settings = LoadV1(V1Json(selectedClipIds: $"\"{A1.Key}\""));
        SettingsMigrator.MigrateToV2(settings, Catalogue);
        settings.Save(_settingsFile);

        var json = File.ReadAllText(_settingsFile);
        Assert.DoesNotContain("sourceMode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selectedClipIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enabledCatalogIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"rotationKeys\"", json);
        Assert.Contains("\"autoAddSources\"", json);
        Assert.Contains("\"knownKeys\"", json);

        var reloaded = AppSettings.Load(_settingsFile);
        Assert.Equal(2, reloaded.Version);
        Assert.Null(reloaded.SourceMode);
        Assert.Null(reloaded.SelectedClipIds);
        Assert.Null(reloaded.EnabledCatalogIds);
        Assert.Equal(Keys(A1), Sorted(reloaded.RotationKeys));
        Assert.Equal(Sorted(settings.AutoAddSources), Sorted(reloaded.AutoAddSources));
        Assert.Equal(Sorted(settings.KnownKeys), Sorted(reloaded.KnownKeys));
        Assert.Equal(20, reloaded.CacheBudgetGb);
    }

    [Fact]
    public void A_cleared_auto_add_set_stays_cleared()
    {
        // The fresh-install default ({apple, local}) must not come back for a user who emptied it.
        var settings = new AppSettings();
        settings.AutoAddSources.Clear();
        settings.Save(_settingsFile);

        Assert.Empty(AppSettings.Load(_settingsFile).AutoAddSources);
    }

    // ---- auto-add of new keys ----------------------------------------------------------------

    private static AppSettings Migrated(params ClipInfo[] known) => new()
    {
        RotationKeys = new(known.Select(c => c.Key), StringComparer.Ordinal),
        KnownKeys = new(known.Select(c => c.Key), StringComparer.Ordinal),
        AutoAddSources = new(["apple", "archive-org"], StringComparer.Ordinal),
    };

    [Fact]
    public void New_clips_from_auto_add_sources_join_the_rotation()
    {
        var settings = Migrated(A1, I1);

        var (added, newlyKnown) = Rotation.MergeNewKeys(settings, Catalogue);

        // A2 (apple) and I2 (archive-org) are new from auto-add sources; T1/Z1 are
        // aerial-manifests, N1 NASA, L1 local — new, but not auto-added.
        Assert.Equal(2, added);
        Assert.Equal(6, newlyKnown);
        Assert.Equal(Keys(A1, A2, I1, I2), Sorted(settings.RotationKeys));
        Assert.Equal(Keys([.. Catalogue]), Sorted(settings.KnownKeys));
    }

    [Fact]
    public void A_clip_the_user_removed_is_not_added_back()
    {
        var settings = Migrated(A1, A2, I1);
        Rotation.SetInRotation(settings, [A2.Key], false);

        Rotation.MergeNewKeys(settings, Catalogue);

        Assert.DoesNotContain(A2.Key, settings.RotationKeys); // known, so not new
    }

    [Fact]
    public void Merging_again_changes_nothing()
    {
        var settings = Migrated(A1);
        Rotation.MergeNewKeys(settings, Catalogue);

        Assert.Equal((0, 0), Rotation.MergeNewKeys(settings, Catalogue));
        Assert.False(SettingsMigrator.Prepare(settings, Catalogue));
    }

    [Fact]
    public void New_files_in_a_local_folder_auto_add_by_default()
    {
        var settings = Migrated(A1);
        Rotation.SetAutoAdd(settings, "local", true);
        var newFile = Local(Path.Combine(LocalFolder, "new-today.mp4"));

        Rotation.MergeNewKeys(settings, [A1, newFile]);

        Assert.Contains(newFile.Key, settings.RotationKeys);
    }

    [Fact]
    public void A_fresh_install_plays_apple_and_local_folders()
    {
        var settings = new AppSettings();
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);

        Assert.True(SettingsMigrator.Prepare(settings, Catalogue));

        Assert.Equal(Keys(A1, A2, L1), Sorted(settings.RotationKeys));
    }

    [Fact]
    public void Auto_add_toggles_accept_catalogue_and_registry_ids()
    {
        var settings = new AppSettings { AutoAddSources = new(StringComparer.Ordinal) };

        Assert.True(Rotation.SetAutoAdd(settings, "registry:nasa", true));
        Assert.False(Rotation.SetAutoAdd(settings, "NASA", true)); // same source
        Assert.True(Rotation.SetAutoAdd(settings, LocalFolderSource.SourceIdPrefix + LocalFolder, true));

        Assert.Equal(["local", "nasa"], Sorted(settings.AutoAddSources));
    }

    [Fact]
    public void Sources_of_a_clip_cover_every_catalogue_listing_it()
    {
        Assert.Equal(["aerial-manifests", "apple"], Sorted(Rotation.SourcesOf(A1)));
        Assert.Equal(["aerial-manifests"], Sorted(Rotation.SourcesOf(T1)));
        Assert.Equal(["aerial-manifests"], Sorted(Rotation.SourcesOf(Z1)));
        Assert.Equal(["archive-org"], Sorted(Rotation.SourcesOf(I1)));
        Assert.Equal(["local"], Sorted(Rotation.SourcesOf(L1)));
        Assert.Equal("apple", Rotation.SourceOf(A1));

        Assert.Equal(Keys(A1, A2, T1, Z1), Sorted(Rotation.KeysOf(Catalogue, "aerial-manifests")));
    }

    // ---- what the saver plays: rotation, then the fallbacks ---------------------------------

    [Fact]
    public void The_saver_plays_only_the_rotation()
    {
        var settings = Migrated(A2, I1, L1);

        var selection = Rotation.Select(settings, Catalogue, _ => true);

        Assert.Equal(RotationFallback.None, selection.Fallback);
        Assert.Equal(Keys(A2, I1, L1), Sorted(selection.Clips.Select(c => c.Id)));
    }

    [Fact]
    public void An_empty_rotation_falls_back_to_apple_even_with_clips_cached()
    {
        var settings = Migrated();
        var cachedUrl = new Uri(I2.Urls.Uhd4KSdr!);

        var selection = Rotation.Select(settings, Catalogue, clip => clip.Url == cachedUrl);

        // The cache is a convenience, never the pool: cached-first replayed the same few clips.
        Assert.Equal(RotationFallback.Apple, selection.Fallback);
        Assert.Equal(Keys(A1, A2), Sorted(selection.Clips.Select(c => c.Id)));
    }

    [Fact]
    public void Without_apple_an_empty_rotation_falls_back_to_cached_clips()
    {
        var cachedUrl = new Uri(I2.Urls.Uhd4KSdr!);

        var selection = Rotation.Select(Migrated(), [I1, I2, N1], clip => clip.Url == cachedUrl);

        Assert.Equal(RotationFallback.Cached, selection.Fallback);
        Assert.Equal([I2.Key], selection.Clips.Select(c => c.Id));
    }

    [Fact]
    public void A_rotation_of_only_stale_keys_counts_as_empty()
    {
        var settings = Migrated();
        settings.RotationKeys.Add("apple:NO-LONGER-LISTED");

        var selection = Rotation.Select(settings, Catalogue, _ => false);

        Assert.Equal(RotationFallback.Apple, selection.Fallback);
    }

    [Fact]
    public void Nothing_cached_falls_back_to_apple()
    {
        var settings = Migrated();

        var selection = Rotation.Select(settings, Catalogue, _ => false);

        // Local files are on disk but are not "cached"; T1 is Apple footage but from tvOS 26.
        Assert.Equal(RotationFallback.Apple, selection.Fallback);
        Assert.Equal(Keys(A1, A2), Sorted(selection.Clips.Select(c => c.Id)));
    }

    [Fact]
    public void Without_a_cache_the_fallback_goes_straight_to_apple()
    {
        var selection = Rotation.Select(Migrated(), Catalogue, isCached: null);

        Assert.Equal(RotationFallback.Apple, selection.Fallback);
    }

    [Fact]
    public void Without_apple_the_last_resort_is_everything()
    {
        var selection = Rotation.Select(Migrated(), [I1, N1], _ => false);

        Assert.Equal(RotationFallback.Everything, selection.Fallback);
        Assert.Equal(Keys(I1, N1), Sorted(selection.Clips.Select(c => c.Id)));
    }

    // ---- ClipResolver without SourceMode ------------------------------------------------------

    [Fact]
    public void Quality_rule_is_shared_by_the_saver_and_pins()
    {
        // Apple follows the user's quality; harvested catalogues play their 4K SDR file.
        var apple = ClipResolver.PlayableClip(A1, VideoQuality.Hd1080Sdr)!;
        var archive = ClipResolver.PlayableClip(I1, VideoQuality.Hd1080Sdr)!;

        Assert.EndsWith("_1080_SDR.mov", apple.PlayableUrl);
        Assert.Equal(I1.Urls.Uhd4KSdr, archive.PlayableUrl);

        foreach (var info in Catalogue.Where(c => !c.IsLocal))
        {
            foreach (var quality in Enum.GetValues<VideoQuality>().Where(q => q != VideoQuality.Auto))
                Assert.Equal(ClipResolver.PlayableClip(info, quality), PinManager.ClipFor(info, quality));
        }

        Assert.Null(PinManager.ClipFor(L1, VideoQuality.Uhd4KSdr)); // nothing to download
        Assert.True(ClipResolver.PlayableClip(L1, VideoQuality.Uhd4KSdr)!.IsLocal);
    }

    [Fact]
    public void Resolver_turns_the_whole_catalogue_into_clips()
    {
        var noUrl = new ClipInfo { Key = "nasa:empty", SourceId = "nasa", CatalogId = "nasa", Title = "empty" };

        var clips = ClipResolver.ToClips([.. Catalogue, noUrl], VideoQuality.Uhd4KSdr);

        Assert.Equal(Keys([.. Catalogue]), Sorted(clips.Select(c => c.Id)));
    }

    [Fact]
    public void Prepare_and_save_keeps_an_edit_made_by_another_process()
    {
        var settings = Migrated(A1);
        settings.Save(_settingsFile);
        var snapshot = AppSettings.Load(_settingsFile);

        // The browser saves while the saver is loading the catalogue.
        var browser = AppSettings.Load(_settingsFile);
        browser.CacheBudgetGb = 64;
        Rotation.SetInRotation(browser, [N1.Key], true);
        browser.Save(_settingsFile);

        var result = SettingsMigrator.PrepareAndSave(snapshot, Catalogue, _settingsFile);

        var onDisk = AppSettings.Load(_settingsFile);
        Assert.Equal(64, onDisk.CacheBudgetGb);
        Assert.Contains(N1.Key, onDisk.RotationKeys);
        Assert.Contains(A2.Key, onDisk.RotationKeys); // auto-added by this run
        Assert.Equal(Sorted(onDisk.RotationKeys), Sorted(result.RotationKeys));
    }

    [Fact]
    public void Prepare_and_save_writes_nothing_when_nothing_changed()
    {
        var settings = Migrated([.. Catalogue]);
        settings.Save(_settingsFile);
        var stamp = File.GetLastWriteTimeUtc(_settingsFile);
        File.SetLastWriteTimeUtc(_settingsFile, stamp.AddMinutes(-5));

        var result = SettingsMigrator.PrepareAndSave(AppSettings.Load(_settingsFile), Catalogue, _settingsFile);

        Assert.Equal(stamp.AddMinutes(-5), File.GetLastWriteTimeUtc(_settingsFile));
        Assert.Equal(Keys([.. Catalogue]), Sorted(result.KnownKeys));
    }
}
