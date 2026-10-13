using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// NASA Image and Video Library. The library is overwhelmingly 1080p and its file names
/// carry no resolution marker (almost everything is "&lt;id&gt;~orig.mp4"), so each candidate
/// has to be measured. scripts/harvest.py ffprobed every one over HTTP (and silently lost
/// every id containing a space, which urllib refuses to fetch: 9 found where 19 exist); this reads the
/// asset's metadata.json instead (NASA publishes the original's ExifTool dump: width,
/// height, duration, codec), which is one small GET rather than a demuxer per candidate.
/// ffprobe is still used, when it is installed, for an asset whose metadata.json lacks
/// dimensions.
/// </summary>
public sealed partial class NasaHarvester(HarvestHttp? http = null, IMediaProbe? probe = null, int parallelism = 8) : ICatalogHarvester
{
    public const string Id = "nasa";
    public const int MinWidth = 3840;
    private const int PageSize = 40;

    private readonly HarvestHttp _http = http ?? new HarvestHttp();

    public string SourceId => Id;

    public static string SearchUrl(string term) =>
        $"https://images-api.nasa.gov/search?q={Uri.EscapeDataString(term)}&media_type=video&page_size={PageSize}";

    /// <summary>One search hit, before it has been measured.</summary>
    public sealed record Candidate(
        string Href, string NasaId, string Title, string? Description, string? Creator,
        IReadOnlyList<string> Keywords, string? ThumbnailUrl);

    /// <summary>Measured facts about the chosen file.</summary>
    public sealed record Measurement(int Width, int Height, double? DurationSeconds, string? Codec);

    public async Task<HarvestResult> HarvestAsync(SourceDefinition source, IProgress<CatalogRefreshProgress>? progress, CancellationToken ct)
    {
        var notes = new List<string>();

        // Keyed by collection href, like harvest.py: one entry per asset across all queries.
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var failedQueries = 0;
        foreach (var term in source.Queries)
        {
            try
            {
                using var doc = await _http.GetJsonAsync(SearchUrl(term), ct).ConfigureAwait(false);
                foreach (var candidate in ParseSearch(doc.RootElement))
                    candidates[candidate.Href] = candidate;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedQueries++;
                Log.Warn($"Harvest {Id}: query '{term}' failed: {ex.Message}");
            }
        }

        if (failedQueries == source.Queries.Count && source.Queries.Count > 0)
            throw new HttpRequestException($"every NASA search failed ({failedQueries} queries)");
        if (failedQueries > 0)
            notes.Add($"{failedQueries} search query(ies) failed");

        var list = candidates.Values.ToList();
        var found = new AppleAsset?[list.Count];
        var failed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var unmeasured = 0;
        var done = 0;
        progress?.Report(new CatalogRefreshProgress(Id, "measuring", 0, list.Count));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var c = list[i];
                try
                {
                    using var files = await _http.GetJsonAsync(c.Href, token).ConfigureAwait(false);
                    var urls = files.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
                    var video = PickVideo(urls);
                    if (video is not null)
                    {
                        var measured = await MeasureAsync(urls, video, token).ConfigureAwait(false);
                        if (measured is null)
                            Interlocked.Increment(ref unmeasured);
                        else if (measured.Width >= MinWidth)
                            found[i] = await ToAssetAsync(c, video, measured, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.TryAdd(c.NasaId, 0);
                    Log.Warn($"Harvest {Id}: {c.NasaId} failed: {ex.Message}");
                }

                var n = Interlocked.Increment(ref done);
                progress?.Report(new CatalogRefreshProgress(Id, "measuring", n, list.Count));
            }).ConfigureAwait(false);

        var assets = found.OfType<AppleAsset>().ToList();
        Log.Info($"Harvest {Id}: {assets.Count} of {list.Count} candidates measured >=4K");

        if (unmeasured > 0)
            notes.Add($"{unmeasured} candidate(s) had no dimensions in metadata.json" +
                      (probe is null ? " and ffprobe is not installed" : " and ffprobe could not read them"));
        if (!failed.IsEmpty)
            notes.Add($"{failed.Count} candidate(s) could not be fetched; their previous entries were kept");

        return new HarvestResult(assets, [], failed.Keys.ToHashSet(StringComparer.Ordinal), notes);
    }

    public static IEnumerable<Candidate> ParseSearch(JsonElement root)
    {
        if (!root.TryGetProperty("collection", out var collection) ||
            !collection.TryGetProperty("items", out var items))
            yield break;

        foreach (var item in items.EnumerateArray())
        {
            var href = Json.Str(item, "href");
            if (href is null || !item.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                continue;

            var d = data[0];
            var nasaId = Json.Str(d, "nasa_id") ?? FolderOf(href);
            if (nasaId is null)
                continue;

            string? thumb = null;
            if (item.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            {
                thumb = links.EnumerateArray()
                             .Where(l => Json.Str(l, "rel") == "preview")
                             .Select(l => Json.Str(l, "href"))
                             .FirstOrDefault(h => h is not null);
            }

            yield return new Candidate(
                Href: href,
                NasaId: nasaId,
                Title: Json.Str(d, "title") ?? nasaId,
                Description: Json.Str(d, "description_508") ?? Json.Str(d, "description"),
                Creator: Json.Str(d, "secondary_creator") ?? Json.Str(d, "photographer")
                         ?? (Json.Str(d, "center") is { } center ? $"NASA {center}" : "NASA"),
                Keywords: Json.Strings(d, "keywords"),
                ThumbnailUrl: thumb is null ? null : Https(thumb));
        }
    }

    /// <summary>Prefer an explicitly-4K name, else the "~orig" master, else the first video — harvest.py's rule.</summary>
    public static string? PickVideo(IReadOnlyList<string> urls)
    {
        var videos = urls.Where(u => u.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                     u.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                         .ToList();
        if (videos.Count == 0)
            return null;

        // When the item's own name says "4K", every rendition matches the marker; take the
        // original among them, which is also the file metadata.json describes.
        var marked = videos.Where(v => FourKMarker().IsMatch(FileName(v))).ToList();
        var pool = marked.Count > 0 ? marked : videos;
        return pool.FirstOrDefault(v => FileName(v).Contains("orig", StringComparison.OrdinalIgnoreCase)) ?? pool[0];
    }

    private async Task<Measurement?> MeasureAsync(List<string> urls, string video, CancellationToken ct)
    {
        var metadataUrl = urls.FirstOrDefault(u => u.EndsWith("/metadata.json", StringComparison.OrdinalIgnoreCase));
        if (metadataUrl is not null)
        {
            using var meta = await _http.GetJsonAsync(Https(metadataUrl), ct).ConfigureAwait(false);
            if (ParseMetadata(meta.RootElement) is { } measured)
                return measured;
        }

        if (probe is null)
            return null;

        var probed = await probe.ProbeAsync(Https(video), ct).ConfigureAwait(false);
        return probed is null ? null : new Measurement(probed.Width, probed.Height, probed.DurationSeconds, probed.Codec);
    }

    /// <summary>NASA's metadata.json is an ExifTool dump of the original upload.</summary>
    public static Measurement? ParseMetadata(JsonElement root)
    {
        var width = Json.Long(root, "QuickTime:ImageWidth") ?? Json.Long(root, "QuickTime:SourceImageWidth");
        var height = Json.Long(root, "QuickTime:ImageHeight") ?? Json.Long(root, "QuickTime:SourceImageHeight");
        if (width is null or <= 0 || height is null or <= 0)
            return null;

        var duration = ParseDuration(Json.Str(root, "QuickTime:Duration") ?? Json.Str(root, "QuickTime:TrackDuration"));
        var codec = Json.Str(root, "QuickTime:CompressorID")?.ToLowerInvariant() switch
        {
            "hvc1" or "hev1" => "hevc",
            "avc1" or "avc3" => "h264",
            _ => null,
        };
        return new Measurement((int)width, (int)height, duration, codec);
    }

    /// <summary>ExifTool durations: "0:02:31", "1:00:00", or "12.5 s".</summary>
    public static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.EndsWith(" s", StringComparison.Ordinal) &&
            double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds;

        var parts = value.Split(':');
        if (parts.Length is 2 or 3)
        {
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return null;
                total = total * 60 + n;
            }
            return total;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : null;
    }

    private async Task<AppleAsset> ToAssetAsync(Candidate c, string video, Measurement m, CancellationToken ct)
    {
        var url = Https(video);
        var folder = FolderOf(c.Href) ?? c.NasaId;
        return new AppleAsset
        {
            Id = "nasa:" + video[(video.LastIndexOf('/') + 1)..], // v1 id
            Key = ClipKey.ForNasa(folder),
            AccessibilityLabel = c.Title,
            ShotId = folder,
            Scene = "space",
            Url4KSdr = url,
            Width = m.Width,
            Height = m.Height,
            DurationSeconds = m.DurationSeconds is { } d ? Math.Round(d, 1) : null,
            SizeBytes = await _http.GetLengthAsync(url, ct).ConfigureAwait(false),
            Codec = m.Codec,
            Creator = c.Creator,
            Description = c.Description,
            Licence = "NASA Media (public domain)",
            Subjects = c.Keywords.Count > 0 ? c.Keywords.ToList() : null,
            ThumbnailUrl = c.ThumbnailUrl,
        };
    }

    private static string FileName(string url) => url[(url.LastIndexOf('/') + 1)..];

    /// <summary>The asset folder: ".../video/&lt;nasa_id&gt;/collection.json" → nasa_id.</summary>
    private static string? FolderOf(string href)
    {
        var parts = href.Split('/');
        return parts.Length >= 2 ? Uri.UnescapeDataString(parts[^2]) : null;
    }

    /// <summary>
    /// collection.json still lists http:// links (the same host serves https), and some ids
    /// contain spaces ("Earth Views from the International Space Station") that it leaves raw.
    /// Upgraded and escaped here, so the catalogue stores the exact string mpv and the cache use.
    /// </summary>
    public static string Https(string url)
    {
        if (url.StartsWith("http://images-assets.nasa.gov/", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url["http://".Length..];

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url;
    }

    [GeneratedRegex(@"(3840|4096|_4k|uhd)", RegexOptions.IgnoreCase)]
    private static partial Regex FourKMarker();
}
