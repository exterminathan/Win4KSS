using System.Globalization;
using System.Text.RegularExpressions;

namespace Win4K.Core.Catalogs;

/// <summary>Facet groups, in the order the browser's sidebar shows them.</summary>
public enum TagFacet
{
    Source,
    Category,
    Subcategory,
    Subject,
    TimeOfDay,
    Resolution,
    Aspect,
    DynamicRange,
    Duration,
    Codec,
    Licence,
    State,
}

/// <summary>One faceted tag, e.g. (Resolution, "4K").</summary>
public readonly record struct ClipTag(TagFacet Facet, string Value)
{
    public override string ToString() => $"{Facet}:{Value}";
}

/// <summary>
/// Live per-clip state, owned by later phases (rotation and keep-offline in settings, cache
/// and download state in the cache). Passed in so <see cref="TagBuilder"/> stays pure.
/// </summary>
[Flags]
public enum ClipState
{
    None = 0,
    InRotation = 1,
    KeptOffline = 2,
    Cached = 4,
    Downloading = 8,
    /// <summary>Force the New tag regardless of <see cref="ClipInfo.AddedUtc"/> (e.g. not in KnownKeys).</summary>
    New = 16,
    Unhealthy = 32,
}

/// <summary>
/// Derives faceted tags from clip metadata. Pure: same input, same tags, no I/O, so the
/// browser can rebuild them for 1,500 clips on every state change without a thought.
/// </summary>
public static partial class TagBuilder
{
    /// <summary>A clip added within this window of "now" is tagged New.</summary>
    public static readonly TimeSpan NewWindow = TimeSpan.FromDays(30);

    public static class Values
    {
        public const string InRotation = "In rotation";
        public const string KeptOffline = "Kept offline";
        public const string Cached = "Cached";
        public const string Downloading = "Downloading";
        public const string New = "New";
        public const string Unhealthy = "Unhealthy";
        public const string HdrAvailable = "HDR available";
        public const string Ultrawide = "Ultrawide";
        public const string SixteenNine = "16:9";
    }

    /// <summary>Display name of a source id for the Source facet.</summary>
    public static string SourceName(string sourceId) => sourceId switch
    {
        "apple" or "apple-tvos26" => "Apple",
        "archive-org" => "Internet Archive",
        "nasa" => "NASA",
        "amazon-fireos8" => "Amazon",
        "jetson-creative" => "Jetson",
        "robin-fourcade" => "Robin",
        "local" => "Local",
        _ when sourceId.StartsWith("local", StringComparison.Ordinal) => "Local",
        _ => sourceId,
    };

    /// <summary>Vendor scenes and Apple categories that mean the same thing share one tag.</summary>
    private static readonly Dictionary<string, string> CategorySynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["city"] = "Cities",
        ["cities"] = "Cities",
        ["space"] = "Space",
        ["landscape"] = "Landscapes",
        ["landscapes"] = "Landscapes",
        ["underwater"] = "Underwater",
    };

    /// <summary>Subjects that say nothing about what is on screen.</summary>
    private static readonly HashSet<string> StopSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "stock footage", "free stock footage", "free usage stock footage", "film stock footage",
        "royalty free", "royalty free footage", "free footage", "free video", "free", "footage",
        "stock", "stock video", "video", "videos", "clip", "clips", "b-roll", "broll",
        "4k", "4k res", "4k resolution", "4k footage", "uhd", "ultra hd", "hd", "1080p", "2160p",
        "pixabay", "public domain", "creative commons", "res", "ultra high definition", "high definition",
        "4k uhd", "uhd 4k", "8k", "5k", "60fps", "slow motion footage",
    };

    public static IReadOnlyList<ClipTag> Build(ClipInfo clip, ClipState state = ClipState.None, DateTime? nowUtc = null)
    {
        var tags = new List<ClipTag>();
        void Add(TagFacet facet, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var tag = new ClipTag(facet, value.Trim());
            if (!tags.Contains(tag))
                tags.Add(tag);
        }

        // Source — every source that lists the clip, so a merged Apple clip is still one "Apple".
        foreach (var source in clip.SourceIds.Count > 0 ? clip.SourceIds : [clip.SourceId])
            Add(TagFacet.Source, SourceName(source));

        Add(TagFacet.Category, NormalizeCategory(clip.Category));
        Add(TagFacet.Subcategory, clip.Subcategory);

        foreach (var subject in Subjects(clip))
            Add(TagFacet.Subject, subject);

        Add(TagFacet.TimeOfDay, clip.TimeOfDay switch
        {
            TimeOfDay.Day => "Day",
            TimeOfDay.Night => "Night",
            TimeOfDay.Sunrise => "Sunrise",
            TimeOfDay.Sunset => "Sunset",
            _ => null,
        });

        Add(TagFacet.Resolution, Resolution(clip));
        Add(TagFacet.Aspect, Aspect(clip.Width, clip.Height));
        if (clip.HasHdr)
            Add(TagFacet.DynamicRange, Values.HdrAvailable);
        Add(TagFacet.Duration, DurationBucket(clip.DurationSeconds));
        Add(TagFacet.Codec, clip.Codec?.ToLowerInvariant() switch
        {
            "hevc" or "h265" or "hvc1" or "hev1" => "HEVC",
            "h264" or "avc" or "avc1" => "H.264",
            _ => null,
        });
        Add(TagFacet.Licence, clip.Licence);

        // State — live, supplied by the caller.
        if (state.HasFlag(ClipState.InRotation)) Add(TagFacet.State, Values.InRotation);
        if (state.HasFlag(ClipState.KeptOffline)) Add(TagFacet.State, Values.KeptOffline);
        if (state.HasFlag(ClipState.Cached)) Add(TagFacet.State, Values.Cached);
        if (state.HasFlag(ClipState.Downloading)) Add(TagFacet.State, Values.Downloading);
        if (state.HasFlag(ClipState.New) || IsNew(clip, nowUtc)) Add(TagFacet.State, Values.New);
        if (state.HasFlag(ClipState.Unhealthy)) Add(TagFacet.State, Values.Unhealthy);

        return tags;
    }

    public static bool IsNew(ClipInfo clip, DateTime? nowUtc) =>
        nowUtc is { } now && clip.AddedUtc is { } added && added > now - NewWindow;

    public static string? NormalizeCategory(string? category)
    {
        // "Other" is the no-category placeholder; "Mixed" is the archive.org Pixabay group's
        // scene, which says only that the group is not one thing.
        if (string.IsNullOrWhiteSpace(category) ||
            category.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("Mixed", StringComparison.OrdinalIgnoreCase))
            return null;
        return CategorySynonyms.TryGetValue(category.Trim(), out var mapped) ? mapped : TitleCase(category.Trim());
    }

    /// <summary>1080p below 2560 wide; 4K up to 5K; 5K+ up to 8K; 8K from 7680.</summary>
    public static string? Resolution(ClipInfo clip)
    {
        if (clip.Width is int w and > 0)
        {
            return w switch
            {
                >= 7680 => "8K",
                >= 5120 => "5K+",
                >= 3840 => "4K",
                >= 1900 => "1080p",
                _ => null,
            };
        }

        if (clip.Urls.Has4K) return "4K";
        if (clip.Urls.Has1080) return "1080p";
        return null;
    }

    /// <summary>
    /// Two buckets, matching what a 16:9 panel does with the clip: anything up to DCI's
    /// 1.90:1 fills it (panscan crops slightly); 2.1:1 and wider letterboxes.
    /// </summary>
    public static string? Aspect(int? width, int? height)
    {
        if (width is not int w || height is not int h || w <= 0 || h <= 0)
            return null;

        var ratio = (double)w / h;
        if (ratio >= 2.1) return Values.Ultrawide;
        if (ratio >= 1.7) return Values.SixteenNine;
        return null;
    }

    public static string? DurationBucket(double? seconds) => seconds switch
    {
        null or <= 0 => null,
        < 30 => "<30 s",
        < 60 => "30–60 s",
        < 180 => "1–3 min",
        < 600 => "3–10 min",
        _ => "10 min+",
    };

    // ---- subjects ----------------------------------------------------------------------

    /// <summary>
    /// The Subject facet, archive.org only: its subject keywords plus the clip-name prefix
    /// ("Thin Smoke 98 - 4K res" → "Thin Smoke"), which is the most specific description
    /// most of those clips have. Normalised to title case, stop-worded, and stripped of the
    /// creator's own name.
    ///
    /// NASA's keywords stay on <see cref="ClipInfo.Subjects"/> for search but are not a
    /// facet: they run to 20–40 per clip ("NASA", "JPL", "science", "engineers"…) and would
    /// bury the sidebar in one-off values.
    /// </summary>
    public static IReadOnlyList<string> Subjects(ClipInfo clip)
    {
        if (clip.SourceId != "archive-org")
            return [];

        var creatorWords = CreatorNames(clip.Creator);
        var result = new List<string>();

        void Consider(string raw)
        {
            var subject = NormalizeSubject(raw);
            if (subject is null || creatorWords.Contains(subject))
                return;
            if (!result.Contains(subject, StringComparer.OrdinalIgnoreCase))
                result.Add(subject);
        }

        if (LabelPrefix(clip.Title) is { } prefix)
            Consider(prefix);

        foreach (var raw in clip.Subjects)
        {
            foreach (var part in raw.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                Consider(part);
        }

        return result;
    }

    public static string? NormalizeSubject(string raw)
    {
        var value = Whitespace().Replace(raw, " ").Trim().Trim('.', ',', '-', '#');
        if (value.Length < 2 || value.Length > 40)
            return null;

        // "fire stock footage" says "fire".
        foreach (var suffix in new[] { " free stock footage", " stock footage", " footage", " 4k" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && value.Length > suffix.Length)
                value = value[..^suffix.Length].Trim();
        }

        if (StopSubjects.Contains(value) || value.Any(char.IsDigit) && !value.Any(char.IsLetter))
            return null;

        return TitleCase(value);
    }

    /// <summary>
    /// "Thin Smoke 98 - 30s - 4k res" → "Thin Smoke"; "Pocket Watch a42" → "Pocket Watch";
    /// "Timelapse Clouds3" → "Timelapse Clouds". Null for names that are not a description
    /// ("video-20001_large", a comma list of keywords, anything long).
    /// </summary>
    public static string? LabelPrefix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains(','))
            return null;

        var head = title.Split(" - ", 2)[0].Trim();
        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Drop trailing clip numbers: "98", "a42", "006".
        while (words.Count > 0 && ClipNumber().IsMatch(words[^1]))
            words.RemoveAt(words.Count - 1);

        if (words.Count == 0)
            return null;

        // "Clouds3" → "Clouds".
        words[^1] = words[^1].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');

        if (words.Count > 3 || words.Any(w => w.Length == 0 || w.Contains('_') || w.Any(char.IsDigit)))
            return null;

        return NormalizeSubject(string.Join(' ', words));
    }

    private static HashSet<string> CreatorNames(string? creator)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(creator))
            return set;

        foreach (var name in creator.Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = Whitespace().Replace(name, " ").Trim();
            if (trimmed.Length > 0)
                set.Add(TitleCase(trimmed));
        }
        return set;
    }

    /// <summary>Title case that leaves acronyms ("ISS", "NASA") alone.</summary>
    public static string TitleCase(string value)
    {
        var words = value.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length == 0 || word.All(c => !char.IsLetter(c) || char.IsUpper(c)) && word.Length > 1)
                continue;
            words[i] = char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z]?\d+[A-Za-z]?$")]
    private static partial Regex ClipNumber();
}
