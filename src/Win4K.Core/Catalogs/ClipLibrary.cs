namespace Win4K.Core.Catalogs;

/// <summary>
/// The full clip catalogue: every source merged into one <see cref="ClipInfo"/> per key
/// (<see cref="ClipMerger"/>). The browser shows all of it and lets the user choose per clip;
/// the saver narrows it to <see cref="AppSettings.RotationKeys"/> (<see cref="Rotation.Select"/>).
/// After loading, call <see cref="SettingsMigrator.Prepare"/> so v1 settings are migrated and
/// new clips from auto-add sources join the rotation.
///
/// Reads only local data plus Apple's manifest (disk cache → network → embedded), so it is
/// cheap enough to call on every browser open. Never refreshes catalogues; that is
/// <see cref="CatalogRefresher"/>'s job.
/// </summary>
public static class ClipLibrary
{
    /// <summary>Every built-in source: Apple, each registry catalogue, each local folder.</summary>
    public static IEnumerable<IClipInfoSource> AllSources(AppSettings settings)
    {
        yield return new AppleAerialSource(
            new Apple.AppleManifestClient(tarballUrlOverride: settings.ManifestUrlOverride),
            () => settings.Quality);

        foreach (var id in SourceRegistry.KnownCatalogs)
            yield return new RegistryCatalogSource(id);

        foreach (var folder in settings.LocalFolders)
            yield return new LocalFolderSource(folder);
    }

    public static async Task<IReadOnlyList<ClipInfo>> LoadAsync(AppSettings? settings = null, CancellationToken ct = default) =>
        await LoadAsync(AllSources(settings ?? AppSettings.Load()), ct).ConfigureAwait(false);

    /// <summary>Loads each source (a failing one is logged and skipped) and merges the result.</summary>
    public static async Task<IReadOnlyList<ClipInfo>> LoadAsync(IEnumerable<IClipInfoSource> sources, CancellationToken ct = default)
    {
        var all = new List<ClipInfo>();
        foreach (var source in sources)
        {
            try
            {
                all.AddRange(await source.GetClipInfosAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Library: {source.DisplayName} failed", ex);
            }
        }

        var merged = ClipMerger.Merge(all);
        Log.Info($"Library: {merged.Count} clips ({all.Count} listings before merge)");
        return merged;
    }
}
