using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win4K.Core.Apple;

/// <summary>
/// Apple's aerial manifest, as found in entries.json inside
/// https://sylvan.apple.com/Aerials/resources-{tvOSVersion}.tar
///
/// Note this is the *modern* schema. The legacy a1.phobos.apple.com manifest is a bare
/// array of { id, assets[] } with a per-asset timeOfDay, and is 1080p H.264 only — we do
/// not support it, because the whole point of this project is 4K.
/// </summary>
public sealed class AppleManifest
{
    public List<AppleAsset> Assets { get; set; } = [];
    public List<AppleCategory> Categories { get; set; } = [];
    public int Version { get; set; }
    public string? LocalizationVersion { get; set; }

    // ---- catalogue extension fields (see Catalogs/CatalogStore) --------------------------
    // Apple's manifest has none of these. Harvested catalogues written by schema v2 carry
    // them; v1 catalogues (scripts/harvest.py, retired) do not, and still load.

    /// <summary>2 for catalogues written by the C# harvesters; absent (null) for v1 and for Apple's own manifest.</summary>
    public int? SchemaVersion { get; set; }

    /// <summary>Registry source id, e.g. "archive-org".</summary>
    public string? SourceId { get; set; }

    public DateTime? GeneratedUtc { get; set; }
    public int? InitialAssetCount { get; set; }
}

public sealed class AppleAsset
{
    public string Id { get; set; } = "";

    /// <summary>Human-readable location, e.g. "Los Angeles", "Alaskan Jellies", "Africa Night".</summary>
    public string AccessibilityLabel { get; set; } = "";

    /// <summary>Apple's internal shot identifier, e.g. "SE_A016_C009". Stable; useful for logs.</summary>
    [JsonPropertyName("shotID")]
    public string ShotId { get; set; } = "";

    /// <summary>Category GUIDs; resolve against <see cref="AppleManifest.Categories"/>.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// Timestamp (seconds, as a string key) to caption-string key. The caption text itself lives
    /// in a binary plist inside the tarball, which we deliberately do not parse — see AppleStrings.
    /// </summary>
    public Dictionary<string, string>? PointsOfInterest { get; set; }

    /// <summary>
    /// Extension field used by the harvested catalogues in assets/registry, which follow
    /// Apple's schema but have no category GUIDs. Falls back to this for grouping.
    /// </summary>
    public string? Scene { get; set; }

    // Further extension fields. Apple's own assets are uniformly 3840x2160 and it publishes
    // neither size nor duration, but the harvested sources are not uniform and do.
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? SizeBytes { get; set; }
    public double? DurationSeconds { get; set; }

    // ---- schema v2 extension fields ----------------------------------------------------
    // Written by the Catalogs harvesters so a catalogue is self-contained: names, captions
    // and licences are resolved at harvest time and the loader needs no strings file.

    /// <summary>Namespaced clip key (<see cref="Catalogs.ClipKey"/>). Absent in v1 catalogues; derived on load.</summary>
    public string? Key { get; set; }

    /// <summary>Which aerial-manifests feed the asset came from, e.g. "amazon-fireos8".</summary>
    public string? Feed { get; set; }

    /// <summary>Vendor feeds (Jetson, Robin, Amazon) use this for the specific place; the label is the region.</summary>
    public string? Title { get; set; }

    /// <summary>"day", "night", "sunrise" or "sunset" — Amazon and the community feeds carry it; Apple dropped it.</summary>
    [JsonPropertyName("timeOfDay")] public string? TimeOfDayLabel { get; set; }

    /// <summary>tvOS 26 subcategory GUIDs; resolve against <see cref="AppleCategory.Subcategories"/>.</summary>
    public List<string>? Subcategories { get; set; }

    public string? PreviewImage { get; set; }
    public string? LocalizedNameKey { get; set; }

    /// <summary>Resolved display names, written at harvest time.</summary>
    public string? CategoryName { get; set; }
    public string? SubcategoryName { get; set; }

    /// <summary>Points of interest resolved to text, ordered by time.</summary>
    public List<CatalogCaption>? Captions { get; set; }

    public string? Creator { get; set; }

    /// <summary>Source-supplied prose (NASA's description), plain text.</summary>
    public string? Description { get; set; }

    /// <summary>Short licence label, e.g. "CC BY-NC-ND 3.0".</summary>
    public string? Licence { get; set; }
    public string? LicenceUrl { get; set; }

    /// <summary>Source-supplied subject keywords (archive.org subject, NASA keywords), unnormalised.</summary>
    public List<string>? Subjects { get; set; }

    /// <summary>"hevc" or "h264", for the primary (4K) rendition, when known.</summary>
    public string? Codec { get; set; }

    public string? ThumbnailUrl { get; set; }

    /// <summary>When this asset first appeared in the catalogue. Drives the "New" tag.</summary>
    public DateTime? AddedUtc { get; set; }

    /// <summary>Everything else in the feed (showInTopLevel, group, …), kept verbatim on round-trip.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonPropertyName("url-4K-HDR")] public string? Url4KHdr { get; set; }
    [JsonPropertyName("url-4K-SDR")] public string? Url4KSdr { get; set; }
    [JsonPropertyName("url-1080-HDR")] public string? Url1080Hdr { get; set; }
    [JsonPropertyName("url-1080-SDR")] public string? Url1080Sdr { get; set; }
    [JsonPropertyName("url-1080-H264")] public string? Url1080H264 { get; set; }

    /// <summary>
    /// Picks the URL for a quality, falling back down the ladder rather than returning null:
    /// a clip we can play at lower quality beats a gap in the playlist.
    /// </summary>
    public string? UrlFor(VideoQuality quality) => Urls.UrlFor(quality);

    [JsonIgnore]
    public ClipUrls Urls => new(Url4KSdr, Url4KHdr, Url1080Sdr, Url1080Hdr, Url1080H264);
}

/// <summary>One resolved point-of-interest caption.</summary>
public sealed class CatalogCaption
{
    /// <summary>Seconds into the clip.</summary>
    public double At { get; set; }
    public string Text { get; set; } = "";
}

public sealed class AppleCategory
{
    public string Id { get; set; } = "";

    /// <summary>e.g. "AerialCategoryUnderwater". Apple resolves this against a binary plist.</summary>
    public string LocalizedNameKey { get; set; } = "";

    public string? LocalizedDescriptionKey { get; set; }
    public int PreferredOrder { get; set; }
    public string? PreviewImage { get; set; }
    public string? RepresentativeAssetId { get; set; }

    /// <summary>tvOS 26 nests a subcategory tree under each category.</summary>
    public List<AppleCategory>? Subcategories { get; set; }

    /// <summary>Display name resolved from a strings file at harvest time, when one exists.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Display name derived from the localisation key rather than the binary plist in the
    /// tarball. "AerialCategoryUnderwater" becomes "Underwater". Apple's four keys are all
    /// single words, so this reads correctly without a bplist parser; if a future manifest
    /// adds a multi-word key, the camel-case split keeps it legible.
    /// </summary>
    public string DisplayName
    {
        get
        {
            const string prefix = "AerialCategory";
            var key = LocalizedNameKey;
            if (string.IsNullOrWhiteSpace(key))
                return "Other";

            if (key.StartsWith(prefix, StringComparison.Ordinal))
                key = key[prefix.Length..];

            return SplitCamelCase(key);
        }
    }

    private static string SplitCamelCase(string value)
    {
        if (value.Length == 0) return value;

        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Source-generated serialisation context. Used instead of reflection so the manifest still
/// deserialises correctly if this is ever published trimmed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppleManifest))]
public partial class AppleJsonContext : JsonSerializerContext
{
}

public static class AppleJson
{
    public static AppleManifest? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AppleJsonContext.Default.AppleManifest);

    public static string Serialize(AppleManifest manifest) =>
        JsonSerializer.Serialize(manifest, AppleJsonContext.Default.AppleManifest);
}
