namespace Win4K.Core;

/// <summary>
/// Per-quality renditions of one clip. Apple publishes all five; the harvested sources
/// usually publish one (4K SDR), so <see cref="UrlFor"/> always falls back down the ladder.
/// </summary>
public sealed record ClipUrls(
    string? Uhd4KSdr = null,
    string? Uhd4KHdr = null,
    string? Hd1080Sdr = null,
    string? Hd1080Hdr = null,
    string? Hd1080H264 = null)
{
    public static ClipUrls Empty { get; } = new();

    public bool HasHdr => !string.IsNullOrWhiteSpace(Uhd4KHdr) || !string.IsNullOrWhiteSpace(Hd1080Hdr);

    public bool Has4K => !string.IsNullOrWhiteSpace(Uhd4KSdr) || !string.IsNullOrWhiteSpace(Uhd4KHdr);

    public bool Has1080 =>
        !string.IsNullOrWhiteSpace(Hd1080Sdr) || !string.IsNullOrWhiteSpace(Hd1080Hdr) || !string.IsNullOrWhiteSpace(Hd1080H264);

    public bool IsEmpty => !Has4K && !Has1080;

    /// <summary>
    /// Picks the URL for a quality, falling back down the ladder rather than returning null:
    /// a clip we can play at lower quality beats a gap in the playlist.
    /// </summary>
    public string? UrlFor(VideoQuality quality)
    {
        // Ordered preference lists, most-preferred first. Every Apple asset has all five, but
        // the schema does not guarantee it, so each list is a full fallback chain.
        string?[] chain = quality switch
        {
            VideoQuality.Uhd4KHdr => [Uhd4KHdr, Uhd4KSdr, Hd1080Hdr, Hd1080Sdr, Hd1080H264],
            VideoQuality.Uhd4KSdr => [Uhd4KSdr, Uhd4KHdr, Hd1080Sdr, Hd1080Hdr, Hd1080H264],
            VideoQuality.Hd1080Hdr => [Hd1080Hdr, Hd1080Sdr, Hd1080H264, Uhd4KSdr, Uhd4KHdr],
            VideoQuality.Hd1080Sdr => [Hd1080Sdr, Hd1080Hdr, Hd1080H264, Uhd4KSdr, Uhd4KHdr],
            VideoQuality.Hd1080H264 => [Hd1080H264, Hd1080Sdr, Hd1080Hdr, Uhd4KSdr, Uhd4KHdr],
            _ => [Uhd4KSdr, Hd1080Sdr, Hd1080H264, Uhd4KHdr, Hd1080Hdr],
        };

        foreach (var url in chain)
        {
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }
        return null;
    }
}

/// <summary>A point-of-interest caption: text shown from <see cref="AtSeconds"/> into the clip.</summary>
public sealed record ClipCaption(double AtSeconds, string Text);

/// <summary>
/// Everything known about one clip, from every source that lists it. This is the browser's
/// model; <see cref="Clip"/> is the thin playable projection the saver uses
/// (<see cref="ToClip"/>), and both share one identity: <see cref="Clip.Id"/> == <see cref="Key"/>.
/// </summary>
public sealed record ClipInfo
{
    // ---- identity and source -----------------------------------------------------------

    /// <summary>Namespaced, stable across refreshes and quality changes. See <see cref="Catalogs.ClipKey"/>.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The most specific origin: "apple" (resources-16 tarball), "apple-tvos26",
    /// "amazon-fireos8", "jetson-creative", "robin-fourcade", "archive-org", "nasa", "local".
    /// After a merge this is the primary source, the one whose URLs are used.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>Every source that lists this clip, primary first. One entry unless merged.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>
    /// The settings-level source this clip is enabled through: "apple", a registry catalogue
    /// id ("aerial-manifests", "archive-org", "nasa"), or "local:&lt;folder&gt;".
    /// </summary>
    public string CatalogId { get; init; } = "";

    // ---- text --------------------------------------------------------------------------

    public required string Title { get; init; }
    public string Category { get; init; } = "Other";
    public string? Subcategory { get; init; }
    public IReadOnlyList<ClipCaption> Captions { get; init; } = [];
    public string? Creator { get; init; }

    /// <summary>Source-supplied prose (NASA's description); null for most sources.</summary>
    public string? Description { get; init; }
    public string? Licence { get; init; }

    /// <summary>Raw subject keywords from the source; <see cref="Catalogs.TagBuilder"/> normalises them.</summary>
    public IReadOnlyList<string> Subjects { get; init; } = [];

    // ---- media facts -------------------------------------------------------------------

    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>Size of the primary rendition, when the source publishes it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>"hevc" or "h264" for the primary rendition, when known.</summary>
    public string? Codec { get; init; }

    public bool HasHdr => Urls.HasHdr;
    public TimeOfDay TimeOfDay { get; init; }

    // ---- links, tags, provenance -------------------------------------------------------

    public ClipUrls Urls { get; init; } = ClipUrls.Empty;

    /// <summary>Local file path for folder clips; they have no URLs and bypass the cache.</summary>
    public string? LocalPath { get; init; }

    public bool IsLocal => LocalPath is not null;

    public string? ThumbnailUrl { get; init; }

    /// <summary>Static (metadata-derived) tags. Live state tags come from <see cref="Catalogs.TagBuilder.Build"/> with a <see cref="Catalogs.ClipState"/>.</summary>
    public IReadOnlyList<Catalogs.ClipTag> Tags { get; init; } = [];

    /// <summary>First seen in a catalogue refresh; null when unknown (Apple's tarball, v1 catalogues).</summary>
    public DateTime? AddedUtc { get; init; }

    /// <summary>
    /// The playable projection at a quality. Null when there is no usable URL for any quality.
    /// </summary>
    public Clip? ToClip(VideoQuality quality)
    {
        if (LocalPath is not null)
        {
            return new Clip(Key, Title, Category, TimeOfDay, new Uri(LocalPath), IsLocal: true, SizeBytes);
        }

        var url = Urls.UrlFor(quality);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Only the primary rendition's size is known, so only claim it when that is what we picked.
        var primary = Urls.Uhd4KSdr ?? Urls.Uhd4KHdr ?? Urls.Hd1080Sdr;
        var size = string.Equals(url, primary, StringComparison.Ordinal) ? SizeBytes : null;

        return new Clip(Key, Title, Category, TimeOfDay, uri, IsLocal: false, size);
    }

    public override string ToString() => $"{Title} [{Key}]";
}
