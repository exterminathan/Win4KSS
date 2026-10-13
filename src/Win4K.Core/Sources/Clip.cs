namespace Win4K.Core;

public enum VideoQuality
{
    /// <summary>Pick from the primary display's width at runtime.</summary>
    Auto = 0,
    Uhd4KSdr = 1,
    Uhd4KHdr = 2,
    Hd1080Sdr = 3,
    Hd1080Hdr = 4,
    Hd1080H264 = 5,
}

public enum TimeOfDay
{
    Unknown = 0,
    Day = 1,
    Night = 2,
    /// <summary>Amazon's feed marks these; for day/night ordering they count as neither.</summary>
    Sunrise = 3,
    Sunset = 4,
}

/// <summary>
/// One playable video, normalised across sources so the playlist engine never needs to know
/// whether it came from Apple or a local folder.
/// </summary>
/// <param name="Id">
/// The clip key (<see cref="Catalogs.ClipKey"/>): "apple:&lt;GUID&gt;", "ia:&lt;item&gt;/&lt;file&gt;",
/// "nasa:&lt;id&gt;", "amazon:&lt;id&gt;", "local:&lt;path&gt;"… Stable across refreshes and quality
/// changes, and the same value as <see cref="ClipInfo.Key"/>. Settings written before clip keys
/// existed are migrated on load (<see cref="AppSettings.MigrateClipKeys"/>).
/// </param>
/// <param name="Title">Display name, e.g. "Los Angeles".</param>
/// <param name="Category">Display category, e.g. "Cities", or "Local" for folder sources.</param>
/// <param name="TimeOfDay">Best-effort; see <see cref="ClipHeuristics"/>.</param>
/// <param name="Url">Remote URL, or a file:// URI for local clips.</param>
/// <param name="IsLocal">Local clips bypass the download cache entirely.</param>
/// <param name="SizeBytes">Known size when available; null until we have seen a response header.</param>
public sealed record Clip(
    string Id,
    string Title,
    string Category,
    TimeOfDay TimeOfDay,
    Uri Url,
    bool IsLocal,
    long? SizeBytes = null)
{
    /// <summary>
    /// The exact string to hand to mpv: a plain path for local files, otherwise the URL with
    /// its escaping intact.
    ///
    /// Never use <c>Url.ToString()</c> for this. It *unescapes*, turning archive.org's
    /// <c>LA%2001.mp4</c> into <c>LA 01.mp4</c>; libcurl then rejects the space ("URL using
    /// bad/illegal format") and mpv fails the load with -13. That was every uncached
    /// archive-org stream failing while Apple's space-free URLs never did.
    /// </summary>
    public string PlayableUrl => Url.IsFile ? Url.LocalPath : Url.AbsoluteUri;

    public override string ToString() => $"{Title} [{Category}]";
}

public static class ClipHeuristics
{
    /// <summary>
    /// Derives time of day from the label suffix, which is all the modern manifest offers.
    ///
    /// Be aware this is thin: of Apple's 114 assets exactly 4 are marked ("Africa Night",
    /// "New York Night", "Korea and Japan Night", "Caribbean Day"). The legacy 1080p manifest
    /// had an explicit timeOfDay on every asset; the 4K one dropped it. So time-of-day
    /// ordering nudges those few clips and leaves the other 110 unaffected — it is not the
    /// meaningful filter it was in cdima/aerial, and the settings UI says so.
    /// </summary>
    public static TimeOfDay FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return TimeOfDay.Unknown;

        if (label.EndsWith(" Night", StringComparison.OrdinalIgnoreCase))
            return TimeOfDay.Night;

        if (label.EndsWith(" Day", StringComparison.OrdinalIgnoreCase))
            return TimeOfDay.Day;

        return TimeOfDay.Unknown;
    }

    /// <summary>Day runs 06:00–18:59, matching cdima/aerial's split.</summary>
    public static TimeOfDay Now(DateTime localNow) =>
        localNow.Hour is >= 6 and < 19 ? TimeOfDay.Day : TimeOfDay.Night;
}

public interface IVideoSource
{
    /// <summary>Stable key used in settings to enable/disable this source.</summary>
    string Id { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct = default);
}

/// <summary>
/// A source that can describe its clips fully (<see cref="ClipInfo"/>), not just as playable
/// <see cref="Clip"/>s. Every built-in source implements it; the clip browser reads all of
/// them through <see cref="Catalogs.ClipLibrary"/>.
/// </summary>
public interface IClipInfoSource : IVideoSource
{
    Task<IReadOnlyList<ClipInfo>> GetClipInfosAsync(CancellationToken ct = default);
}
