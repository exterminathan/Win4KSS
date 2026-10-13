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
    /// <summary>Bumped when a migration is needed. Unknown future versions are loaded as-is.</summary>
    public int Version { get; set; } = 1;

    public SourceMode SourceMode { get; set; } = SourceMode.Apple;
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

    /// <summary>Clip keys to play (<see cref="Clip.Id"/>). Empty means "all", which is also the first-run state.</summary>
    public List<string> SelectedClipIds { get; set; } = [];

    public List<string> LocalFolders { get; set; } = [];

    /// <summary>
    /// Ids of the harvested catalogues in assets/registry to mix in (archive-org, nasa,
    /// aerial-manifests). Off by default: Apple's aerials are the point of the app, and the
    /// harvested sources are opt-in breadth rather than a surprise.
    /// </summary>
    public List<string> EnabledCatalogIds { get; set; } = [];

    /// <summary>Overrides the tarball probe. Normally null; exposed for debugging a bad manifest.</summary>
    public string? ManifestUrlOverride { get; set; }

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
    /// ffmpeg for clip-browser previews. Null (the default) searches C:fmpegfmpeg.exe,
    /// C:fmpeginfmpeg.exe, then PATH; see <see cref="Media.FfmpegLocator"/>. Without
    /// ffmpeg the browser gets remote thumbnails and placeholders, and no hover clips. Additive.
    /// </summary>
    public string? FfmpegPath { get; set; }

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

        if (SelectedClipIds.Count > 0)
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

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                           ?? new AppSettings();
            settings.KeptOffline ??= new HashSet<string>(StringComparer.Ordinal);
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

        // Temp-then-move: a crash mid-write leaves the previous good file intact.
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
        Log.Info($"Settings: saved to {path}");
    }
}
