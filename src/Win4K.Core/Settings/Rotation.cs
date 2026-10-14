using Win4K.Core.Catalogs;

namespace Win4K.Core;

/// <summary>Which rule chose the clips the saver plays; see <see cref="Rotation.Select"/>.</summary>
public enum RotationFallback
{
    /// <summary>The rotation itself: <see cref="AppSettings.RotationKeys"/> matched at least one clip.</summary>
    None = 0,

    /// <summary>Nothing in the rotation and no Apple aerials known, so every clip already in the cache.</summary>
    Cached = 1,

    /// <summary>The rotation matched nothing, so Apple's aerials.</summary>
    Apple = 2,

    /// <summary>Not even Apple: the whole catalogue, rather than a black screen.</summary>
    Everything = 3,
}

/// <param name="Infos">The chosen clips.</param>
/// <param name="Clips">Their playable projections at the settings' quality (<see cref="ClipResolver.PlayableClip"/>).</param>
/// <param name="Fallback">Which rule chose them.</param>
public sealed record RotationSelection(IReadOnlyList<ClipInfo> Infos, IReadOnlyList<Clip> Clips, RotationFallback Fallback);

/// <summary>
/// The per-clip rotation of settings v2: what the saver plays, and how new clips join it.
///
/// <para><b>Source ids.</b> Auto-add works per source, at the granularity the user thinks in:
/// "apple" (the resources-16 tarball), each registry catalogue ("aerial-manifests",
/// "archive-org", "nasa"), and "local" for every local folder. A clip belongs to every source
/// that lists it (<see cref="SourcesOf"/>): Apple's aerials are also in aerial-manifests' tvOS 26
/// feed, so they belong to both.</para>
///
/// <para><b>New vs known.</b> <see cref="AppSettings.KnownKeys"/> exists only to detect clips the
/// catalogue gained since the last run (<see cref="MergeNewKeys"/>), and the saver fills it on
/// every run. So the browser's "New" tag must not come from it: it comes from
/// <see cref="ClipInfo.AddedUtc"/> within <see cref="TagBuilder.NewWindow"/>
/// (<see cref="TagBuilder.IsNew"/>, which <see cref="TagBuilder.Build"/> applies when given a now).</para>
///
/// <para><b>Health.</b> Unhealthy clips (<see cref="Playback.ClipHealth"/>) stay in the rotation;
/// <see cref="AppSettings.FailedClips"/> is keyed by the same clip keys.</para>
/// </summary>
public static class Rotation
{
    /// <summary>The source id of every local folder (<see cref="LocalFolderSource"/>).</summary>
    public const string LocalSourceId = "local";

    /// <summary>The registry catalogue that carries the vendor feeds (tvOS 26, Fire TV, community).</summary>
    public const string AerialManifestsId = "aerial-manifests";

    /// <summary>Every source id, in the order a UI should offer them.</summary>
    public static IReadOnlyList<string> SourceIds { get; } =
        [AppleAerialSource.SourceId, .. SourceRegistry.KnownCatalogs, LocalSourceId];

    /// <summary>
    /// The source id for a <see cref="ClipInfo.CatalogId"/> or a <see cref="ClipInfo.SourceIds"/>
    /// entry: "apple", a registry catalogue id, or "local". A vendor feed id ("apple-tvos26",
    /// "amazon-fireos8"…) belongs to aerial-manifests, the only catalogue with feeds.
    /// </summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (id.StartsWith(RegistryCatalogSource.SourceIdPrefix, StringComparison.OrdinalIgnoreCase))
            id = id[RegistryCatalogSource.SourceIdPrefix.Length..];

        if (id.Equals(AppleAerialSource.SourceId, StringComparison.OrdinalIgnoreCase))
            return AppleAerialSource.SourceId;

        if (id.StartsWith(LocalSourceId, StringComparison.OrdinalIgnoreCase))
            return LocalSourceId;

        foreach (var catalog in SourceRegistry.KnownCatalogs)
        {
            if (id.Equals(catalog, StringComparison.OrdinalIgnoreCase))
                return catalog;
        }

        return AerialManifestsId;
    }

    /// <summary>The clip's primary source: the one whose URLs it plays.</summary>
    public static string SourceOf(ClipInfo clip) =>
        Normalize(clip.CatalogId) ?? Normalize(clip.SourceId) ?? AerialManifestsId;

    /// <summary>Every source that lists the clip, primary first.</summary>
    public static IReadOnlySet<string> SourcesOf(ClipInfo clip)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal) { SourceOf(clip) };
        foreach (var id in clip.SourceIds.Append(clip.SourceId))
        {
            if (Normalize(id) is { } source)
                sources.Add(source);
        }
        return sources;
    }

    /// <summary>The keys of every clip a source lists.</summary>
    public static IEnumerable<string> KeysOf(IEnumerable<ClipInfo> catalogue, string sourceId)
    {
        var source = Normalize(sourceId);
        return catalogue.Where(c => SourcesOf(c).Contains(source!)).Select(c => c.Key);
    }

    // ---- edits (the browser's setRotation / auto-add toggles) ----------------------------

    public static bool IsInRotation(AppSettings settings, string key) => settings.RotationKeys.Contains(key);

    /// <summary>Adds clips to, or removes them from, the rotation. The caller saves (<see cref="AppSettings.Save"/>).</summary>
    /// <returns>How many keys changed.</returns>
    public static int SetInRotation(AppSettings settings, IEnumerable<string> keys, bool inRotation) =>
        keys.Distinct(StringComparer.Ordinal)
            .Count(key => inRotation ? settings.RotationKeys.Add(key) : settings.RotationKeys.Remove(key));

    /// <summary>
    /// Turns auto-add on or off for a source. Only clips the source gains from now on are
    /// affected; to change its existing clips too, pair this with <see cref="SetInRotation"/>
    /// over <see cref="KeysOf"/>. The caller saves.
    /// </summary>
    /// <returns>True if the set changed.</returns>
    public static bool SetAutoAdd(AppSettings settings, string sourceId, bool autoAdd)
    {
        var source = Normalize(sourceId) ?? throw new ArgumentException("Empty source id", nameof(sourceId));
        return autoAdd ? settings.AutoAddSources.Add(source) : settings.AutoAddSources.Remove(source);
    }

    /// <summary>
    /// Records every catalogue key in <see cref="AppSettings.KnownKeys"/>, adding the ones not
    /// seen before to the rotation when one of their sources is in
    /// <see cref="AppSettings.AutoAddSources"/>. A no-op on unmigrated (v1) settings, whose
    /// known set is empty and would make every clip look new.
    /// </summary>
    /// <returns>(clips added to the rotation, keys newly known). Anything non-zero needs saving.</returns>
    public static (int Added, int NewlyKnown) MergeNewKeys(AppSettings settings, IEnumerable<ClipInfo> catalogue)
    {
        if (settings.Version < 2)
            return (0, 0);

        var added = 0;
        var known = 0;
        foreach (var clip in catalogue)
        {
            if (!settings.KnownKeys.Add(clip.Key))
                continue;

            known++;
            if (SourcesOf(clip).Overlaps(settings.AutoAddSources) && settings.RotationKeys.Add(clip.Key))
                added++;
        }

        if (known > 0)
            Log.Info($"Rotation: {known} new clip key(s), {added} auto-added to the rotation");
        return (added, known);
    }

    // ---- what the saver plays ------------------------------------------------------------

    /// <summary>
    /// The clips the saver plays, in catalogue order (the playlist shuffles them):
    /// <list type="number">
    ///   <item>the rotation — catalogue clips whose key is in <see cref="AppSettings.RotationKeys"/>;</item>
    ///   <item>if that is empty, Apple's aerials;</item>
    ///   <item>if there are none (no manifest), every remote clip already cached;</item>
    ///   <item>if nothing is cached, the whole catalogue.</item>
    /// </list>
    /// The cache never decides what plays while there is anything else: it is a convenience,
    /// and a cached-first fallback replayed the same few clips forever.
    /// Clips with no playable URL are dropped at each step.
    /// </summary>
    /// <param name="isCached">Whether a playable clip is on disk; null when there is no cache.</param>
    public static RotationSelection Select(AppSettings settings, IReadOnlyList<ClipInfo> catalogue, Func<Clip, bool>? isCached = null)
    {
        var quality = settings.Quality;

        List<(ClipInfo Info, Clip Clip)> Playable(IEnumerable<ClipInfo> infos) =>
            infos.Select(i => (Info: i, Clip: ClipResolver.PlayableClip(i, quality)))
                 .Where(p => p.Clip is not null)
                 .Select(p => (p.Info, p.Clip!))
                 .ToList();

        RotationSelection Result(List<(ClipInfo Info, Clip Clip)> picked, RotationFallback fallback) =>
            new(picked.Select(p => p.Info).ToList(), picked.Select(p => p.Clip).ToList(), fallback);

        var rotation = Playable(catalogue.Where(c => settings.RotationKeys.Contains(c.Key)));
        if (rotation.Count > 0)
            return Result(rotation, RotationFallback.None);

        var apple = Playable(catalogue.Where(c => SourceOf(c) == AppleAerialSource.SourceId));
        if (apple.Count > 0)
            return Result(apple, RotationFallback.Apple);

        if (isCached is not null)
        {
            var cached = Playable(catalogue.Where(c => !c.IsLocal)).Where(p => isCached(p.Clip)).ToList();
            if (cached.Count > 0)
                return Result(cached, RotationFallback.Cached);
        }

        return Result(Playable(catalogue), RotationFallback.Everything);
    }
}
