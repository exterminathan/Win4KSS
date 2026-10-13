using System.Globalization;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// Turns a manifest in Apple's schema (Apple's own, or a harvested v1/v2 catalogue) into
/// <see cref="ClipInfo"/>. The one place that knows how each extension field maps.
/// </summary>
public static class CatalogClips
{
    /// <param name="manifest">Apple's manifest or a registry catalogue.</param>
    /// <param name="catalogId">"apple" for Apple's tarball, else the registry catalogue id.</param>
    public static IReadOnlyList<ClipInfo> ToClipInfos(AppleManifest manifest, string catalogId)
    {
        var categories = new Dictionary<string, AppleCategory>(StringComparer.OrdinalIgnoreCase);
        var subcategories = new Dictionary<string, AppleCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in manifest.Categories)
        {
            categories.TryAdd(category.Id, category);
            foreach (var sub in category.Subcategories ?? [])
                subcategories.TryAdd(sub.Id, sub);
        }

        var clips = new List<ClipInfo>(manifest.Assets.Count);
        foreach (var asset in manifest.Assets)
        {
            var clip = ToClipInfo(asset, catalogId, categories, subcategories);
            if (clip is null)
            {
                Log.Warn($"Catalogue {catalogId}: no usable URL for asset {asset.Id} ({asset.AccessibilityLabel})");
                continue;
            }
            clips.Add(clip);
        }
        return clips;
    }

    private static ClipInfo? ToClipInfo(
        AppleAsset asset,
        string catalogId,
        IReadOnlyDictionary<string, AppleCategory> categories,
        IReadOnlyDictionary<string, AppleCategory> subcategories)
    {
        var urls = asset.Urls;
        if (urls.IsEmpty)
            return null;

        var sourceId = !string.IsNullOrWhiteSpace(asset.Feed) ? asset.Feed! : catalogId;

        // Apple groups by category GUID; harvested catalogues resolve the name at harvest time
        // (v2) or carry a plain "scene" string (v1 and the vendor feeds).
        var category = asset.CategoryName
                       ?? asset.Categories
                               .Select(id => categories.TryGetValue(id, out var c) ? c.DisplayName : null)
                               .FirstOrDefault(n => n is not null)
                       ?? TitleCase(asset.Scene)
                       ?? "Other";

        // The community feeds put the specific place in "title" ("Cap Méchant") and the
        // region in the label ("France"): title it by the place, group it by the region.
        var hasVendorTitle = !string.IsNullOrWhiteSpace(asset.Title);
        var title = hasVendorTitle ? asset.Title!.Trim()
                  : !string.IsNullOrWhiteSpace(asset.AccessibilityLabel) ? asset.AccessibilityLabel
                  : asset.Id;

        var subcategory = asset.SubcategoryName
                          ?? (asset.Subcategories ?? [])
                                 .Select(id => subcategories.TryGetValue(id, out var s) ? s.Name ?? SubcategoryDisplayName(s.LocalizedNameKey) : null)
                                 .FirstOrDefault(n => n is not null)
                          ?? (hasVendorTitle && !string.IsNullOrWhiteSpace(asset.AccessibilityLabel) ? asset.AccessibilityLabel : null);

        // Apple publishes no media facts, but its renditions are uniform: 3840x2160 HEVC for
        // every 4K file (the URLs say so: "_4K_HEVC").
        var isApple = catalogId == AppleAerialSource.SourceId;
        var has4K = urls.Has4K;

        var clip = new ClipInfo
        {
            Key = ClipKey.ForAsset(catalogId, asset),
            SourceId = sourceId,
            SourceIds = [sourceId],
            CatalogId = catalogId,
            Title = title,
            Category = category,
            Subcategory = subcategory,
            Captions = (asset.Captions ?? []).Select(c => new ClipCaption(c.At, c.Text)).ToList(),
            Creator = asset.Creator ?? (isApple ? "Apple" : null),
            Description = asset.Description,
            Licence = asset.Licence,
            Subjects = asset.Subjects ?? [],
            Width = asset.Width ?? (isApple && has4K ? 3840 : null),
            Height = asset.Height ?? (isApple && has4K ? 2160 : null),
            DurationSeconds = asset.DurationSeconds,
            SizeBytes = asset.SizeBytes,
            Codec = asset.Codec ?? (isApple ? "hevc" : null),
            TimeOfDay = ParseTimeOfDay(asset.TimeOfDayLabel) is var t and not TimeOfDay.Unknown
                ? t
                : ClipHeuristics.FromLabel(asset.AccessibilityLabel),
            Urls = urls,
            ThumbnailUrl = asset.ThumbnailUrl ?? NullIfEmpty(asset.PreviewImage),
            AddedUtc = asset.AddedUtc,
        };

        return clip with { Tags = TagBuilder.Build(clip) };
    }

    public static TimeOfDay ParseTimeOfDay(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "day" => TimeOfDay.Day,
        "night" => TimeOfDay.Night,
        "sunrise" => TimeOfDay.Sunrise,
        "sunset" => TimeOfDay.Sunset,
        _ => TimeOfDay.Unknown,
    };

    /// <summary>"AerialSubcategoryPalauCoral" → "Palau Coral", for when no strings file resolved it.</summary>
    public static string? SubcategoryDisplayName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        const string prefix = "AerialSubcategory";
        if (key.StartsWith(prefix, StringComparison.Ordinal))
            key = key[prefix.Length..];

        return new AppleCategory { LocalizedNameKey = "AerialCategory" + key }.DisplayName;
    }

    /// <summary>"nature" -> "Nature". Scene values are lower-case single words.</summary>
    public static string? TitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return char.ToUpper(value[0], CultureInfo.InvariantCulture) + value[1..];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
