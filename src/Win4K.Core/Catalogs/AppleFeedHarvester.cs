using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// The aerial-manifests source: Apple's tvOS 26 manifest, Amazon Fire TV's, and the two
/// community feeds — all already in Apple's schema. harvest.py mirrored them verbatim; this
/// also resolves what the browser needs while the strings file is to hand: category and
/// subcategory names, point-of-interest captions, Amazon's time of day, and the vendor.
/// </summary>
public sealed partial class AppleFeedHarvester(HarvestHttp? http = null) : ICatalogHarvester
{
    public const string Id = "aerial-manifests";

    private readonly HarvestHttp _http = http ?? new HarvestHttp();

    public string SourceId => Id;

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apple-tvos26"] = "Apple",
        ["amazon-fireos8"] = "Amazon",
        ["jetson-creative"] = "Jetson Creative",
        ["robin-fourcade"] = "Robin Fourcade",
    };

    public async Task<HarvestResult> HarvestAsync(SourceDefinition source, IProgress<CatalogRefreshProgress>? progress, CancellationToken ct)
    {
        var assets = new List<AppleAsset>();
        var categories = new List<AppleCategory>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var done = 0;

        foreach (var feed in source.Feeds)
        {
            progress?.Report(new CatalogRefreshProgress(Id, feed.Id, done, source.Feeds.Count));
            try
            {
                var manifest = AppleJson.Deserialize(await _http.GetStringAsync(feed.Url, ct).ConfigureAwait(false))
                               ?? throw new InvalidDataException("feed did not parse");

                IReadOnlyDictionary<string, string>? strings = null;
                if (!string.IsNullOrWhiteSpace(feed.StringsUrl))
                {
                    try
                    {
                        strings = ParseStrings(await _http.GetStringAsync(feed.StringsUrl!, ct).ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        // Captions are a nicety; the clips are still good without them.
                        notes.Add($"{feed.Id}: strings file unavailable ({ex.Message}); names fall back to keys");
                        Log.Warn($"Harvest {Id}: {feed.Id} strings failed: {ex.Message}");
                    }
                }

                var got = Transform(manifest, feed.Id, strings);
                assets.AddRange(got);
                categories.AddRange(manifest.Categories);
                Log.Info($"Harvest {Id}: {feed.Id}: {got.Count} assets with a 4K rendition");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(feed.Id);
                notes.Add($"{feed.Id}: fetch failed ({ex.Message}); previous entries kept");
                Log.Warn($"Harvest {Id}: {feed.Id} failed: {ex.Message}");
            }

            done++;
            progress?.Report(new CatalogRefreshProgress(Id, feed.Id, done, source.Feeds.Count));
        }

        if (failed.Count == source.Feeds.Count && source.Feeds.Count > 0)
            throw new HttpRequestException("every aerial-manifests feed failed");

        return new HarvestResult(assets, categories, failed, notes);
    }

    /// <summary>
    /// Resolves one feed in place and returns its 4K assets. Pure: no I/O. Also writes resolved
    /// names onto <paramref name="manifest"/>'s category tree.
    /// </summary>
    public static List<AppleAsset> Transform(AppleManifest manifest, string feedId, IReadOnlyDictionary<string, string>? strings)
    {
        var categories = new Dictionary<string, AppleCategory>(StringComparer.OrdinalIgnoreCase);
        var subcategories = new Dictionary<string, AppleCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in manifest.Categories)
        {
            category.Name ??= Lookup(strings, category.LocalizedNameKey);
            categories.TryAdd(category.Id, category);
            foreach (var sub in category.Subcategories ?? [])
            {
                sub.Name ??= Lookup(strings, sub.LocalizedNameKey) ?? CatalogClips.SubcategoryDisplayName(sub.LocalizedNameKey);
                subcategories.TryAdd(sub.Id, sub);
            }
        }

        var result = new List<AppleAsset>();
        foreach (var asset in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Url4KSdr) && string.IsNullOrWhiteSpace(asset.Url4KHdr))
                continue;

            asset.Feed = feedId;
            asset.Key = ClipKey.ForFeed(feedId, asset.Id);

            // Category: the key-derived name ("Space"), not the strings file's ("Earth"), so a
            // merged clip reads the same as Apple's resources-16 copy of it.
            asset.CategoryName ??= asset.Categories
                                        .Select(id => categories.TryGetValue(id, out var c) ? c.DisplayName : null)
                                        .FirstOrDefault(n => n is not null);
            asset.SubcategoryName ??= (asset.Subcategories ?? [])
                                          .Select(id => subcategories.TryGetValue(id, out var s) ? s.Name : null)
                                          .FirstOrDefault(n => n is not null);

            var captions = Captions(asset.PointsOfInterest, strings);
            asset.Captions = captions.Count > 0 ? captions : null;

            // All four vendors publish 3840x2160 HEVC for their 4K renditions (sources.json,
            // verified samples); Apple's naming says so outright ("_4K_HEVC").
            asset.Width ??= 3840;
            asset.Height ??= 2160;
            asset.Codec ??= "hevc";
            asset.Creator ??= Vendors.TryGetValue(feedId, out var vendor) ? vendor : feedId;
            asset.ThumbnailUrl ??= string.IsNullOrWhiteSpace(asset.PreviewImage) ? null : asset.PreviewImage;
            asset.TimeOfDayLabel = string.IsNullOrWhiteSpace(asset.TimeOfDayLabel) ? null : asset.TimeOfDayLabel.Trim().ToLowerInvariant();

            result.Add(asset);
        }
        return result;
    }

    /// <summary>
    /// Points of interest in time order. tvOS values are keys into the strings file; Amazon's
    /// and Robin's are the text itself; Jetson's are keys with no published strings file,
    /// which are dropped rather than shown as "video_inspire_…_0".
    /// </summary>
    public static List<CatalogCaption> Captions(Dictionary<string, string>? pointsOfInterest, IReadOnlyDictionary<string, string>? strings)
    {
        var captions = new List<CatalogCaption>();
        if (pointsOfInterest is null)
            return captions;

        foreach (var (at, value) in pointsOfInterest)
        {
            if (!double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                continue;

            var text = Lookup(strings, value) ?? (LooksLikeKey(value) ? null : Normalize(value));
            if (!string.IsNullOrWhiteSpace(text))
                captions.Add(new CatalogCaption { At = seconds, Text = text });
        }

        return captions.OrderBy(c => c.At).ToList();
    }

    public static IReadOnlyDictionary<string, string> ParseStrings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = Normalize(property.Value.GetString()!);
        }
        return result;
    }

    /// <summary>
    /// The strings file uses non-breaking spaces between words ("Hong\u00A0Kong"); read as
    /// Latin-1 anywhere along the way they become "\u00C2\u00A0". Both become a plain space, and so
    /// do the hard line breaks some captions carry ("Temblor Range\nin southern California"):
    /// the browser wraps captions itself.
    /// </summary>
    public static string Normalize(string value) =>
        Spaces().Replace(value.Replace("\u00C2\u00A0", " ").Replace('\u00A0', ' '), " ").Trim();

    private static string? Lookup(IReadOnlyDictionary<string, string>? strings, string? key) =>
        key is not null && strings is not null && strings.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>"GMT026_363A_103NC_E1027_32": no spaces, has an underscore — a key, not prose.</summary>
    private static bool LooksLikeKey(string value) => !value.Contains(' ') && value.Contains('_');

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
