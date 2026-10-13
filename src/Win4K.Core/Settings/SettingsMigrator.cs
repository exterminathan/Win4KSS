using Win4K.Core.Catalogs;

namespace Win4K.Core;

/// <summary>
/// Settings v1 → v2, and the per-run rotation upkeep that follows it.
///
/// <para>The migration needs the catalogue (v1 named sources; v2 names clips), and
/// <see cref="AppSettings.Load"/> is synchronous and catalogue-free, so it runs here, after the
/// caller has loaded <see cref="ClipLibrary"/>. Both the saver and the browser call
/// <see cref="Prepare"/> (or <see cref="PrepareAndSave"/>) once they have the catalogue.</para>
///
/// <para>Idempotent: <see cref="MigrateToV2"/> does nothing to v2 settings, and
/// <see cref="Rotation.MergeNewKeys"/> nothing once every key is known. The v1 file is kept as
/// settings.v1.json by the first v2 save (<see cref="AppSettings.Save"/>), whoever makes it.</para>
/// </summary>
public static class SettingsMigrator
{
    /// <summary>
    /// Rewrites v1 settings as v2, reproducing exactly what v1 played:
    /// <list type="bullet">
    ///   <item><b>Rotation</b> = every catalogue clip listed by a source v1 had enabled — Apple
    ///   when <see cref="SourceMode"/> was Apple or Both, local folders when it was LocalFolders
    ///   or Both, plus <see cref="AppSettings.EnabledCatalogIds"/> — narrowed to
    ///   <see cref="AppSettings.SelectedClipIds"/> when that matched anything (v1 matched it
    ///   case-insensitively and ignored a selection that matched nothing).</item>
    ///   <item><b>Auto-add</b> = those sources. Local folders auto-add by default, so "local" is
    ///   included unless v1 had folders configured but switched off.</item>
    ///   <item><b>Known</b> = every catalogue key, so nothing counts as new after migrating.</item>
    /// </list>
    /// The retired fields are nulled, so they are not written again.
    /// </summary>
    /// <returns>True if the settings were migrated (and need saving).</returns>
    public static bool MigrateToV2(AppSettings settings, IReadOnlyList<ClipInfo> catalogue)
    {
        if (settings.Version >= 2)
            return false;

        // Without a catalogue the rotation would come out empty and every clip would later
        // look new; stay v1 (Rotation.Select falls back sensibly) and migrate next time.
        if (catalogue.Count == 0)
        {
            Log.Warn("Settings: v1 -> v2 migration postponed, the catalogue is empty");
            return false;
        }

        var mode = settings.SourceMode ?? SourceMode.Apple;
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        if (mode is SourceMode.Apple or SourceMode.Both)
            enabled.Add(AppleAerialSource.SourceId);

        var localEnabled = mode is SourceMode.LocalFolders or SourceMode.Both;
        if (localEnabled)
            enabled.Add(Rotation.LocalSourceId);

        foreach (var id in settings.EnabledCatalogIds ?? [])
        {
            var catalog = SourceRegistry.KnownCatalogs.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (catalog is not null)
                enabled.Add(catalog);
        }

        var pool = catalogue.Where(c => Rotation.SourcesOf(c).Overlaps(enabled)).ToList();

        if (settings.SelectedClipIds is { Count: > 0 } selectedIds)
        {
            var selected = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subset = pool.Where(c => selected.Contains(c.Key)).ToList();
            if (subset.Count > 0)
                pool = subset;
        }

        var autoAdd = new HashSet<string>(enabled, StringComparer.Ordinal);
        if (localEnabled || settings.LocalFolders.Count == 0)
            autoAdd.Add(Rotation.LocalSourceId);

        settings.RotationKeys.UnionWith(pool.Select(c => c.Key));
        settings.AutoAddSources = autoAdd;
        settings.KnownKeys.UnionWith(catalogue.Select(c => c.Key));

        settings.SourceMode = null;
        settings.SelectedClipIds = null;
        settings.EnabledCatalogIds = null;
        settings.Version = 2;

        Log.Info($"Settings: migrated v1 -> v2: {settings.RotationKeys.Count} clips in rotation from " +
                 $"[{string.Join(", ", enabled.Order(StringComparer.Ordinal))}], auto-add " +
                 $"[{string.Join(", ", autoAdd.Order(StringComparer.Ordinal))}]");
        return true;
    }

    /// <summary>
    /// Everything a process does to settings once it has the catalogue: migrate v1, then merge
    /// clips new since the last run into the rotation (<see cref="Rotation.MergeNewKeys"/>).
    /// </summary>
    /// <returns>True if anything changed and should be saved.</returns>
    public static bool Prepare(AppSettings settings, IReadOnlyList<ClipInfo> catalogue)
    {
        var migrated = MigrateToV2(settings, catalogue);
        var (_, newlyKnown) = Rotation.MergeNewKeys(settings, catalogue);
        return migrated || newlyKnown > 0;
    }

    /// <summary>
    /// <see cref="Prepare"/>, persisted. When something changed, the file is re-read and the
    /// same (idempotent) changes applied to that copy before saving, so an edit another process
    /// made in the meantime — the browser — is not overwritten by this one's stale snapshot.
    /// </summary>
    /// <returns>The settings to use from here on: <paramref name="settings"/>, or the saved copy.</returns>
    public static AppSettings PrepareAndSave(AppSettings settings, IReadOnlyList<ClipInfo> catalogue, string? path = null)
    {
        if (!Prepare(settings, catalogue))
            return settings;

        var fresh = AppSettings.Load(path);
        Prepare(fresh, catalogue);
        try
        {
            fresh.Save(path);
        }
        catch (Exception ex)
        {
            // Unsaved, the same changes are simply made again next run.
            Log.Error("Settings: could not save the migrated rotation", ex);
        }
        return fresh;
    }
}
