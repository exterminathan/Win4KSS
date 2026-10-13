using Win4K.Core.Apple;

namespace Win4K.Core;

/// <summary>
/// Apple TV aerials, streamed from sylvan.apple.com. 114 clips as of the tvOS 16 manifest.
/// </summary>
public sealed class AppleAerialSource : IClipInfoSource
{
    public const string SourceId = "apple";

    private readonly AppleManifestClient _client;
    private readonly Func<VideoQuality> _quality;

    public AppleAerialSource(AppleManifestClient? client = null, Func<VideoQuality>? quality = null)
    {
        _client = client ?? new AppleManifestClient();
        _quality = quality ?? (() => VideoQuality.Uhd4KSdr);
    }

    public string Id => SourceId;
    public string DisplayName => "Apple TV Aerials";

    public async Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct = default)
    {
        var manifest = await _client.GetAsync(ct: ct).ConfigureAwait(false);
        return ToClips(manifest, Resolve(_quality()));
    }

    public async Task<IReadOnlyList<ClipInfo>> GetClipInfosAsync(CancellationToken ct = default)
    {
        var manifest = await _client.GetAsync(ct: ct).ConfigureAwait(false);
        return Catalogs.CatalogClips.ToClipInfos(manifest, SourceId);
    }

    /// <summary>
    /// Resolves <see cref="VideoQuality.Auto"/> against the primary display.
    /// SDR is the deliberate default even on an HDR-capable panel: Apple's HDR clips are PQ,
    /// and tone-mapping them correctly is a bigger surface than the 4K/1080 decision.
    /// </summary>
    public static VideoQuality Resolve(VideoQuality requested, int? primaryWidth = null)
    {
        if (requested != VideoQuality.Auto)
            return requested;

        var width = primaryWidth ?? Display.PrimaryWidth;
        return width >= 2560 ? VideoQuality.Uhd4KSdr : VideoQuality.Hd1080Sdr;
    }

    /// <summary>
    /// The playable clips at a quality. Ids are clip keys ("apple:&lt;GUID&gt;"), the same
    /// identity the browser and the registry catalogues use; see <see cref="Catalogs.ClipKey"/>.
    /// </summary>
    public static IReadOnlyList<Clip> ToClips(AppleManifest manifest, VideoQuality quality) =>
        Catalogs.CatalogClips.ToClipInfos(manifest, SourceId)
                .Select(info => info.ToClip(quality))
                .OfType<Clip>()
                .ToList();
}
