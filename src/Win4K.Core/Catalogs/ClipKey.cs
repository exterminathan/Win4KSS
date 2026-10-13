using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// The namespaced identity every clip carries, used as <see cref="Clip.Id"/>,
/// <see cref="ClipInfo.Key"/>, and the key of every per-clip setting (failure counts today;
/// rotation and keep-offline sets later).
///
/// <list type="table">
///   <item><term>apple:&lt;GUID&gt;</term><description>Apple asset GUID, upper-case. resources-16 and tvOS 26 share it, which is what lets them merge.</description></item>
///   <item><term>ia:&lt;item&gt;/&lt;file&gt;</term><description>archive.org identifier and the raw (unescaped) file name, which may itself contain '/'.</description></item>
///   <item><term>nasa:&lt;nasa_id&gt;</term><description>NASA library id, the folder name under images-assets.nasa.gov/video/.</description></item>
///   <item><term>amazon:&lt;id&gt;</term><description>Fire TV asset id, verbatim ("content-…").</description></item>
///   <item><term>jetson:&lt;id&gt;</term><description>Jetson Creative community feed.</description></item>
///   <item><term>robin:&lt;id&gt;</term><description>Robin Fourcade community feed.</description></item>
///   <item><term>local:&lt;path&gt;</term><description>Full path of a file in a local folder.</description></item>
/// </list>
///
/// Keys never contain a URL, so they survive CDN moves and quality changes.
/// </summary>
public static class ClipKey
{
    public const string Apple = "apple";
    public const string Archive = "ia";
    public const string Nasa = "nasa";
    public const string Amazon = "amazon";
    public const string Jetson = "jetson";
    public const string Robin = "robin";
    public const string Local = "local";

    public static readonly IReadOnlyList<string> Namespaces = [Apple, Archive, Nasa, Amazon, Jetson, Robin, Local];

    /// <summary>aerial-manifests feed id → key namespace. A feed not listed here uses its own id.</summary>
    public static readonly IReadOnlyDictionary<string, string> FeedNamespaces =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple-tvos26"] = Apple,
            ["amazon-fireos8"] = Amazon,
            ["jetson-creative"] = Jetson,
            ["robin-fourcade"] = Robin,
        };

    public static string ForApple(string guid) => $"{Apple}:{guid.Trim().ToUpperInvariant()}";

    public static string ForArchive(string identifier, string fileName) => $"{Archive}:{identifier}/{fileName}";

    public static string ForNasa(string nasaId) => $"{Nasa}:{nasaId}";

    public static string ForLocal(string path) => $"{Local}:{path}";

    public static string ForFeed(string feedId, string assetId)
    {
        var ns = FeedNamespaces.TryGetValue(feedId, out var mapped) ? mapped : feedId;
        return ns == Apple ? ForApple(assetId) : $"{ns}:{assetId}";
    }

    /// <summary>The namespace of a key, or null for a bare (legacy) id.</summary>
    public static string? NamespaceOf(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0)
            return null;

        var ns = key[..colon];
        return Namespaces.Contains(ns, StringComparer.Ordinal) ? ns : null;
    }

    /// <summary>
    /// The key for a catalogue asset. v2 catalogues store it; v1 catalogues (and Apple's own
    /// manifest) do not, so it is derived from the source and the asset's raw id the same way
    /// the harvesters derive it — which is what makes AddedUtc diffing across v1→v2 work.
    /// </summary>
    public static string ForAsset(string sourceId, AppleAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Key))
            return asset.Key!;

        if (!string.IsNullOrWhiteSpace(asset.Feed))
            return ForFeed(asset.Feed!, asset.Id);

        return sourceId switch
        {
            AppleAerialSource.SourceId => ForApple(asset.Id),
            "archive-org" => Normalize(asset.Id),
            // The v1 id was "nasa:<file>"; the NASA id is the folder, which v1 kept in shotID.
            "nasa" => !string.IsNullOrWhiteSpace(asset.ShotId) ? ForNasa(asset.ShotId) : Normalize(asset.Id),
            _ => NamespaceOf(asset.Id) is not null ? asset.Id : $"{sourceId}:{asset.Id}",
        };
    }

    /// <summary>
    /// Rewrites an already-namespaced key that uses a pre-v2 shape into the current one.
    /// Idempotent: a current key comes back unchanged.
    ///  - "ia:&lt;item&gt;:&lt;file&gt;" → "ia:&lt;item&gt;/&lt;file&gt;". Item identifiers can contain neither
    ///    ':' nor '/', so whichever comes first after the prefix says which shape it is.
    ///  - "nasa:&lt;id&gt;~orig.mp4" → "nasa:&lt;id&gt;".
    /// </summary>
    public static string Normalize(string key)
    {
        switch (NamespaceOf(key))
        {
            case Archive:
            {
                var rest = key[(Archive.Length + 1)..];
                var separator = rest.IndexOfAny([':', '/']);
                if (separator > 0 && rest[separator] == ':')
                    return ForArchive(rest[..separator], rest[(separator + 1)..]);
                return key;
            }

            case Nasa:
            {
                var rest = key[(Nasa.Length + 1)..];
                var extension = Path.GetExtension(rest);
                if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                {
                    var stem = rest[..^extension.Length];
                    var tilde = stem.IndexOf('~');
                    return ForNasa(tilde > 0 ? stem[..tilde] : stem);
                }
                return key;
            }

            case Apple:
                return ForApple(key[(Apple.Length + 1)..]);

            default:
                return key;
        }
    }

    /// <summary>
    /// Maps a pre-Phase-B <see cref="Clip.Id"/> (as stored in settings) to its key.
    /// Unambiguous shapes are mapped directly; a bare vendor id ("robinfrcd_…",
    /// "video_inspire_…") is ambiguous between feeds, so it goes through
    /// <paramref name="resolveBare"/> (normally a lookup in the aerial-manifests catalogue).
    /// An id nothing recognises is returned unchanged: it simply matches no clip, which every
    /// consumer already tolerates.
    /// </summary>
    public static string FromLegacy(string id, Func<string, string?>? resolveBare = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return id;

        if (NamespaceOf(id) is not null)
            return Normalize(id);

        if (Guid.TryParse(id, out _))
            return ForApple(id);

        if (Path.IsPathFullyQualified(id))
            return ForLocal(id);

        if (id.StartsWith("content-", StringComparison.Ordinal))
            return $"{Amazon}:{id}";

        return resolveBare?.Invoke(id) ?? id;
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> BareIds = new(BuildBareIds);

    /// <summary>
    /// <see cref="FromLegacy"/>'s default for bare vendor ids: the key of the
    /// aerial-manifests asset with that id, or null. Loads the catalogue on first use only.
    /// </summary>
    public static string? ResolveFromCatalogues(string bareId) =>
        BareIds.Value.TryGetValue(bareId, out var key) ? key : null;

    private static IReadOnlyDictionary<string, string> BuildBareIds()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            const string catalogId = "aerial-manifests";
            foreach (var asset in SourceRegistry.LoadCatalog(catalogId)?.Assets ?? [])
                map.TryAdd(asset.Id, ForAsset(catalogId, asset));
        }
        catch (Exception ex)
        {
            Log.Error("ClipKey: could not load aerial-manifests to migrate legacy ids", ex);
        }
        return map;
    }
}
