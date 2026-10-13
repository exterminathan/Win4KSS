using Win4K.Core.Catalogs;

namespace Win4K.Core;

/// <summary>
/// Turns the catalogue into playable clips. Every source is always resolved: since settings v2
/// the choice of what plays is per clip (<see cref="Rotation"/>), not per source, so there is
/// no source mode to honour here. The saver narrows the result with <see cref="Rotation.Select"/>.
/// </summary>
public static class ClipResolver
{
    /// <summary>Every clip in the catalogue, playable at the settings' quality. Not filtered to the rotation.</summary>
    public static async Task<IReadOnlyList<Clip>> ResolveAsync(AppSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= AppSettings.Load();
        var catalogue = await ClipLibrary.LoadAsync(settings, ct).ConfigureAwait(false);
        return ToClips(catalogue, settings.Quality);
    }

    /// <summary>
    /// The rendition a clip plays at — the one rule shared by the saver, the cache and pins
    /// (<see cref="Caching.PinManager.ClipFor"/>), so a pinned or cached file is the one the
    /// saver looks for:
    /// <list type="bullet">
    ///   <item>Apple's clips: the user's quality (<see cref="AppleAerialSource.Resolve"/>, so
    ///   Auto follows the primary display);</item>
    ///   <item>harvested catalogues (including tvOS-only clips from aerial-manifests): 4K SDR,
    ///   which is what they publish, falling down the ladder if not;</item>
    ///   <item>local files: the file itself.</item>
    /// </list>
    /// Null when the clip has no usable URL.
    /// </summary>
    public static Clip? PlayableClip(ClipInfo info, VideoQuality quality)
    {
        if (info.IsLocal)
            return info.ToClip(VideoQuality.Auto);

        var apple = string.Equals(info.CatalogId, AppleAerialSource.SourceId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.SourceId, AppleAerialSource.SourceId, StringComparison.OrdinalIgnoreCase);
        return info.ToClip(apple ? AppleAerialSource.Resolve(quality) : VideoQuality.Uhd4KSdr);
    }

    /// <summary>The playable clips among <paramref name="infos"/>, in order.</summary>
    public static IReadOnlyList<Clip> ToClips(IEnumerable<ClipInfo> infos, VideoQuality quality) =>
        infos.Select(i => PlayableClip(i, quality)).OfType<Clip>().ToList();

    /// <summary>Clips from <paramref name="sources"/> in order, one per key; a failing source is skipped.</summary>
    public static async Task<IReadOnlyList<Clip>> ResolveAsync(IEnumerable<IVideoSource> sources, CancellationToken ct = default)
    {
        var all = new List<Clip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            try
            {
                var clips = await source.GetClipsAsync(ct).ConfigureAwait(false);

                // One clip per key. Apple's tarball and the tvOS 26 feed (aerial-manifests)
                // list the same 114 aerials under the same GUID; the source yielded first —
                // Apple, whose URLs the cache already holds — wins.
                var added = 0;
                foreach (var clip in clips)
                {
                    if (seen.Add(clip.Id))
                    {
                        all.Add(clip);
                        added++;
                    }
                }

                var duplicates = clips.Count - added;
                Log.Info($"Sources: {source.DisplayName} contributed {added} clips" +
                         (duplicates > 0 ? $" ({duplicates} already listed by an earlier source)" : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One dead source must not take the whole playlist down with it — if the
                // network is out, local folders should still play.
                Log.Error($"Sources: {source.DisplayName} failed", ex);
            }
        }

        return all;
    }

    /// <summary>Every source: Apple, each registry catalogue, each local folder. Same as <see cref="ClipLibrary.AllSources"/>.</summary>
    public static IEnumerable<IVideoSource> BuildSources(AppSettings settings) => ClipLibrary.AllSources(settings);
}
