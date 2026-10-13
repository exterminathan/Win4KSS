using System.Formats.Tar;
using System.Reflection;

namespace Win4K.Core.Apple;

/// <summary>
/// Resolves Apple's aerial manifest, with three fallbacks so the screen saver still has
/// something to play when the network, or Apple, is unavailable.
///
/// Resolution order: fresh disk cache -> network -> stale disk cache -> embedded copy.
///
/// The manifest is NOT at a stable URL. It lives inside a per-tvOS-version tarball at
/// https://sylvan.apple.com/Aerials/resources-{N}.tar. As of 2026-08 the newest published
/// is N=16; 17 and 18 return 404. We probe a range so a future release is picked up without
/// a code change.
///
/// Deliberately ignored: https://sylvan.apple.com/Aerials/2x/entries.json. It still returns
/// 200 and looks like exactly what we want, but it is stale — every video URL inside it
/// 404s. Only the tarball copy is correct.
/// </summary>
public sealed class AppleManifestClient
{
    public const string TarballUrlFormat = "https://sylvan.apple.com/Aerials/resources-{0}.tar";
    public const string LegacyTarballUrl = "https://sylvan.apple.com/Aerials/resources.tar";
    private const string EntriesFileName = "entries.json";

    /// <summary>Highest tvOS tarball to probe for. Generous headroom over the current 16.</summary>
    private const int ProbeMax = 22;

    /// <summary>Lowest to probe. 13 is the oldest that carries the 4K URL fields.</summary>
    private const int ProbeMin = 13;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string? _urlOverride;

    public AppleManifestClient(HttpClient? http = null, string? tarballUrlOverride = null)
    {
        _http = http ?? Http.Client;
        _urlOverride = tarballUrlOverride;
    }

    /// <summary>Where the last successful manifest came from. Surfaced in the settings About tab.</summary>
    public string LastResolvedFrom { get; private set; } = "(not resolved)";

    public async Task<AppleManifest> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && TryReadCache(requireFresh: true, out var fresh))
        {
            LastResolvedFrom = "disk cache";
            Log.Info($"Manifest: {fresh!.Assets.Count} assets from fresh disk cache");
            return fresh;
        }

        try
        {
            var fetched = await FetchFromNetworkAsync(ct).ConfigureAwait(false);
            if (fetched is { Assets.Count: > 0 })
            {
                WriteCache(fetched);
                LastResolvedFrom = "sylvan.apple.com";
                Log.Info($"Manifest: {fetched.Assets.Count} assets from network");
                return fetched;
            }
            Log.Warn("Manifest: network returned an empty manifest");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Manifest: network fetch failed", ex);
        }

        // Stale beats nothing — the videos themselves rarely change.
        if (TryReadCache(requireFresh: false, out var stale))
        {
            LastResolvedFrom = "disk cache (stale)";
            Log.Warn($"Manifest: falling back to stale disk cache ({stale!.Assets.Count} assets)");
            return stale;
        }

        var embedded = ReadEmbeddedFallback();
        LastResolvedFrom = "embedded fallback";
        Log.Warn($"Manifest: falling back to embedded copy ({embedded.Assets.Count} assets)");
        return embedded;
    }

    // ---- network ---------------------------------------------------------------------

    private async Task<AppleManifest?> FetchFromNetworkAsync(CancellationToken ct)
    {
        var url = _urlOverride ?? await ProbeTarballUrlAsync(ct).ConfigureAwait(false);
        if (url is null)
        {
            Log.Warn("Manifest: no reachable tarball found");
            return null;
        }

        Log.Info($"Manifest: downloading {url}");
        await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);

        // TarReader needs to walk sequentially; the tarball is ~2 MB so buffering is cheap
        // and avoids holding the socket open while we parse.
        using var buffer = new MemoryStream(capacity: 2 * 1024 * 1024);
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        return await ExtractManifestAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the newest published tarball. Probes every candidate concurrently with HEAD
    /// (sylvan supports it) and takes the highest that answers 200 — one round trip's
    /// latency instead of walking down from 22 to 16 one request at a time.
    /// </summary>
    public async Task<string?> ProbeTarballUrlAsync(CancellationToken ct = default)
    {
        var candidates = new List<(int Version, string Url)>();
        for (var n = ProbeMax; n >= ProbeMin; n--)
            candidates.Add((n, string.Format(TarballUrlFormat, n)));
        candidates.Add((0, LegacyTarballUrl));

        var probes = candidates.Select(async c =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, c.Url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                                .ConfigureAwait(false);
                return (c.Version, c.Url, Ok: response.IsSuccessStatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (c.Version, c.Url, Ok: false);
            }
        });

        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        var best = results.Where(r => r.Ok)
                          .OrderByDescending(r => r.Version)
                          .Select(r => r.Url)
                          .FirstOrDefault();

        if (best is not null)
            Log.Info($"Manifest: newest tarball is {best}");

        return best;
    }

    public static async Task<AppleManifest?> ExtractManifestAsync(Stream tarStream, CancellationToken ct = default)
    {
        await using var reader = new TarReader(tarStream, leaveOpen: true);

        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            // Entry names are POSIX-style and unprefixed in Apple's tarballs ("entries.json"),
            // but tolerate a leading "./" in case that ever changes.
            var name = entry.Name.TrimStart('.', '/');
            if (!name.Equals(EntriesFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.DataStream is null)
                continue;

            using var text = new StreamReader(entry.DataStream);
            var json = await text.ReadToEndAsync(ct).ConfigureAwait(false);
            return AppleJson.Deserialize(json);
        }

        return null;
    }

    // ---- disk cache ------------------------------------------------------------------

    private static bool TryReadCache(bool requireFresh, out AppleManifest? manifest)
    {
        manifest = null;
        try
        {
            var path = AppPaths.ManifestFile;
            if (!File.Exists(path))
                return false;

            if (requireFresh && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > CacheTtl)
                return false;

            manifest = AppleJson.Deserialize(File.ReadAllText(path));
            return manifest is { Assets.Count: > 0 };
        }
        catch (Exception ex)
        {
            Log.Error("Manifest: cache read failed", ex);
            return false;
        }
    }

    private static void WriteCache(AppleManifest manifest)
    {
        try
        {
            AppPaths.EnsureRoot();
            var path = AppPaths.ManifestFile;
            var temp = path + ".tmp";

            // Write-then-move so a crash mid-write cannot leave a truncated manifest that
            // would then be trusted on the next run.
            File.WriteAllText(temp, AppleJson.Serialize(manifest));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Manifest: cache write failed", ex);
        }
    }

    // ---- embedded fallback -----------------------------------------------------------

    public static AppleManifest ReadEmbeddedFallback()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith("fallback-entries.json", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       "Embedded fallback manifest is missing from Win4K.Core.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return AppleJson.Deserialize(reader.ReadToEnd())
               ?? throw new InvalidOperationException("Embedded fallback manifest failed to parse.");
    }
}
