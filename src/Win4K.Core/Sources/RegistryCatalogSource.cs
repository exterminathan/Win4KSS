using System.Text.Json;
using Win4K.Core.Apple;
using Win4K.Core.Catalogs;

namespace Win4K.Core;

/// <summary>
/// One harvested catalogue from assets/registry — Archive.org, NASA, or the mirrored aerial
/// manifests. These exist so the screen saver is not one Apple decision away from having
/// nothing to play, which is exactly how cdima/aerial ended up unusable.
///
/// The catalogues are written in Apple's own manifest schema, so they need no separate
/// deserialiser; they simply carry extension fields (scene, width, height, sizeBytes, and
/// in schema v2 key, captions, subjects, licence, thumbnailUrl, addedUtc…) that
/// <see cref="AppleAsset"/> understands. Loaded local-refresh-first, embedded second.
/// </summary>
public sealed class RegistryCatalogSource(string catalogId, string? displayName = null) : IClipInfoSource
{
    public const string SourceIdPrefix = "registry:";

    public string CatalogId { get; } = catalogId;

    public string Id => SourceIdPrefix + CatalogId;

    public string DisplayName { get; } = displayName ?? catalogId;

    public Task<IReadOnlyList<ClipInfo>> GetClipInfosAsync(CancellationToken ct = default)
    {
        var manifest = SourceRegistry.LoadCatalog(CatalogId);
        if (manifest is null)
            return Task.FromResult<IReadOnlyList<ClipInfo>>([]);

        // v2 catalogues are already mirror-free; a v1 one (harvest.py) still lists archive.org's
        // "_201507" re-uploads, so drop them on load too.
        IReadOnlyList<ClipInfo> clips = ClipMerger.DropArchiveMirrors(CatalogClips.ToClipInfos(manifest, CatalogId)).ToList();
        return Task.FromResult(clips);
    }

    public async Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct = default)
    {
        // Most of these catalogues publish a single 4K SDR rendition, so quality selection is
        // a no-op — UrlFor falls through the chain and lands on url-4K-SDR.
        var infos = await GetClipInfosAsync(ct).ConfigureAwait(false);
        return infos.Select(i => i.ToClip(VideoQuality.Uhd4KSdr)).OfType<Clip>().ToList();
    }
}

public sealed record RegistrySourceInfo(string Id, string DisplayName, string Harvest, int ClipCount);

/// <summary>
/// Reads the harvested catalogues and the registry that describes them. Both are embedded,
/// so the installed single-file .scr carries them with no folder to deploy alongside; a
/// catalogue refreshed in-app (<see cref="CatalogRefresher"/>) overrides the embedded copy.
/// </summary>
public static class SourceRegistry
{
    private static readonly Dictionary<string, AppleManifest?> CatalogCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static List<RegistrySourceInfo>? _sources;

    /// <summary>Catalogue ids that ship with the app, in the order they should be offered.</summary>
    public static readonly string[] KnownCatalogs = ["aerial-manifests", "archive-org", "nasa"];

    public static AppleManifest? LoadCatalog(string catalogId)
    {
        lock (Gate)
        {
            if (CatalogCache.TryGetValue(catalogId, out var cached))
                return cached;

            AppleManifest? manifest = null;
            try
            {
                (manifest, var origin) = new CatalogStore().Load(catalogId);

                if (manifest is null)
                    Log.Warn($"Registry: catalogue '{catalogId}' missing or unparseable");
                else
                    Log.Info($"Registry: catalogue '{catalogId}' has {manifest.Assets.Count} assets ({origin.ToString().ToLowerInvariant()})");
            }
            catch (Exception ex)
            {
                Log.Error($"Registry: failed to load catalogue '{catalogId}'", ex);
            }

            CatalogCache[catalogId] = manifest;
            return manifest;
        }
    }

    /// <summary>Forgets loaded catalogues, so the next read sees a refresh that just landed.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            CatalogCache.Clear();
            _sources = null;
        }
    }

    /// <summary>
    /// Describes each shipped catalogue, pulling display names out of sources.json where it
    /// has them and falling back to the id.
    /// </summary>
    public static IReadOnlyList<RegistrySourceInfo> Available()
    {
        lock (Gate)
        {
            if (_sources is not null)
                return _sources;

            var names = new Dictionary<string, (string Display, string Harvest)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var json = CatalogStore.ReadEmbedded("sources.json");
                if (json is not null)
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("sources", out var array))
                    {
                        foreach (var entry in array.EnumerateArray())
                        {
                            var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            var display = entry.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                            var harvest = entry.TryGetProperty("harvest", out var h) ? h.GetString() : "";

                            names[id] = (display ?? id, harvest ?? "");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Registry: sources.json unreadable", ex);
            }

            var result = new List<RegistrySourceInfo>();
            foreach (var id in KnownCatalogs)
            {
                var manifest = LoadCatalog(id);
                if (manifest is null || manifest.Assets.Count == 0)
                    continue; // only offer catalogues that actually shipped with content

                var (display, harvest) = names.TryGetValue(id, out var n) ? n : (id, "");
                result.Add(new RegistrySourceInfo(id, display, harvest, manifest.Assets.Count));
            }

            _sources = result;
            return result;
        }
    }
}
