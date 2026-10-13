using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// Internet Archive: one advanced search per sources.json group, then metadata/&lt;id&gt; for
/// every hit, keeping each video file that is at least 3840 wide. archive.org reports width
/// and height per file, so nothing is downloaded to filter.
///
/// Port of scripts/harvest.py's archive-org harvester, plus the metadata it threw away:
/// subjects, creator, licence, per-file length, and a .thumbs frame for the browser.
/// </summary>
public sealed partial class ArchiveOrgHarvester(HarvestHttp? http = null, int parallelism = 12) : ICatalogHarvester
{
    public const string Id = "archive-org";

    /// <summary>Anything narrower is not worth cataloguing for a 4K screen saver.</summary>
    public const int MinWidth = 3840;

    private const string SearchRows = "300";
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm"];

    private readonly HarvestHttp _http = http ?? new HarvestHttp();

    public string SourceId => Id;

    public static string SearchUrl(string query) =>
        "https://archive.org/advancedsearch.php?q=" + Uri.EscapeDataString(query) +
        $"&rows={SearchRows}&page=1&output=json&fl%5B%5D=identifier";

    public static string MetadataUrl(string identifier) => $"https://archive.org/metadata/{identifier}";

    /// <summary>
    /// Same escaping as Python's urllib.parse.quote: every byte but unreserved characters
    /// percent-encoded, '/' kept. Must match the v1 catalogue byte for byte — the video
    /// cache is keyed by URL, so a different escaping would orphan every cached clip.
    /// </summary>
    public static string DownloadUrl(string identifier, string fileName) =>
        $"https://archive.org/download/{identifier}/" +
        string.Join('/', fileName.Split('/').Select(Uri.EscapeDataString));

    public async Task<HarvestResult> HarvestAsync(SourceDefinition source, IProgress<CatalogRefreshProgress>? progress, CancellationToken ct)
    {
        var assets = new List<AppleAsset>();
        var failed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var group in source.Groups)
        {
            // A failed search fails the whole harvest: without the item list we cannot tell
            // "gone upstream" from "not fetched", and the refresher keeps the old catalogue.
            var ids = await SearchAsync(group.Query, ct).ConfigureAwait(false);
            Log.Info($"Harvest {Id}: {group.Name}: {ids.Count} items");

            var results = new List<AppleAsset>[ids.Count];
            var done = 0;
            progress?.Report(new CatalogRefreshProgress(Id, $"{group.Name}: items", 0, ids.Count));

            await Parallel.ForEachAsync(
                Enumerable.Range(0, ids.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (i, token) =>
                {
                    try
                    {
                        using var meta = await _http.GetJsonAsync(MetadataUrl(ids[i]), token).ConfigureAwait(false);
                        results[i] = ParseItem(meta.RootElement, ids[i], group.Scene);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed.TryAdd(ids[i], 0);
                        results[i] = [];
                        Log.Warn($"Harvest {Id}: metadata for {ids[i]} failed: {ex.Message}");
                    }

                    var n = Interlocked.Increment(ref done);
                    progress?.Report(new CatalogRefreshProgress(Id, $"{group.Name}: items", n, ids.Count));
                }).ConfigureAwait(false);

            foreach (var got in results)
                assets.AddRange(got);
        }

        if (!failed.IsEmpty)
            notes.Add($"{failed.Count} item(s) could not be fetched; their previous entries were kept");

        return new HarvestResult(assets, [], failed.Keys.ToHashSet(StringComparer.Ordinal), notes);
    }

    public async Task<List<string>> SearchAsync(string query, CancellationToken ct)
    {
        using var doc = await _http.GetJsonAsync(SearchUrl(query), ct).ConfigureAwait(false);
        var docs = doc.RootElement.GetProperty("response").GetProperty("docs");

        // youtube-* items are re-uploads with no rights and usually transcoded; skip them.
        return docs.EnumerateArray()
                   .Select(d => Json.Str(d, "identifier"))
                   .OfType<string>()
                   .Where(id => !id.StartsWith("youtube-", StringComparison.Ordinal))
                   .ToList();
    }

    /// <summary>Every ≥4K video file in one item's metadata, as catalogue rows. Pure.</summary>
    public static List<AppleAsset> ParseItem(JsonElement root, string identifier, string scene)
    {
        var assets = new List<AppleAsset>();
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return assets;

        var metadata = root.TryGetProperty("metadata", out var m) ? m : default;
        var itemTitle = Json.Str(metadata, "title");
        var creator = Json.Str(metadata, "creator");
        var licenceUrl = Json.Str(metadata, "licenseurl");
        var subjects = Json.Strings(metadata, "subject")
                           .SelectMany(s => s.Split(';'))
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

        var licence = LicenceLabel(licenceUrl)
                      ?? (creator?.Contains("Pixabay", StringComparison.OrdinalIgnoreCase) == true ? "Pixabay Content License" : null);

        var all = files.EnumerateArray().ToList();
        var videos = all.Where(f => IsVideo(Json.Str(f, "name"))).ToList();
        var itemThumb = ItemThumbnail(all, identifier);

        foreach (var file in videos)
        {
            var name = Json.Str(file, "name")!;
            var width = (int)(Json.Long(file, "width") ?? 0);
            var height = (int)(Json.Long(file, "height") ?? 0);
            if (width < MinWidth)
                continue;

            var stem = Path.GetFileNameWithoutExtension(name);
            var title = CleanTitle(stem, itemTitle);

            assets.Add(new AppleAsset
            {
                Id = $"ia:{identifier}:{name}", // v1 id, kept so older readers still see a unique id
                Key = ClipKey.ForArchive(identifier, name),
                AccessibilityLabel = title,
                ShotId = identifier,
                Scene = scene,
                Url4KSdr = DownloadUrl(identifier, name),
                Width = width,
                Height = height,
                SizeBytes = Json.Long(file, "size") ?? 0,
                DurationSeconds = Json.Double(file, "length") is { } length ? Math.Round(length, 1) : null,
                Codec = CodecFromFormat(Json.Str(file, "format")),
                Creator = creator,
                Licence = licence,
                LicenceUrl = licenceUrl,
                Subjects = subjects.Count > 0 ? subjects : null,
                ThumbnailUrl = FrameThumbnail(all, identifier, name) ?? itemThumb,
            });
        }
        return assets;
    }

    private static bool IsVideo(string? name) =>
        name is not null && VideoExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The middle frame from the item's .thumbs folder that belongs to this file (linked by
    /// files[].original). The first frame is often a fade from black, the middle rarely is.
    /// </summary>
    private static string? FrameThumbnail(List<JsonElement> files, string identifier, string videoName)
    {
        var frames = files.Where(f => Json.Str(f, "format") == "Thumbnail" && Json.Str(f, "original") == videoName)
                          .Select(f => Json.Str(f, "name"))
                          .OfType<string>()
                          .Order(StringComparer.Ordinal)
                          .ToList();

        return frames.Count == 0 ? null : DownloadUrl(identifier, frames[frames.Count / 2]);
    }

    /// <summary>Items with no derivatives (the Pixabay mirror) still ship a JPEG or the item tile.</summary>
    private static string? ItemThumbnail(List<JsonElement> files, string identifier)
    {
        var names = files.Select(f => Json.Str(f, "name")).OfType<string>().ToList();
        var jpeg = names.FirstOrDefault(n => n.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) &&
                                             n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault(n => n == "__ia_thumb.jpg");
        return jpeg is null ? null : DownloadUrl(identifier, jpeg);
    }

    /// <summary>
    /// "Fire 66 - 45s - 4k res" → "Fire 66"; "water bubbles 048 - 4K res" → "Water Bubbles 048";
    /// Pixabay's "video-20001_large" → the item title, minus its "- Pixabay Free Stock Footage" tail.
    /// </summary>
    public static string CleanTitle(string stem, string? itemTitle)
    {
        if (GenericStem().IsMatch(stem) && !string.IsNullOrWhiteSpace(itemTitle))
        {
            var cleaned = PixabayTail().Replace(itemTitle, "").Trim().TrimEnd('-', ',').Trim();
            if (cleaned.Length > 0)
                return cleaned;
        }

        // Drop everything from the first dash-separated technical note: a running time ("45s",
        // "3 min 30s", "Fireworks 14- 28s"), "4k res", or a frame rate ("120fps").
        var title = ResolutionTail().Replace(TechnicalTail().Replace(stem, ""), "").Trim();
        if (title.Length == 0)
            title = stem;

        // All lower case ("water bubbles 048") reads as a file name; title-case it.
        if (title.Any(char.IsLetter) && !title.Any(char.IsUpper))
            title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title);

        return title;
    }

    /// <summary>creativecommons.org URL → "CC BY-NC-ND 3.0"; CC0 and PD mark likewise.</summary>
    public static string? LicenceLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains("/publicdomain/zero", StringComparison.OrdinalIgnoreCase))
            return "CC0";
        if (url.Contains("/publicdomain/mark", StringComparison.OrdinalIgnoreCase))
            return "Public Domain";

        var cc = CcLicence().Match(url);
        if (cc.Success)
            return $"CC {cc.Groups[1].Value.ToUpperInvariant()} {cc.Groups[2].Value}";

        return null;
    }

    /// <summary>archive.org's "format": "MPEG4" and "h.264" are H.264 in practice (sources.json, verified).</summary>
    public static string? CodecFromFormat(string? format) => format?.ToLowerInvariant() switch
    {
        null => null,
        var f when f.Contains("265") || f.Contains("hevc") => "hevc",
        var f when f.Contains("264") || f == "mpeg4" => "h264",
        _ => null,
    };

    [GeneratedRegex(@"^video-\d+_(large|source|medium|small|tiny)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericStem();

    [GeneratedRegex(@"\s*-?\s*Pixabay\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex PixabayTail();

    [GeneratedRegex(@"(\s*-\s*(\d+m\s?)?\d+s)?\s*-?\s*4k\s*res(olution)?(_\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionTail();

    [GeneratedRegex(@"\s*-\s*((\d+\s*(m|min)\s*)?\d+\s*s|\d+\s*min|4k\s*res(olution)?|\d+\s*fps)(\s*-.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTail();

    [GeneratedRegex(@"/licenses/([a-z-]+)/(\d+(\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CcLicence();
}
