using System.Globalization;

namespace Win4K.Core.Media;

/// <summary>
/// The ffmpeg command lines behind every preview. Pure, so the exact arguments (and the seek
/// clamp for short clips) are unit-tested without ffmpeg installed.
/// </summary>
public static class PreviewCommands
{
    /// <summary>Width of every still and hover clip, in pixels; height keeps the aspect (even).</summary>
    public const int Width = 480;

    public const double HoverSeconds = 4;

    /// <summary>Where previews are taken from: far enough in to clear a fade from black.</summary>
    public const double DefaultSeekSeconds = 8;

    /// <summary>Below this duration the default seek would run off the end, so seek to a quarter instead.</summary>
    public const double ShortClipSeconds = 12;

    /// <summary>JPEG quality for ffmpeg's mjpeg encoder: 2 (best) … 31. 4 keeps a 480 px still near 30 KB.</summary>
    public const int JpegQuality = 4;

    /// <summary>Per-read network timeout for ffmpeg over HTTPS, in microseconds.</summary>
    private const string NetworkTimeoutMicros = "20000000";

    /// <summary>
    /// The preview's start time: <see cref="DefaultSeekSeconds"/>, or a quarter of the way in
    /// for clips shorter than <see cref="ShortClipSeconds"/>. Unknown duration → the default.
    /// </summary>
    public static double SeekSeconds(double? durationSeconds)
    {
        if (durationSeconds is not { } d || double.IsNaN(d) || d <= 0)
            return DefaultSeekSeconds;
        return d < ShortClipSeconds ? Math.Round(d * 0.25, 3) : DefaultSeekSeconds;
    }

    /// <summary>
    /// A 4 s, 480 px, silent H.264 clip with the index up front, so the browser starts it
    /// instantly. <c>yuv420p</c> is not optional: Apple's sources are 10-bit (Main10), and
    /// libx264 would otherwise follow them into High 10, which Chromium cannot decode.
    /// </summary>
    public static IReadOnlyList<string> Hover(string input, string output, double seekSeconds, string? caFile)
    {
        var args = Prefix();
        args.AddRange(["-ss", Format(seekSeconds), "-t", Format(HoverSeconds)]);
        AddInput(args, input, caFile);
        args.AddRange(
        [
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale={Width}:-2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            output,
        ]);
        return args;
    }

    /// <summary>
    /// One 480 px JPEG frame from a video. Decodes keyframes only and takes the first one at or
    /// before the seek point: far cheaper than an accurate seek through 4K HEVC.
    /// </summary>
    public static IReadOnlyList<string> StillFromVideo(string input, string output, double seekSeconds, string? caFile)
    {
        var args = Prefix();
        args.AddRange(["-ss", Format(seekSeconds), "-noaccurate_seek", "-skip_frame", "nokey"]);
        AddInput(args, input, caFile);
        args.AddRange(["-map", "0:v:0", "-frames:v", "1", "-vf", $"scale={Width}:-2", "-q:v", Format(JpegQuality), output]);
        return args;
    }

    /// <summary>Re-encodes a downloaded thumbnail (JPEG or PNG) as a JPEG no wider than 480 px; never upscales.</summary>
    public static IReadOnlyList<string> StillFromImage(string input, string output)
    {
        var args = Prefix();
        args.AddRange(["-i", input, "-frames:v", "1", "-vf", $"scale='min({Width},iw)':-2", "-q:v", Format(JpegQuality), output]);
        return args;
    }

    public static bool IsRemote(string input) =>
        input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static List<string> Prefix() => ["-hide_banner", "-nostdin", "-loglevel", "error", "-y"];

    private static void AddInput(List<string> args, string input, string? caFile)
    {
        if (IsRemote(input))
        {
            // ffmpeg here is a GnuTLS build with tls_verify defaulting to 0 — i.e. no checking
            // at all. Turn it on and hand it the same bundle mpv uses (Windows roots + Apple's).
            args.AddRange(["-rw_timeout", NetworkTimeoutMicros]);
            if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && caFile is not null)
                args.AddRange(["-tls_verify", "1", "-ca_file", caFile]);
        }
        args.AddRange(["-i", input]);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
