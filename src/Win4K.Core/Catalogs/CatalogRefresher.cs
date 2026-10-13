using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>What happened to one source in a refresh.</summary>
/// <param name="HarvestedCount">Unique assets the harvester found — the number scripts/harvest.py reported.</param>
/// <param name="MirrorsDropped">archive.org re-uploads removed by <see cref="ClipMerger.DropArchiveMirrors(IReadOnlyList{AppleAsset}, string)"/>.</param>
/// <param name="CarriedOver">Rows kept from the previous catalogue because their item could not be fetched.</param>
/// <param name="AssetCount">Rows in the catalogue written (or, on failure, the one kept).</param>
public sealed record CatalogRefreshOutcome(
    string SourceId,
    bool Succeeded,
    int HarvestedCount,
    int MirrorsDropped,
    int CarriedOver,
    int AssetCount,
    int Added,
    int Removed,
    string? Error,
    IReadOnlyList<string> Notes,
    string? Path)
{
    public override string ToString() => Succeeded
        ? $"{SourceId}: {AssetCount} clips (harvested {HarvestedCount}, -{MirrorsDropped} mirrors, +{CarriedOver} carried over; {Added} new, {Removed} gone)"
        : $"{SourceId}: FAILED — {Error}";
}

public sealed record CatalogRefreshReport(IReadOnlyList<CatalogRefreshOutcome> Sources)
{
    public bool AllSucceeded => Sources.All(s => s.Succeeded);
}

/// <summary>
/// Re-harvests the auto sources in sources.json and writes each to the local catalogue
/// store, replacing scripts/harvest.py. Runs from the clip browser (on demand, and at most
/// every <see cref="AutoRefreshInterval"/> in the background) — never from the saver, which
/// only ever reads catalogues.
///
/// A refresh never makes things worse: a harvester that throws, or returns nothing, leaves
/// the previous catalogue in place; items that fail individually keep their previous rows.
/// </summary>
public sealed class CatalogRefresher
{
    /// <summary>
    /// The date scripts/harvest.py first produced the catalogues. Every clip that was already
    /// in a v1 catalogue gets this as its <see cref="AppleAsset.AddedUtc"/>, so the first C#
    /// refresh does not tag 1,400 clips as New.
    /// </summary>
    public static readonly DateTime BaselineAddedUtc = new(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromDays(7);

    private readonly CatalogStore _store;
    private readonly IReadOnlyList<SourceDefinition> _sources;
    private readonly Dictionary<string, ICatalogHarvester> _harvesters;
    private readonly TimeProvider _time;

    /// <param name="store">Defaults to %LOCALAPPDATA%\Win4KScreensavers\catalogs.</param>
    /// <param name="sources">Defaults to the embedded sources.json.</param>
    /// <param name="harvesters">Defaults to the three live harvesters over <see cref="Http.Client"/>.</param>
    public CatalogRefresher(
        CatalogStore? store = null,
        IReadOnlyList<SourceDefinition>? sources = null,
        IEnumerable<ICatalogHarvester>? harvesters = null,
        TimeProvider? time = null)
    {
        _store = store ?? new CatalogStore();
        _sources = sources ?? SourceDefinitions.LoadEmbedded();
        _harvesters = (harvesters ?? DefaultHarvesters()).ToDictionary(h => h.SourceId, StringComparer.OrdinalIgnoreCase);
        _time = time ?? TimeProvider.System;
    }

    public static IEnumerable<ICatalogHarvester> DefaultHarvesters(HttpClient? http = null)
    {
        var client = new HarvestHttp(http);
        return
        [
            new ArchiveOrgHarvester(client),
            new NasaHarvester(client, FfprobeMediaProbe.TryCreate()),
            new AppleFeedHarvester(client),
        ];
    }

    public CatalogStore Store => _store;

    /// <summary>Sources marked harvest:auto that have a harvester.</summary>
    public IReadOnlyList<string> AutoSourceIds =>
        _sources.Where(s => s.IsAuto && _harvesters.ContainsKey(s.Id)).Select(s => s.Id).ToList();

    /// <summary>True when any auto source has no local catalogue, or one older than <see cref="AutoRefreshInterval"/>.</summary>
    public bool IsDue()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return AutoSourceIds.Any(id => _store.LastRefreshedUtc(id) is not { } last || now - last >= AutoRefreshInterval);
    }

    /// <summary>
    /// Refreshes the given sources (default: every auto source) concurrently.
    /// Cancellation stops in-flight harvests and writes nothing for them.
    /// </summary>
    public async Task<CatalogRefreshReport> RefreshAsync(
        IEnumerable<string>? sourceIds = null,
        IProgress<CatalogRefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        var wanted = (sourceIds ?? AutoSourceIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = _sources.Where(s => wanted.Contains(s.Id)).ToList();

        var outcomes = await Task.WhenAll(definitions.Select(def => RefreshOneAsync(def, progress, ct))).ConfigureAwait(false);

        foreach (var missing in wanted.Where(id => definitions.All(d => !d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            Log.Warn($"Catalogues: '{missing}' is not in sources.json");

        SourceRegistry.Invalidate();
        foreach (var outcome in outcomes)
            Log.Info($"Catalogues: {outcome}");

        return new CatalogRefreshReport(outcomes);
    }

    private async Task<CatalogRefreshOutcome> RefreshOneAsync(
        SourceDefinition source, IProgress<CatalogRefreshProgress>? progress, CancellationToken ct)
    {
        var (previous, _) = _store.Load(source.Id);
        var previousCount = previous?.Assets.Count ?? 0;

        CatalogRefreshOutcome Failed(string error, IReadOnlyList<string>? notes = null) =>
            new(source.Id, false, 0, 0, 0, previousCount, 0, 0, error, notes ?? [], null);

        if (!_harvesters.TryGetValue(source.Id, out var harvester))
            return Failed("no harvester for this source");

        HarvestResult result;
        try
        {
            Log.Info($"Catalogues: harvesting {source.Id}");
            result = await harvester.HarvestAsync(source, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Catalogues: {source.Id} harvest failed; keeping the previous catalogue", ex);
            return Failed(ex.Message);
        }

        // A harvest that raced a cancel to completion still writes nothing.
        ct.ThrowIfCancellationRequested();

        var built = Build(source.Id, previous, result, _time.GetUtcNow().UtcDateTime);
        if (built.Manifest.Assets.Count == 0)
            return Failed("the harvest found no clips; keeping the previous catalogue", result.Notes);

        _store.Save(source.Id, built.Manifest);
        return new CatalogRefreshOutcome(
            source.Id, true, built.Harvested, built.MirrorsDropped, built.CarriedOver,
            built.Manifest.Assets.Count, built.Added, built.Removed, null, result.Notes, _store.PathFor(source.Id));
    }

    public sealed record BuildResult(AppleManifest Manifest, int Harvested, int MirrorsDropped, int CarriedOver, int Added, int Removed);

    /// <summary>
    /// Turns a harvest into the catalogue to write: dedupe, drop mirrors, carry over failed
    /// items, and stamp <see cref="AppleAsset.AddedUtc"/> by diffing against
    /// <paramref name="previous"/>. Pure; the refresher's only logic that is not I/O.
    /// </summary>
    public static BuildResult Build(string sourceId, AppleManifest? previous, HarvestResult result, DateTime nowUtc)
    {
        // harvest.py's identity rule (one asset per URL), then one per key.
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<AppleAsset>();
        foreach (var asset in result.Assets)
        {
            var url = asset.Url4KSdr ?? asset.Url4KHdr ?? asset.UrlFor(VideoQuality.Uhd4KSdr) ?? "";
            asset.Key ??= ClipKey.ForAsset(sourceId, asset);
            if (seenUrls.Add(url) && seenKeys.Add(asset.Key))
                unique.Add(asset);
        }
        var harvested = unique.Count;

        var assets = ClipMerger.DropArchiveMirrors(unique, sourceId);
        var mirrors = harvested - assets.Count;

        var previousByKey = new Dictionary<string, AppleAsset>(StringComparer.Ordinal);
        foreach (var old in previous?.Assets ?? [])
            previousByKey.TryAdd(ClipKey.ForAsset(sourceId, old), old);

        // Keep what we could not re-fetch, as it was.
        var carried = 0;
        if (result.FailedUnits.Count > 0)
        {
            var have = assets.Select(a => a.Key!).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, old) in previousByKey)
            {
                if (HarvestResult.UnitOf(old) is { } unit && result.FailedUnits.Contains(unit) && have.Add(key))
                {
                    old.Key = key;
                    assets.Add(old);
                    carried++;
                }
            }
        }

        var added = 0;
        foreach (var asset in assets)
        {
            if (previousByKey.TryGetValue(asset.Key!, out var old))
            {
                asset.AddedUtc = old.AddedUtc ?? BaselineAddedUtc;
            }
            else
            {
                asset.AddedUtc = nowUtc;
                added++;
            }
        }

        var keys = assets.Select(a => a.Key!).ToHashSet(StringComparer.Ordinal);
        var removed = previousByKey.Keys.Count(k => !keys.Contains(k));

        var categories = result.Categories.Count > 0
            ? result.Categories.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList()
            : previous?.Categories.Where(_ => carried > 0).ToList() ?? [];

        var manifest = new AppleManifest
        {
            Assets = assets,
            Categories = categories,
            Version = CatalogStore.CurrentSchemaVersion,
            SchemaVersion = CatalogStore.CurrentSchemaVersion,
            SourceId = sourceId,
            GeneratedUtc = nowUtc,
            InitialAssetCount = assets.Count,
        };

        return new BuildResult(manifest, harvested, mirrors, carried, added, removed);
    }
}
