using System.Buffers.Binary;
using System.Text;
using Win4K.Core.Media;

namespace Win4K.Tests;

public class Mp4BoxTests
{
    /// <summary>A box with a 32-bit size header and <paramref name="payload"/> zero bytes.</summary>
    private static byte[] Box(string type, int payload)
    {
        var bytes = new byte[8 + payload];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4));
        return bytes;
    }

    /// <summary>Just the header of a box claiming <paramref name="size"/> bytes (the body is "not downloaded").</summary>
    private static byte[] Header(string type, uint size)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, size);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4));
        return bytes;
    }

    private static byte[] LargeHeader(string type, ulong size)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 1);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), size);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Faststart_file_has_its_index_in_the_head()
    {
        var head = Concat(Box("ftyp", 16), Box("moov", 400), Header("mdat", 50_000_000), new byte[1000]);

        Assert.Equal(MoovLocation.InHead, Mp4Boxes.LocateMoov(head));
        Assert.Equal(["ftyp", "moov", "mdat"], Mp4Boxes.TopLevel(head).Select(b => b.Type));
    }

    [Fact]
    public void Index_after_a_large_mdat_is_beyond_the_head()
    {
        // ftyp, then an mdat that runs far past the 2 KB we "downloaded".
        var head = Concat(Box("ftyp", 16), Box("free", 8), Header("mdat", 300_000_000), new byte[2000]);

        Assert.Equal(MoovLocation.BeyondHead, Mp4Boxes.LocateMoov(head));
        var mdat = Mp4Boxes.TopLevel(head)[^1];
        Assert.Equal("mdat", mdat.Type);
        Assert.Equal(300_000_000, mdat.Size);
    }

    [Fact]
    public void Index_straddling_the_end_of_the_head_is_beyond_it()
    {
        var head = Concat(Box("ftyp", 16), Header("moov", 5000), new byte[100]);
        Assert.Equal(MoovLocation.BeyondHead, Mp4Boxes.LocateMoov(head));
    }

    [Fact]
    public void Largesize_boxes_are_walked()
    {
        // QuickTime's habit: wide + 64-bit mdat, index at the tail.
        var head = Concat(Box("ftyp", 12), Box("wide", 0), LargeHeader("mdat", 5_000_000_000), new byte[64]);

        var boxes = Mp4Boxes.TopLevel(head);
        Assert.Equal(["ftyp", "wide", "mdat"], boxes.Select(b => b.Type));
        Assert.Equal(5_000_000_000, boxes[^1].Size);
        Assert.Equal(MoovLocation.BeyondHead, Mp4Boxes.LocateMoov(head));
    }

    [Fact]
    public void Largesize_moov_inside_the_head_counts()
    {
        var moov = Concat(LargeHeader("moov", 16 + 32), new byte[32]);
        var head = Concat(Box("ftyp", 8), moov, Header("mdat", 1_000_000));
        Assert.Equal(MoovLocation.InHead, Mp4Boxes.LocateMoov(head));
    }

    [Fact]
    public void Size_zero_box_runs_to_end_of_file()
    {
        var head = Concat(Box("ftyp", 8), Header("mdat", 0), new byte[500]);

        var boxes = Mp4Boxes.TopLevel(head);
        Assert.Equal(-1, boxes[^1].Size);
        Assert.Equal(MoovLocation.BeyondHead, Mp4Boxes.LocateMoov(head));
    }

    [Fact]
    public void Non_mp4_bytes_are_recognised()
    {
        Assert.Equal(MoovLocation.NotMp4, Mp4Boxes.LocateMoov([]));
        Assert.Equal(MoovLocation.NotMp4, Mp4Boxes.LocateMoov(Encoding.ASCII.GetBytes("<!DOCTYPE html><html>")));
        // MPEG-TS sync bytes.
        Assert.Equal(MoovLocation.NotMp4, Mp4Boxes.LocateMoov(Enumerable.Repeat((byte)0x47, 376).ToArray()));
        // A printable but unknown first box.
        Assert.Equal(MoovLocation.NotMp4, Mp4Boxes.LocateMoov(Box("abcd", 8)));
    }

    [Fact]
    public void Malformed_sizes_stop_the_walk_without_throwing()
    {
        // Size smaller than its own header.
        var bad = Concat(Box("ftyp", 8), Header("moov", 4), new byte[32]);
        Assert.Single(Mp4Boxes.TopLevel(bad));
        Assert.Equal(MoovLocation.BeyondHead, Mp4Boxes.LocateMoov(bad));

        // A header cut off mid-way.
        var cut = Concat(Box("ftyp", 8), new byte[] { 0, 0, 1 });
        Assert.Single(Mp4Boxes.TopLevel(cut));

        // A largesize header cut off before its 64-bit size.
        var cutLarge = Concat(Box("ftyp", 8), LargeHeader("mdat", 1)[..12]);
        Assert.Single(Mp4Boxes.TopLevel(cutLarge));
    }
}
