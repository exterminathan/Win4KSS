using System.Text.RegularExpressions;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>
/// Collapses the same clip listed by several sources into one <see cref="ClipInfo"/>, and
/// drops archive.org's re-uploaded mirrors.
///
/// <para><b>Apple resources-16 ⋈ tvOS 26</b> share asset GUIDs, so they share a key. The
/// merged clip keeps Apple's tarball URLs (that is what the saver has always played and
/// cached) and takes tvOS 26's subcategory, captions and preview image. tvOS-only clips
/// stay as they are.</para>
///
/// <para><b>archive.org mirrors</b>: "Water614KRes_201507" is a byte-identical re-upload of
/// "Water614KRes". Same file name, same size → keep the original.</para>
/// </summary>
public static partial class ClipMerger
{
    /// <summary>Lower wins. Decides whose URLs a merged clip plays.</summary>
    private static int Priority(string sourceId) => sourceId switch
    {
        AppleAerialSource.SourceId => 0,
        "apple-tvos26" => 1,
        "archive-org" => 2,
        "nasa" => 3,
        "amazon-fireos8" => 4,
        "jetson-creative" => 5,
        "robin-fourcade" => 6,
        _ when sourceId.StartsWith("local", StringComparison.Ordinal) => 10,
        _ => 20,
    };

    public static IReadOnlyList<ClipInfo> Merge(IEnumerable<ClipInfo> clips)
    {
        var groups = new Dictionary<string, List<ClipInfo>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var clip in DropArchiveMirrors(clips))
        {
            if (!groups.TryGetValue(clip.Key, out var group))
            {
                groups[clip.Key] = group = [];
                order.Add(clip.Key);
            }
            group.Add(clip);
        }

        var merged = new List<ClipInfo>(order.Count);
        foreach (var key in order)
        {
            var group = groups[key];
            merged.Add(group.Count == 1 ? group[0] : MergeGroup(group));
        }
        return merged;
    }

    private static ClipInfo MergeGroup(List<ClipInfo> group)
    {
        var ranked = group.OrderBy(c => Priority(c.SourceId)).ToList();
        var primary = ranked[0];
        var rest = ranked.Skip(1).ToList();

        T? First<T>(Func<ClipInfo, T?> pick) where T : class =>
            ranked.Select(pick).FirstOrDefault(v => v is not null);

        T? FirstValue<T>(Func<ClipInfo, T?> pick) where T : struct =>
            ranked.Select(pick).FirstOrDefault(v => v.HasValue);

        var merged = primary with
        {
            SourceIds = ranked.SelectMany(c => c.SourceIds.Count > 0 ? c.SourceIds : [c.SourceId])
                              .Distinct(StringComparer.Ordinal).ToList(),
            Category = primary.Category != "Other" ? primary.Category
                       : rest.Select(c => c.Category).FirstOrDefault(c => c != "Other") ?? primary.Category,
            Subcategory = First(c => c.Subcategory),
            Captions = ranked.Select(c => c.Captions).FirstOrDefault(c => c.Count > 0) ?? [],
            Creator = First(c => c.Creator),
            Description = First(c => c.Description),
            Licence = First(c => c.Licence),
            Subjects = ranked.SelectMany(c => c.Subjects).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Width = FirstValue(c => c.Width),
            Height = FirstValue(c => c.Height),
            DurationSeconds = FirstValue(c => c.DurationSeconds),
            Codec = First(c => c.Codec),
            TimeOfDay = ranked.Select(c => c.TimeOfDay).FirstOrDefault(t => t != TimeOfDay.Unknown),
            ThumbnailUrl = First(c => c.ThumbnailUrl),
            AddedUtc = ranked.Select(c => c.AddedUtc).Min(), // nulls are ignored
        };

        return merged with { Tags = TagBuilder.Build(merged) };
    }

    /// <summary>
    /// Drops archive.org "_YYYYMM" re-uploads whose (file name, size) matches a clip from
    /// another item. Order-independent: whichever of the pair is the mirror is the one dropped.
    /// </summary>
    public static IEnumerable<ClipInfo> DropArchiveMirrors(IEnumerable<ClipInfo> clips)
    {
        var list = clips as IReadOnlyList<ClipInfo> ?? clips.ToList();

        var originals = new HashSet<(string, long?)>();
        foreach (var clip in list)
        {
            if (ArchiveParts(clip) is { } parts && !MirrorSuffix().IsMatch(parts.Item))
                originals.Add((parts.File, clip.SizeBytes));
        }

        foreach (var clip in list)
        {
            if (ArchiveParts(clip) is { } parts &&
                MirrorSuffix().IsMatch(parts.Item) &&
                originals.Contains((parts.File, clip.SizeBytes)))
            {
                continue;
            }
            yield return clip;
        }
    }

    /// <summary>Same rule over raw catalogue assets, for the harvester to apply before writing.</summary>
    public static List<AppleAsset> DropArchiveMirrors(IReadOnlyList<AppleAsset> assets, string sourceId)
    {
        var keyed = assets.Select(a => (Asset: a, Key: ClipKey.ForAsset(sourceId, a))).ToList();

        var originals = new HashSet<(string, long?)>();
        foreach (var (asset, key) in keyed)
        {
            if (ArchiveParts(key) is { } parts && !MirrorSuffix().IsMatch(parts.Item))
                originals.Add((parts.File, asset.SizeBytes));
        }

        return keyed.Where(x => !(ArchiveParts(x.Key) is { } parts &&
                                  MirrorSuffix().IsMatch(parts.Item) &&
                                  originals.Contains((parts.File, x.Asset.SizeBytes))))
                    .Select(x => x.Asset)
                    .ToList();
    }

    private static (string Item, string File)? ArchiveParts(ClipInfo clip) => ArchiveParts(clip.Key);

    private static (string Item, string File)? ArchiveParts(string key)
    {
        if (ClipKey.NamespaceOf(key) != ClipKey.Archive)
            return null;

        var rest = key[(ClipKey.Archive.Length + 1)..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? (rest[..slash], rest[(slash + 1)..]) : null;
    }

    [GeneratedRegex(@"_\d{6}$")]
    private static partial Regex MirrorSuffix();
}
