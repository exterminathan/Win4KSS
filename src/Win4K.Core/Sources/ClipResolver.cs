namespace Win4K.Core;

/// <summary>
/// Assembles the clip list from whichever sources the user has enabled.
/// </summary>
public static class ClipResolver
{
    public static Task<IReadOnlyList<Clip>> ResolveAsync(AppSettings? settings = null, CancellationToken ct = default) =>
        ResolveAsync(BuildSources(settings ?? AppSettings.Load()), ct);

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

    public static IEnumerable<IVideoSource> BuildSources(AppSettings settings)
    {
        if (settings.SourceMode is SourceMode.Apple or SourceMode.Both)
        {
            yield return new AppleAerialSource(
                new Apple.AppleManifestClient(tarballUrlOverride: settings.ManifestUrlOverride),
                () => settings.Quality);
        }

        if (settings.SourceMode is SourceMode.LocalFolders or SourceMode.Both)
        {
            foreach (var folder in settings.LocalFolders)
                yield return new LocalFolderSource(folder);
        }

        // Harvested catalogues are additive and independent of SourceMode: they are extra
        // breadth, and their whole purpose is to keep something playable when Apple is not.
        var available = SourceRegistry.Available().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in settings.EnabledCatalogIds)
        {
            if (available.TryGetValue(id, out var info))
                yield return new RegistryCatalogSource(info.Id, info.DisplayName);
        }
    }
}
