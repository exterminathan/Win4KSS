using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win4K.Core;

public enum MultiMonitorMode
{
    /// <summary>Video on the primary display, the rest blacked out. Cheapest, and the default.</summary>
    MainOnly = 0,
    SameOnEach = 1,
    DifferentVideos = 5,
    /// <summary>One video stretched across the union of all displays.</summary>
    SpanAll = 10,
}

/// <summary>Settings v1 only: which of Apple / local folders played. Retired by v2; see <see cref="SettingsMigrator"/>.</summary>
public enum SourceMode
{
    Apple = 0,
    LocalFolders = 1,
    Both = 2,
}

/// <summary>
/// User settings, stored as JSON at %LOCALAPPDATA%\Win4KScreensavers\settings.json.
///
/// Deliberately not the registry, which is where cdima/aerial kept these. Every value there
/// was a string round-tripped through bool.Parse, so a hand-edited or partially written key
/// threw on startup; and there was no way to see the whole configuration at once.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The schema version this build writes. v1 → v2 is <see cref="SettingsMigrator.MigrateToV2"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The file the v1 settings are copied to, once, before the first v2 save overwrites them.</summary>
    public const string V1BackupFileName = "settings.v1.json";

    /// <summary>
    /// Bumped when a migration is needed. Unknown future versions are loaded as-is. A fresh
    /// settings object is already current; a file with no "version" field loads as v1.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    public VideoQuality Quality { get; set; } = VideoQuality.Auto;
    public MultiMonitorMode MultiMonitorMode { get; set; } = MultiMonitorMode.MainOnly;

    public bool Shuffle { get; set; } = true;

    /// <summary>
    /// Only 4 of Apple's 114 clips are marked day/night, so this nudges very little.
    /// See <see cref="ClipHeuristics.FromLabel"/>.
    /// </summary>
    public bool UseTimeOfDay { get; set; } = true;

    public bool CacheEnabled { get; set; } = true;
    public string? CacheDirectory { get; set; }
    public double CacheBudgetGb { get; set; } = 20.0;

    /// <summary>
    /// Folders whose video files are clips ("local:&lt;path&gt;" keys), rescanned on every run.
    /// Their source id is <see cref="Rotation.LocalSourceId"/>, auto-add by default, so a file
    /// dropped into a folder joins the rotation on the next run.
    /// </summary>
    public List<string> LocalFolders { get; set; } = [];

    // ---- Retired v1 fields. Read from v1 files by SettingsMigrator, nulled by the migration,
    // and never written again (null is skipped on write). Do not build on them.

    /// <summary>v1 only: which of Apple / local folders played. Null in v2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceMode? SourceMode { get; set; }

    /// <summary>v1 only: clip keys to play, empty meaning "all". Null in v2; see <see cref="RotationKeys"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SelectedClipIds { get; set; }

    /// <summary>v1 only: registry catalogues mixed in (archive-org, nasa, aerial-manifests). Null in v2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EnabledCatalogIds { get; set; }

    /// <summary>Overrides the tarball probe. Normally null; exposed for debugging a bad manifest.</summary>
    public string? ManifestUrlOverride { get; set; }

    // ---- updates (Win4K.Core.Updates.UpdateChecker) -----------------------------------------
    // Additive, so files without these load with the defaults below. Deliberately NOT part of
    // SettingsPortability's export/import (apart from AutoCheckForUpdates): the last-check time
    // and result describe this machine's own history, not a choice worth carrying to another one.

    /// <summary>
    /// Whether the clip browser checks for app updates on open, at most once every 24 hours
    /// (<see cref="Updates.UpdateChecker"/>). The manual "Check now" button in Settings &gt; About
    /// works regardless of this. Never consulted outside the browser — see CLAUDE.md "Never
    /// during the saver" and <c>Program.AllowsUpdateCheck</c>. TODO(review): exact placement
    /// (its own settings section vs. folded into "about") is a judgment call; it travels in
    /// SettingsPortability's "playback" section for now.
    /// </summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>When the update checker last actually ran (UTC), successful or not. Null before the first check.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>The latest version the last check found, if any (regardless of whether it was newer). Null if there was no release to compare, or the check failed.</summary>
    public string? LastUpdateLatestVersion { get; set; }

    /// <summary>Whether <see cref="LastUpdateLatestVersion"/> was newer than this build, as of <see cref="LastUpdateCheckUtc"/>.</summary>
    public bool LastUpdateAvailable { get; set; }

    /// <summary>The newer release's notes (its GitHub release body), when <see cref="LastUpdateAvailable"/> is true. Null otherwise.</summary>
    public string? LastUpdateNotes { get; set; }

    /// <summary>
    /// Clip key (<see cref="Clip.Id"/>) → number of sessions in which the clip failed twice and was benched. Written by
    /// <see cref="Playback.ClipHealth"/>; 3 or more means "unhealthy". Additive, so v1 files
    /// without it load as empty.
    /// </summary>
    public Dictionary<string, int> FailedClips { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Clip keys (<see cref="Clip.Id"/>) kept offline ("pinned"): downloaded at the current
    /// quality and never evicted. They count against <see cref="CacheBudgetGb"/>; the rolling
    /// cache gets what is left. Managed through <see cref="Caching.PinManager"/>, which refuses
    /// a pin that would not fit. Additive, so files without it load as empty; settings v2
    /// (Phase F) adopts it as-is.
    /// </summary>
    public HashSet<string> KeptOffline { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// ffmpeg for clip-browser previews. Null (the default) searches C:\ffmpeg\ffmpeg.exe,
    /// C:\ffmpeg\bin\ffmpeg.exe, then PATH; see <see cref="Media.FfmpegLocator"/>. Without
    /// ffmpeg the browser gets remote thumbnails and placeholders, and no hover clips. Additive.
    /// </summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// The global "Start now" hotkey in the "CTRL+ALT+S" form, held by the resident hotkey
    /// listener (<c>Win4KSS.exe /hotkey</c>). "" means no hotkey. Null means never set: the
    /// default applies, and the listener first adopts whatever the old Start Screen Saver
    /// shortcuts carried (until v1.2.6 the hotkey was a field on those .lnk files). A null
    /// never overwrites a value already on disk (see <see cref="Save"/>), so a process that
    /// loaded its settings before the listener migrated cannot quietly undo the migration.
    /// </summary>
    public string? StartNowHotkey { get; set; }

    // ---- Settings v2: the rotation is per clip. Edit these through Rotation (SetInRotation,
    // SetAutoAdd, MergeNewKeys) rather than directly; it keeps the rules in one place.

    /// <summary>
    /// Clip keys (<see cref="Clip.Id"/>) the saver plays, per clip, across all sources. Keys
    /// the catalogue no longer lists are ignored, not removed. When none match, the saver
    /// falls back to cached clips, then Apple (<see cref="Rotation.Select"/>).
    /// </summary>
    public HashSet<string> RotationKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Source ids (<see cref="Rotation.SourcesOf"/>: "apple", "aerial-manifests", "archive-org",
    /// "nasa", "local") whose new clips join <see cref="RotationKeys"/> automatically. A fresh
    /// install auto-adds Apple and local folders, which is what makes its first run play Apple.
    /// </summary>
    public HashSet<string> AutoAddSources { get; set; } =
        new(StringComparer.Ordinal) { AppleAerialSource.SourceId, Rotation.LocalSourceId };

    /// <summary>
    /// Every clip key seen so far, for auto-add detection only (<see cref="Rotation.MergeNewKeys"/>):
    /// a catalogue key missing from here is new, and joins the rotation if its source auto-adds.
    /// The saver fills it on every run, so it is NOT the browser's "New" tag; that comes from
    /// <see cref="ClipInfo.AddedUtc"/> (<see cref="Catalogs.TagBuilder.IsNew"/>).
    /// </summary>
    public HashSet<string> KnownKeys { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string ResolvedCacheDirectory =>
        string.IsNullOrWhiteSpace(CacheDirectory) ? AppPaths.DefaultCacheDirectory : CacheDirectory;

    [JsonIgnore]
    public long CacheBudgetBytes => (long)(CacheBudgetGb * 1024 * 1024 * 1024);

    // ---- clip-key migration ------------------------------------------------------------

    /// <summary>
    /// Rewrites clip ids saved before clip keys existed — a bare Apple GUID, "ia:&lt;item&gt;:&lt;file&gt;",
    /// "nasa:&lt;file&gt;", a bare local path — into the current keys ("apple:&lt;GUID&gt;",
    /// "ia:&lt;item&gt;/&lt;file&gt;", "nasa:&lt;id&gt;", "local:&lt;path&gt;"). See <see cref="Catalogs.ClipKey.FromLegacy"/>.
    ///
    /// Idempotent and cheap, so it runs on every <see cref="Load"/> rather than behind a
    /// version bump (that is reserved for the settings v2 migration). It is not saved back
    /// on its own; the next <see cref="Save"/> persists it.
    /// </summary>
    /// <param name="resolveBare">Maps an ambiguous bare vendor id to its key; defaults to a lookup in the aerial-manifests catalogue.</param>
    /// <returns>True if anything changed.</returns>
    public bool MigrateClipKeys(Func<string, string?>? resolveBare = null)
    {
        resolveBare ??= Catalogs.ClipKey.ResolveFromCatalogues;
        var changed = false;

        string Map(string id)
        {
            var key = Catalogs.ClipKey.FromLegacy(id, resolveBare);
            changed |= !string.Equals(key, id, StringComparison.Ordinal);
            return key;
        }

        if (SelectedClipIds is { Count: > 0 })
            SelectedClipIds = SelectedClipIds.Select(Map).Distinct(StringComparer.Ordinal).ToList();

        if (FailedClips.Count > 0)
        {
            var migrated = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, count) in FailedClips)
            {
                // Two legacy ids for one clip (Apple's GUID via the tarball and via tvOS 26)
                // counted the same sessions; keep the larger count rather than adding them.
                var key = Map(id);
                migrated[key] = Math.Max(count, migrated.GetValueOrDefault(key));
            }
            FailedClips = migrated;
        }

        return changed;
    }

    // ---- persistence -----------------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="path">Defaults to settings.json under the app root; tests pass a temp file.</param>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= AppPaths.SettingsFile;
            if (!File.Exists(path))
                return new AppSettings();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();

            // A fresh object is current, so a file that never said which version it is would
            // skip the v1 -> v2 migration. Every file written so far does say; this is for a
            // hand-written one.
            if (ReadVersion(json) is null)
                settings.Version = 1;

            settings.KeptOffline ??= new HashSet<string>(StringComparer.Ordinal);
            settings.RotationKeys ??= new HashSet<string>(StringComparer.Ordinal);
            settings.AutoAddSources ??= new HashSet<string>(StringComparer.Ordinal);
            settings.KnownKeys ??= new HashSet<string>(StringComparer.Ordinal);
            settings.MigrateClipKeys();
            return settings;
        }
        catch (Exception ex)
        {
            // A corrupt settings file must never stop the screen saver from running;
            // fall back to defaults and say so in the log.
            Log.Error("Settings: load failed, using defaults", ex);
            return new AppSettings();
        }
    }

    /// <param name="path">Defaults to settings.json under the app root; tests pass a temp file.</param>
    public void Save(string? path = null)
    {
        if (path is null)
        {
            AppPaths.EnsureRoot();
            path = AppPaths.SettingsFile;
        }
        var temp = path + ".tmp";

        // The first v2 save over a v1 file keeps the v1 file, once, as settings.v1.json.
        if (Version >= 2)
            BackUpV1(path);

        // Another process (the hotkey listener, the browser) may have set the hotkey since this
        // one loaded; null only means "never set", so it must not erase that.
        if (StartNowHotkey is null && File.Exists(path))
            StartNowHotkey = ReadStringField(path, nameof(StartNowHotkey));

        // Temp-then-move: a crash mid-write leaves the previous good file intact.
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
        Log.Info($"Settings: saved to {path}");
    }

    /// <summary>A top-level string field of a settings file, or null if it is absent or unreadable.</summary>
    private static string? ReadStringField(string path, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The "version" field of a settings file's JSON, or null if it has none (or is not JSON).</summary>
    private static int? ReadVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(nameof(Version), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.TryGetInt32(out var version))
                {
                    return version;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// Copies a v1 settings file to settings.v1.json beside it, unless that backup already
    /// exists: the first backup is the one worth keeping. Never throws; a failed backup is
    /// logged and must not stop the save.
    /// </summary>
    private static void BackUpV1(string path)
    {
        try
        {
            var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, V1BackupFileName);
            if (!File.Exists(path) || File.Exists(backup))
                return;

            if (ReadVersion(File.ReadAllText(path)) is >= 2)
                return;

            File.Copy(path, backup, overwrite: false);
            Log.Info($"Settings: backed up v1 settings to {backup}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: could not back up v1 settings: {ex.Message}");
        }
    }
}
