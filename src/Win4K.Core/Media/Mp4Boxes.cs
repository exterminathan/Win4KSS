using System.Buffers.Binary;

namespace Win4K.Core.Media;

/// <summary>One top-level ISO-BMFF (MP4 / QuickTime) box. <see cref="Size"/> is -1 for a box that runs to end of file.</summary>
public readonly record struct Mp4Box(string Type, long Offset, long Size)
{
    public long End => Size < 0 ? long.MaxValue : Offset + Size;
}

/// <summary>Where the movie index (<c>moov</c>) sits relative to a downloaded head of the file.</summary>
public enum MoovLocation
{
    /// <summary>The whole <c>moov</c> box is inside the head, so ffmpeg can read the head on its own.</summary>
    InHead,

    /// <summary><c>moov</c> starts after the head, or straddles its end: the index is at the tail.</summary>
    BeyondHead,

    /// <summary>The bytes do not start like an MP4/QuickTime file.</summary>
    NotMp4,
}

/// <summary>
/// Walks the top-level boxes of an MP4/QuickTime head segment — just enough to tell whether
/// a truncated download is playable. A file whose <c>moov</c> comes after <c>mdat</c> cannot
/// be decoded from its first 20 MB; ffmpeg has to fetch the tail, which only works over HTTP.
/// Pure and allocation-light; never reads past the span.
/// </summary>
public static class Mp4Boxes
{
    /// <summary>Box types that may legitimately open a file. Anything else means "not an MP4".</summary>
    private static readonly HashSet<string> LeadingTypes = new(StringComparer.Ordinal)
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "moof", "styp", "sidx", "pdin", "meta",
    };

    /// <summary>
    /// The top-level boxes whose headers lie inside <paramref name="head"/>. The last box may
    /// extend past the end of the span (its <see cref="Mp4Box.End"/> says how far). Stops at
    /// the first malformed header.
    /// </summary>
    public static IReadOnlyList<Mp4Box> TopLevel(ReadOnlySpan<byte> head)
    {
        var boxes = new List<Mp4Box>();
        long offset = 0;

        while (offset + 8 <= head.Length)
        {
            var at = (int)offset;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head[at..]);
            var typeBytes = head.Slice(at + 4, 4);
            if (!IsPrintable(typeBytes))
                break;
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            long header = 8;

            if (size == 1)
            {
                // 64-bit "largesize" follows the type.
                if (offset + 16 > head.Length)
                    break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[(at + 8)..]);
                header = 16;
                if (size < 0)
                    break;
            }
            else if (size == 0)
            {
                // Runs to end of file: nothing follows it.
                boxes.Add(new Mp4Box(type, offset, -1));
                break;
            }

            if (size < header)
                break;

            boxes.Add(new Mp4Box(type, offset, size));
            offset += size;
        }

        return boxes;
    }

    /// <summary>Whether <paramref name="head"/> (the first bytes of a file) carries the complete <c>moov</c> box.</summary>
    public static MoovLocation LocateMoov(ReadOnlySpan<byte> head)
    {
        var boxes = TopLevel(head);
        if (boxes.Count == 0 || !LeadingTypes.Contains(boxes[0].Type))
            return MoovLocation.NotMp4;

        foreach (var box in boxes)
        {
            if (box.Type == "moov")
                return box.Size > 0 && box.End <= head.Length ? MoovLocation.InHead : MoovLocation.BeyondHead;
        }

        // Ran off the end of the head (or into a run-to-EOF mdat) without meeting moov.
        return MoovLocation.BeyondHead;
    }

    /// <summary>
    /// Decides early, from the first bytes of a download, that the first
    /// <paramref name="headLength"/> bytes cannot contain the whole <c>moov</c>: either it is
    /// seen ending past that point, or a box that is not <c>moov</c> already runs past it.
    /// False means "fits" or "cannot tell yet" — keep downloading.
    /// </summary>
    public static bool IndexIsBeyond(ReadOnlySpan<byte> prefix, long headLength)
    {
        var boxes = TopLevel(prefix);
        if (boxes.Count == 0 || !LeadingTypes.Contains(boxes[0].Type))
            return false;

        foreach (var box in boxes)
        {
            if (box.Type == "moov")
                return box.Size <= 0 || box.End > headLength;
            if (box.End > headLength)
                return true;
        }
        return false;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            // QuickTime uses the © sign (0xA9) in some atom names, but never at top level.
            if (b is < 0x20 or > 0x7E)
                return false;
        }
        return true;
    }
}
