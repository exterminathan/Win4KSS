using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Win4K.Core;
using Win4K.Core.Media;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

public class PreviewCommandTests
{
    [Theory]
    [InlineData(null, 8)]
    [InlineData(0.0, 8)]
    [InlineData(300.0, 8)]
    [InlineData(12.0, 8)]
    [InlineData(11.0, 2.75)]
    [InlineData(8.0, 2)]
    [InlineData(2.0, 0.5)]
    public void Seek_is_clamped_for_short_clips(double? duration, double expected) =>
        Assert.Equal(expected, PreviewCommands.SeekSeconds(duration));

    [Fact]
    public void Hover_command_matches_the_spec()
    {
        var args = PreviewCommands.Hover(@"C:\in.mov", @"C:\out.mp4", 8, caFile: "ca.pem");

        Assert.Equal(
            "-hide_banner -nostdin -loglevel error -y -ss 8 -t 4 -i C:\\in.mov -map 0:v:0 -an -sn -dn " +
            "-vf scale=480:-2 -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -movflags +faststart C:\\out.mp4",
            string.Join(' ', args));
        // Seeking on the input side (before -i) is what keeps a 4K HEVC source fast.
        Assert.True(FakeFfmpeg.IndexOf(args, "-ss") < FakeFfmpeg.IndexOf(args, "-i"));
        // A local file never gets TLS options.
        Assert.DoesNotContain("-ca_file", args);
    }

    [Fact]
    public void Short_clip_seek_is_formatted_invariantly()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var args = PreviewCommands.Hover("in.mp4", "out.mp4", PreviewCommands.SeekSeconds(9), null);
            Assert.Equal("2.25", args[FakeFfmpeg.IndexOf(args, "-ss") + 1]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Remote_input_verifies_tls_against_our_bundle()
    {
        var args = PreviewCommands.Hover("https://sylvan.apple.com/a.mov", "out.mp4", 8, caFile: @"C:\x\ca-bundle.pem");

        var i = FakeFfmpeg.IndexOf(args, "-tls_verify");
        Assert.True(i >= 0 && args[i + 1] == "1");
        Assert.Equal(@"C:\x\ca-bundle.pem", args[FakeFfmpeg.IndexOf(args, "-ca_file") + 1]);
        Assert.True(FakeFfmpeg.IndexOf(args, "-ca_file") < FakeFfmpeg.IndexOf(args, "-i"));
        Assert.Contains("-rw_timeout", args);
    }

    [Fact]
    public void Still_commands_take_one_keyframe_and_never_upscale_thumbnails()
    {
        var video = PreviewCommands.StillFromVideo("in.mp4", "out.jpg", 2.5, null);
        Assert.Equal("2.5", video[FakeFfmpeg.IndexOf(video, "-ss") + 1]);
        Assert.Contains("-noaccurate_seek", video);
        Assert.Equal("nokey", video[FakeFfmpeg.IndexOf(video, "-skip_frame") + 1]);
        Assert.Equal("1", video[FakeFfmpeg.IndexOf(video, "-frames:v") + 1]);
        Assert.True(FakeFfmpeg.IndexOf(video, "-skip_frame") < FakeFfmpeg.IndexOf(video, "-i"));
        Assert.Equal("out.jpg", video[^1]);

        var image = PreviewCommands.StillFromImage("thumb.png", "out.jpg");
        Assert.Equal("scale='min(480,iw)':-2", image[FakeFfmpeg.IndexOf(image, "-vf") + 1]);
    }

    [Fact]
    public void Key_hash_is_stable_filename_safe_and_distinct()
    {
        var a = MediaPreviewService.HashFor("ia:PocketWatch/Pocket Watch a26 - 1m 10s - 4k res.mp4");
        Assert.Equal(a, MediaPreviewService.HashFor("ia:PocketWatch/Pocket Watch a26 - 1m 10s - 4k res.mp4"));
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, MediaPreviewService.HashFor("ia:PocketWatch/Pocket Watch a27 - 1m 10s - 4k res.mp4"));
        // SHA-1("apple:X") — pinned so a refactor cannot silently orphan every preview on disk.
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes("apple:X")))[..16].ToLowerInvariant(),
            MediaPreviewService.HashFor("apple:X"));
    }

    [Fact]
    public void Placeholder_colour_is_stable_per_category()
    {
        Assert.Matches("^#[0-9a-f]{6}$", MediaPreviewService.PlaceholderColor("Space"));
        Assert.Equal(MediaPreviewService.PlaceholderColor("Space"), MediaPreviewService.PlaceholderColor(" space "));
        Assert.NotEqual(MediaPreviewService.PlaceholderColor("Space"), MediaPreviewService.PlaceholderColor("Underwater"));
        Assert.Equal(MediaPreviewService.PlaceholderColor(null), MediaPreviewService.PlaceholderColor("Other"));
    }

    [Fact]
    public void Image_width_is_read_from_the_header()
    {
        Assert.Equal(160, MediaPreviewService.ImageWidth(MediaPreviewServiceTests.JpegOfWidth(160)));
        Assert.Equal(1920, MediaPreviewService.ImageWidth(MediaPreviewServiceTests.JpegOfWidth(1920)));

        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x03, 0x84 }.CopyTo(png, 0);
        Assert.Equal(900, MediaPreviewService.ImageWidth(png));

        Assert.Null(MediaPreviewService.ImageWidth([0xFF, 0xD8, 0xFF])); // truncated
        Assert.Null(MediaPreviewService.ImageWidth("GIF89a"u8.ToArray()));
    }

    [Fact]
    public void Image_sniffing()
    {
        Assert.Equal(".jpg", MediaPreviewService.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(".png", MediaPreviewService.SniffImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal(".webp", MediaPreviewService.SniffImage("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(MediaPreviewService.SniffImage("<html>"u8.ToArray()));
    }
}

public class PreviewIndexTests
{
    private static PreviewRecord Ready(DateTime access, long still, long hover = 0) => new()
    {
        LastAccessUtc = access,
        Still = new PreviewItem { Status = PreviewStatus.Ready, File = "s.jpg", Bytes = still },
        Hover = hover > 0 ? new PreviewItem { Status = PreviewStatus.Ready, File = "h.mp4", Bytes = hover } : null,
    };

    [Fact]
    public void Evicts_least_recently_accessed_until_under_the_cap()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var index = new PreviewIndex();
        index.Entries["old"] = Ready(t, 100, 300);
        index.Entries["mid"] = Ready(t.AddHours(1), 100, 300);
        index.Entries["new"] = Ready(t.AddHours(2), 100, 300);
        index.Entries["failed"] = new PreviewRecord
        {
            LastAccessUtc = t.AddDays(-1),
            Still = new PreviewItem { Status = PreviewStatus.Failed, Bytes = 999 },
        };

        Assert.Equal(1200, index.TotalBytes); // failed items hold no file and count nothing
        Assert.Empty(index.SelectEvictions(1200));
        Assert.Equal(["old"], index.SelectEvictions(1000));
        Assert.Equal(["old", "mid"], index.SelectEvictions(500));
        // A protected key (being generated) is skipped, and the next one goes instead.
        Assert.Equal(["mid", "new"], index.SelectEvictions(500, new HashSet<string> { "old" }));
    }
}

/// <summary>
/// MediaPreviewService against a fake ffmpeg and a fake origin. Nothing here needs ffmpeg
/// installed or the network; each test gets its own previews folder.
/// </summary>
public class MediaPreviewServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeFfmpeg _ffmpeg = new();
    private readonly FixtureHttp _http = new();
    private readonly List<MediaPreviewService> _services = [];

    public MediaPreviewServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-previews-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string PreviewsDir => Path.Combine(_root, "previews");

    private MediaPreviewService Create(
        bool ffmpeg = true, long cap = 1L << 30, Func<ClipInfo, string?>? inputFile = null, int workers = 2)
    {
        var service = new MediaPreviewService(new AppSettings(), cache: null, new MediaPreviewOptions
        {
            Directory = PreviewsDir,
            Http = _http.Client(),
            Runner = _ffmpeg,
            Time = _time,
            FfmpegPath = ffmpeg ? "ffmpeg.exe" : null,
            AutoDetectFfmpeg = false,
            CaFile = () => "ca.pem",
            InputFileFor = inputFile ?? (_ => null),
            CapBytes = cap,
            Workers = workers,
            SaveDebounce = TimeSpan.FromMilliseconds(10),
        });
        _services.Add(service);
        return service;
    }

    private static ClipInfo Clip(string key, string? thumbnail = null, string? url = null, double? duration = null,
                                 long? size = null, string category = "Space") => new()
    {
        Key = key,
        SourceId = "test",
        Title = key,
        Category = category,
        ThumbnailUrl = thumbnail,
        DurationSeconds = duration,
        SizeBytes = size,
        Urls = new ClipUrls(Uhd4KSdr: url ?? $"https://cdn.example/{key.Replace(':', '_')}.mp4"),
    };

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private void ServeBytes(string url, byte[] body, string type = "image/jpeg") =>
        _http.Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(type) } },
        });

    /// <summary>A synthetic MP4 of <paramref name="length"/> bytes, index first or last.</summary>
    private static byte[] Mp4(bool moovFirst, int length = 64 * 1024)
    {
        static void Write(byte[] into, int at, string type, int size)
        {
            BinaryPrimitives.WriteUInt32BigEndian(into.AsSpan(at), (uint)size);
            Encoding.ASCII.GetBytes(type, into.AsSpan(at + 4));
        }

        var bytes = new byte[length];
        Write(bytes, 0, "ftyp", 16);
        const int moov = 512;
        if (moovFirst)
        {
            Write(bytes, 16, "moov", moov);
            Write(bytes, 16 + moov, "mdat", length - 16 - moov);
        }
        else
        {
            Write(bytes, 16, "mdat", length - 16 - moov);
            Write(bytes, length - moov, "moov", moov);
        }
        return bytes;
    }

    /// <summary>Serves a video honouring Range requests, recording the ranges asked for.</summary>
    private List<RangeHeaderValue?> ServeVideo(string url, byte[] body)
    {
        var ranges = new List<RangeHeaderValue?>();
        _videoRoutes[new Uri(url).AbsoluteUri] = (body, ranges);
        return ranges;
    }

    private readonly Dictionary<string, (byte[] Body, List<RangeHeaderValue?> Ranges)> _videoRoutes = new();

    private HttpClient RangeClient() => new(new RangeHandler(this)) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class RangeHandler(MediaPreviewServiceTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!owner._videoRoutes.TryGetValue(url, out var route))
                return await new HttpMessageInvoker(owner._http).SendAsync(request, ct);

            lock (route.Ranges)
                route.Ranges.Add(request.Headers.Range);
            var range = request.Headers.Range?.Ranges.Single();
            var from = (int)(range?.From ?? 0);
            var to = (int)Math.Min(range?.To ?? route.Body.Length - 1, route.Body.Length - 1);
            var slice = route.Body[from..(to + 1)];
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new CountingStream(slice, owner)) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } },
            };
            response.Content.Headers.ContentLength = slice.Length;
            if (range is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, route.Body.Length);
            return response;
        }
    }

    private long _served;

    /// <summary>Counts the video bytes the service actually read, to prove a download was abandoned.</summary>
    private sealed class CountingStream(byte[] body, MediaPreviewServiceTests owner) : MemoryStream(body)
    {
        // MemoryStream routes every other Read overload of a subclass through this one.
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            Interlocked.Add(ref owner._served, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var array = new byte[buffer.Length];
            var read = Read(array, 0, array.Length);
            array.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));
    }

    private MediaPreviewService CreateWithRanges(long headBytes, bool ffmpeg = true, bool heads = true)
    {
        var service = new MediaPreviewService(new AppSettings(), cache: null, new MediaPreviewOptions
        {
            Directory = PreviewsDir,
            Http = RangeClient(),
            Runner = _ffmpeg,
            Time = _time,
            FfmpegPath = ffmpeg ? "ffmpeg.exe" : null,
            AutoDetectFfmpeg = false,
            CaFile = () => "ca.pem",
            InputFileFor = _ => null,
            DefaultHeadBytes = headBytes,
            UseHeadSegments = heads,
            MinHeadBytes = 1024,
            SaveDebounce = TimeSpan.FromMilliseconds(10),
        });
        _services.Add(service);
        return service;
    }

    // ---- scheduling ------------------------------------------------------------------------

    [Fact]
    public void On_demand_requests_jump_ahead_of_background_fill()
    {
        var service = Create();
        service.SetCatalog(new[] { "a", "b", "c", "d", "e" }.Select(k => Clip(k)));
        service.BackgroundFill = true;

        Assert.Equal(("a", PreviewKind.Still), Take(service));

        // Cards d and e scroll into view: their stills come next, in the order given.
        var states = service.Request(["d", "e"], PreviewKind.Still);
        Assert.All(states, s => Assert.Equal(PreviewStatus.Pending, s.Status));
        Assert.Equal(("d", PreviewKind.Still), Take(service));

        // A newer request outranks the older one still waiting.
        service.Request(["c"], PreviewKind.Both);
        Assert.Equal(("c", PreviewKind.Still), Take(service));
        Assert.Equal(("c", PreviewKind.Hover), Take(service));
        Assert.Equal(("e", PreviewKind.Still), Take(service));

        // Then background resumes: b (a, c, d, e are running), then hover clips in catalogue order.
        Assert.Equal(("b", PreviewKind.Still), Take(service));
        Assert.Equal(("a", PreviewKind.Hover), Take(service));
    }

    [Fact]
    public void Cancelled_requests_leave_the_queue()
    {
        var service = Create();
        service.SetCatalog(new[] { "a", "b", "c" }.Select(k => Clip(k)));

        service.Request(["a", "b", "c"], PreviewKind.Still);
        service.Cancel(["a", "c"]);

        Assert.Equal(("b", PreviewKind.Still), Take(service));
        Assert.Null(service.TryTakeNext()); // background fill is off until Start/BackgroundFill
        Assert.Equal(PreviewStatus.Missing, service.Get("a", PreviewKind.Still).Status);
    }

    [Fact]
    public void Nothing_runs_until_Start()
    {
        var service = Create();
        service.SetCatalog([Clip("a", thumbnail: "https://img.example/a.jpg")]);
        ServeBytes("https://img.example/a.jpg", Jpeg);

        service.Request(["a"], PreviewKind.Still);
        Thread.Sleep(100);

        Assert.Empty(_http.Requests);
        Assert.Empty(_ffmpeg.Calls);
        Assert.Equal(PreviewStatus.Pending, service.Get("a", PreviewKind.Still).Status);
    }

    [Fact]
    public async Task Requested_previews_are_generated_and_announced()
    {
        var service = Create(inputFile: _ => Path.Combine(_root, "cached.mov"));
        service.SetCatalog([Clip("apple:A", thumbnail: "https://img.example/a.png")]);
        ServeBytes("https://img.example/a.png", Png, "image/png");

        var updates = new List<PreviewState>();
        var done = new TaskCompletionSource();
        service.PreviewUpdated += (_, e) =>
        {
            lock (updates) updates.Add(e.State);
            if (updates.Count == 2) done.TrySetResult();
        };

        service.Start(backgroundFill: false);
        service.Request(["apple:A"]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var still = updates.Single(u => u.Kind == PreviewKind.Still);
        var hash = MediaPreviewService.HashFor("apple:A");
        Assert.Equal(PreviewStatus.Ready, still.Status);
        Assert.Equal("thumbnail", still.Source);
        Assert.Equal(hash + ".jpg", still.FileName);
        Assert.Equal($"https://previews.win4k/{hash}.jpg?v={still.Version}", still.Url);
        Assert.True(File.Exists(Path.Combine(PreviewsDir, hash + ".jpg")));

        var hover = updates.Single(u => u.Kind == PreviewKind.Hover);
        Assert.Equal(PreviewStatus.Ready, hover.Status);
        Assert.Equal(hash + ".mp4", hover.FileName);

        // The thumbnail went through ffmpeg's image re-encode, not stored raw.
        Assert.Contains(_ffmpeg.Calls, c => c.Contains("scale='min(480,iw)':-2"));
        // Temp files are cleaned up once the job ends.
        await WaitUntil(() => Directory.GetFiles(Path.Combine(PreviewsDir, "tmp")).Length == 0);
    }

    // ---- inputs ----------------------------------------------------------------------------

    [Fact]
    public async Task Cached_file_is_used_without_touching_the_network()
    {
        var cached = Path.Combine(_root, "cached.mov");
        File.WriteAllBytes(cached, [1, 2, 3]);
        var service = Create(inputFile: c => c.Key == "a" ? cached : null);
        service.SetCatalog([Clip("a", duration: 6)]);

        var state = await service.GenerateAsync("a", PreviewKind.Hover);

        Assert.Equal(PreviewStatus.Ready, state.Status);
        Assert.Equal("local", state.Source);
        Assert.Empty(_http.Requests);
        var args = _ffmpeg.Calls.Single();
        Assert.Equal(cached, FakeFfmpeg.InputOf(args));
        Assert.Equal("1.5", args[FakeFfmpeg.IndexOf(args, "-ss") + 1]); // 25% of a 6 s clip
    }

    [Fact]
    public async Task Faststart_video_is_read_from_a_ranged_head_segment()
    {
        const string url = "https://cdn.example/clip%2001.mp4";
        var body = Mp4(moovFirst: true, length: 64 * 1024);
        var ranges = ServeVideo(url, body);
        var service = CreateWithRanges(headBytes: 16 * 1024);
        service.SetCatalog([Clip("ia:x/clip 01.mp4", url: url)]);

        var still = await service.GenerateAsync("ia:x/clip 01.mp4", PreviewKind.Still);

        Assert.Equal(PreviewStatus.Ready, still.Status);
        Assert.Equal("head", still.Source);
        var range = Assert.Single(ranges)!.Ranges.Single();
        Assert.Equal((0L, 16L * 1024 - 1), (range.From!.Value, range.To!.Value));

        // ffmpeg read a local temp file, not the URL; and the head was reused for the hover
        // clip in the same job instead of being fetched a second time.
        Assert.All(_ffmpeg.Calls, c => Assert.False(PreviewCommands.IsRemote(FakeFfmpeg.InputOf(c))));
        Assert.Equal(2, _ffmpeg.Calls.Count);
        Assert.Equal(PreviewStatus.Ready, service.Get("ia:x/clip 01.mp4", PreviewKind.Hover).Status);
    }

    [Fact]
    public async Task Tail_index_falls_back_to_ffmpeg_over_https_with_our_bundle()
    {
        const string url = "https://cdn.example/tail.mov";
        ServeVideo(url, Mp4(moovFirst: false, length: 64 * 1024));
        var service = CreateWithRanges(headBytes: 16 * 1024);
        service.SetCatalog([Clip("apple:T", url: url)]);

        var hover = await service.GenerateAsync("apple:T", PreviewKind.Hover);

        Assert.Equal(PreviewStatus.Ready, hover.Status);
        Assert.Equal("remote", hover.Source);
        var args = _ffmpeg.Calls.Single();
        Assert.Equal(url, FakeFfmpeg.InputOf(args));
        Assert.Equal("ca.pem", args[FakeFfmpeg.IndexOf(args, "-ca_file") + 1]);
        Assert.Equal("1", args[FakeFfmpeg.IndexOf(args, "-tls_verify") + 1]);
    }

    [Fact]
    public async Task Tail_index_is_spotted_without_downloading_the_whole_head()
    {
        const string url = "https://cdn.example/big-tail.mov";
        ServeVideo(url, Mp4(moovFirst: false, length: 8 * 1024 * 1024));
        var service = CreateWithRanges(headBytes: 4 * 1024 * 1024);
        service.SetCatalog([Clip("apple:B", url: url)]);

        var hover = await service.GenerateAsync("apple:B", PreviewKind.Hover);

        Assert.Equal("remote", hover.Source);
        Assert.InRange(Interlocked.Read(ref _served), 64 * 1024, 512 * 1024);
    }

    [Fact]
    public async Task A_small_thumbnail_is_replaced_by_a_frame_once_the_video_is_at_hand()
    {
        const string url = "https://cdn.example/archive.mp4";
        ServeVideo(url, Mp4(moovFirst: true));
        ServeBytes("https://img.example/small.jpg", JpegOfWidth(160));
        var service = CreateWithRanges(headBytes: 16 * 1024);
        service.SetCatalog([Clip("ia:x/archive.mp4", thumbnail: "https://img.example/small.jpg", url: url)]);
        var updates = new List<PreviewState>();
        service.PreviewUpdated += (_, e) => { lock (updates) updates.Add(e.State); };

        // Background fill does stills first: the 160 px thumbnail is all there is for now.
        var still = await service.GenerateAsync("ia:x/archive.mp4", PreviewKind.Still);
        Assert.Equal("thumbnail", still.Source);
        Assert.Empty(_videoRoutes[url].Ranges); // no video fetched for a still

        // The hover clip downloads the head anyway, and the still is upgraded from it.
        var hover = await service.GenerateAsync("ia:x/archive.mp4", PreviewKind.Hover);
        Assert.Equal("head", hover.Source);
        var upgraded = service.Get("ia:x/archive.mp4", PreviewKind.Still);
        Assert.Equal("head", upgraded.Source);
        Assert.Equal(still.FileName, upgraded.FileName);
        Assert.NotEqual(still.Url, upgraded.Url);
        Assert.Contains(updates, u => u.Kind == PreviewKind.Still && u.Source == "head");
        Assert.Single(_videoRoutes[url].Ranges);
    }

    [Fact]
    public async Task A_large_thumbnail_is_kept()
    {
        var cached = Path.Combine(_root, "cached.mov");
        File.WriteAllBytes(cached, [1]);
        ServeBytes("https://img.example/big.jpg", JpegOfWidth(900));
        var service = Create(inputFile: _ => cached);
        service.SetCatalog([Clip("tv:a", thumbnail: "https://img.example/big.jpg")]);

        Assert.Equal("thumbnail", (await service.GenerateAsync("tv:a", PreviewKind.Still)).Source);
        await service.GenerateAsync("tv:a", PreviewKind.Hover);
        Assert.Equal("thumbnail", service.Get("tv:a", PreviewKind.Still).Source);
    }

    [Fact]
    public async Task A_small_thumbnail_with_the_clip_cached_goes_straight_to_a_frame()
    {
        var cached = Path.Combine(_root, "cached.mov");
        File.WriteAllBytes(cached, [1]);
        ServeBytes("https://img.example/small.jpg", JpegOfWidth(160));
        var service = Create(inputFile: _ => cached);
        service.SetCatalog([Clip("ia:y", thumbnail: "https://img.example/small.jpg")]);

        var still = await service.GenerateAsync("ia:y", PreviewKind.Still);

        Assert.Equal("local", still.Source);
        Assert.Equal(cached, FakeFfmpeg.InputOf(_ffmpeg.Calls.Last()));
    }

    /// <summary>A JPEG header (SOI, APP0, SOF0) declaring <paramref name="width"/>×90 pixels.</summary>
    internal static byte[] JpegOfWidth(int width)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange("JFIF "u8.ToArray());
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 90, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    [Fact]
    public async Task Head_segments_can_be_switched_off()
    {
        const string url = "https://cdn.example/direct.mp4";
        var ranges = ServeVideo(url, Mp4(moovFirst: true));
        var service = CreateWithRanges(headBytes: 16 * 1024, heads: false);
        service.SetCatalog([Clip("ia:d", url: url)]);

        var hover = await service.GenerateAsync("ia:d", PreviewKind.Hover);

        Assert.Equal("remote", hover.Source);
        Assert.Empty(ranges);
        Assert.Equal(url, FakeFfmpeg.InputOf(_ffmpeg.Calls.Single()));
    }

    [Fact]
    public async Task A_head_too_short_for_the_seek_retries_over_https()
    {
        const string url = "https://cdn.example/short.mp4";
        ServeVideo(url, Mp4(moovFirst: true));
        var service = CreateWithRanges(headBytes: 16 * 1024);
        service.SetCatalog([Clip("nasa:S", url: url)]);
        // ffmpeg "produces nothing" from the head (the seek ran past the downloaded data).
        _ffmpeg.Behaviour = args => PreviewCommands.IsRemote(FakeFfmpeg.InputOf(args)) ? null : new ProcessResult(0, "", false);

        var hover = await service.GenerateAsync("nasa:S", PreviewKind.Hover);

        Assert.Equal(PreviewStatus.Ready, hover.Status);
        Assert.Equal("remote", hover.Source);
        Assert.Equal(2, _ffmpeg.Calls.Count);
    }

    [Fact]
    public void Head_size_follows_the_bitrate()
    {
        var service = CreateWithRanges(headBytes: 20L << 20);
        // Unknown bitrate → the default.
        Assert.Equal(20L << 20, service.HeadBytesFor(Clip("a")));
        // 1 MB/s for (8 + 4 + 2) s × 1.25 + 1 MB ≈ 18.5 MB.
        var mbps = service.HeadBytesFor(Clip("b", duration: 100, size: 100L << 20));
        Assert.InRange(mbps, 18L << 20, 19L << 20);
        // A very high bitrate is capped.
        Assert.Equal(64L << 20, service.HeadBytesFor(Clip("c", duration: 10, size: 2L << 30)));
    }

    [Fact]
    public async Task Html_error_page_is_never_stored_as_a_thumbnail()
    {
        var service = Create(ffmpeg: false);
        service.SetCatalog([Clip("a", thumbnail: "https://img.example/a.jpg")]);
        ServeBytes("https://img.example/a.jpg", "<html>Service Unavailable</html>"u8.ToArray(), "text/html");

        var still = await service.GenerateAsync("a", PreviewKind.Still);

        Assert.Equal(PreviewStatus.Failed, still.Status);
        Assert.Contains("HTML", still.Error);
        Assert.Empty(Directory.GetFiles(PreviewsDir, "*.jpg"));
    }

    // ---- without ffmpeg --------------------------------------------------------------------

    [Fact]
    public async Task Without_ffmpeg_thumbnails_are_stored_as_is_and_the_rest_are_placeholders()
    {
        var service = Create(ffmpeg: false);
        service.SetCatalog(
        [
            Clip("ia:jpg", thumbnail: "https://img.example/t.jpg"),
            Clip("tv:png", thumbnail: "https://img.example/t.png"),
            Clip("amazon:none", category: "Cities"),
        ]);
        ServeBytes("https://img.example/t.jpg", Jpeg);
        ServeBytes("https://img.example/t.png", Png, "image/png");

        Assert.False(service.Capabilities.FfmpegAvailable);
        Assert.False(service.Capabilities.HoverClips);

        var jpg = await service.GenerateAsync("ia:jpg", PreviewKind.Still);
        Assert.Equal(PreviewStatus.Ready, jpg.Status);
        Assert.EndsWith(".jpg", jpg.FileName);
        Assert.Equal(Jpeg, File.ReadAllBytes(Path.Combine(PreviewsDir, jpg.FileName!)));

        var png = await service.GenerateAsync("tv:png", PreviewKind.Still);
        Assert.EndsWith(".png", png.FileName);

        var none = await service.GenerateAsync("amazon:none", PreviewKind.Still);
        Assert.Equal(PreviewStatus.Placeholder, none.Status);
        Assert.Null(none.Url);
        Assert.Equal(MediaPreviewService.PlaceholderColor("Cities"), none.PlaceholderColor);

        // Hover clips are unavailable, never queued, and nothing ran.
        var hover = service.Request(["ia:jpg"], PreviewKind.Hover).Single();
        Assert.Equal(PreviewStatus.Unavailable, hover.Status);
        Assert.Null(service.TryTakeNext());
        Assert.Empty(_ffmpeg.Calls);
    }

    [Fact]
    public async Task Placeholders_made_without_ffmpeg_are_retried_once_it_appears()
    {
        var first = Create(ffmpeg: false);
        first.SetCatalog([Clip("amazon:none")]);
        Assert.Equal(PreviewStatus.Placeholder, (await first.GenerateAsync("amazon:none", PreviewKind.Still)).Status);
        first.Dispose();

        var second = Create(ffmpeg: true, inputFile: _ => Path.Combine(_root, "x.mov"));
        second.SetCatalog([Clip("amazon:none")]);
        var state = second.Request(["amazon:none"], PreviewKind.Still).Single();
        Assert.Equal(PreviewStatus.Pending, state.Status);
    }

    // ---- failures ------------------------------------------------------------------------

    [Fact]
    public async Task Failures_are_remembered_and_retried_after_a_day()
    {
        var service = Create(inputFile: _ => Path.Combine(_root, "in.mov"));
        service.SetCatalog([Clip("a")]);
        _ffmpeg.Behaviour = _ => new ProcessResult(1, "Invalid data found when processing input", false);

        var failed = await service.GenerateAsync("a", PreviewKind.Hover);
        Assert.Equal(PreviewStatus.Failed, failed.Status);
        Assert.Contains("Invalid data", failed.Error);
        var calls = _ffmpeg.Calls.Count;

        // Asked again within the window: reported as failed, not queued, not run.
        Assert.Equal(PreviewStatus.Failed, service.Request(["a"], PreviewKind.Hover).Single().Status);
        Assert.Null(service.TryTakeNext());
        Assert.Equal(PreviewStatus.Failed, (await service.GenerateAsync("a", PreviewKind.Hover)).Status);
        Assert.Equal(calls, _ffmpeg.Calls.Count);

        // A day later it is fair game again.
        _time.Advance(TimeSpan.FromHours(24));
        _ffmpeg.Behaviour = null;
        Assert.Equal(PreviewStatus.Pending, service.Request(["a"], PreviewKind.Hover).Single().Status);
        Assert.Equal(PreviewStatus.Ready, (await service.GenerateAsync("a", PreviewKind.Hover)).Status);
    }

    [Fact]
    public async Task Failure_memo_survives_a_restart()
    {
        var first = Create(inputFile: _ => "in.mov");
        first.SetCatalog([Clip("a")]);
        _ffmpeg.Behaviour = _ => new ProcessResult(1, "boom", false);
        await first.GenerateAsync("a", PreviewKind.Hover);
        first.Dispose();

        var second = Create(inputFile: _ => "in.mov");
        second.SetCatalog([Clip("a")]);
        Assert.Equal(PreviewStatus.Failed, second.Get("a", PreviewKind.Hover).Status);
        Assert.Equal(PreviewStatus.Failed, second.Request(["a"], PreviewKind.Hover).Single().Status);
    }

    [Fact]
    public async Task A_timed_out_ffmpeg_is_a_failure()
    {
        var service = Create(inputFile: _ => "in.mov");
        service.SetCatalog([Clip("a", duration: 60)]);
        _ffmpeg.Behaviour = _ => new ProcessResult(-1, "", TimedOut: true);

        var state = await service.GenerateAsync("a", PreviewKind.Hover);

        Assert.Equal(PreviewStatus.Failed, state.Status);
        Assert.Contains("timed out", state.Error);
    }

    // ---- LRU cap and persistence ---------------------------------------------------------

    [Fact]
    public async Task Folder_is_capped_by_evicting_the_least_recently_used()
    {
        var service = Create(cap: 2500, inputFile: _ => "in.mov");
        service.SetCatalog(new[] { "a", "b", "c" }.Select(k => Clip(k)));
        var evicted = new List<PreviewState>();
        service.PreviewUpdated += (_, e) =>
        {
            if (e.State.Status == PreviewStatus.Missing)
                lock (evicted) evicted.Add(e.State);
        };

        await service.GenerateAsync("a", PreviewKind.Hover);
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.GenerateAsync("b", PreviewKind.Hover);
        _time.Advance(TimeSpan.FromMinutes(1));
        service.Request(["a"], PreviewKind.Hover); // a is looked at again: b is now the oldest
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.GenerateAsync("c", PreviewKind.Hover);

        Assert.Equal(PreviewStatus.Ready, service.Get("a", PreviewKind.Hover).Status);
        Assert.Equal(PreviewStatus.Missing, service.Get("b", PreviewKind.Hover).Status);
        Assert.Equal(PreviewStatus.Ready, service.Get("c", PreviewKind.Hover).Status);
        Assert.Equal("b", Assert.Single(evicted).Key);
        Assert.False(File.Exists(Path.Combine(PreviewsDir, MediaPreviewService.HashFor("b") + ".mp4")));
        Assert.Equal(2000, service.Stats().Bytes);
    }

    [Fact]
    public async Task Background_fill_stops_short_of_the_cap()
    {
        var service = Create(cap: 2000, inputFile: _ => "in.mov");
        service.SetCatalog(new[] { "a", "b", "c" }.Select(k => Clip(k)));
        await service.GenerateAsync("a", PreviewKind.Hover);
        await service.GenerateAsync("b", PreviewKind.Hover); // 2000 bytes: at the cap

        service.BackgroundFill = true;
        Assert.Null(service.TryTakeNext());
        // On-demand work still goes through.
        service.Request(["c"], PreviewKind.Still);
        Assert.Equal(("c", PreviewKind.Still), Take(service));
    }

    [Fact]
    public async Task Index_round_trips_and_reconciles_with_the_folder()
    {
        var first = Create(inputFile: _ => "in.mov");
        first.SetCatalog(new[] { "a", "b" }.Select(k => Clip(k)));
        var a = await first.GenerateAsync("a", PreviewKind.Hover);
        var b = await first.GenerateAsync("b", PreviewKind.Hover);
        first.Dispose();

        Assert.True(File.Exists(Path.Combine(PreviewsDir, PreviewIndex.FileName)));
        File.Delete(Path.Combine(PreviewsDir, b.FileName!));                // lost behind our back
        File.WriteAllBytes(Path.Combine(PreviewsDir, "deadbeefdeadbeef.mp4"), [1]); // orphan
        File.WriteAllBytes(Path.Combine(PreviewsDir, "tmp", "x.tmp.mp4"), [1]);     // killed mid-run

        var second = Create(inputFile: _ => "in.mov");
        second.SetCatalog(new[] { "a", "b" }.Select(k => Clip(k)));

        var reloaded = second.Get("a", PreviewKind.Hover);
        Assert.Equal(PreviewStatus.Ready, reloaded.Status);
        Assert.Equal(a.Url, reloaded.Url);
        Assert.Equal(PreviewStatus.Missing, second.Get("b", PreviewKind.Hover).Status);
        Assert.False(File.Exists(Path.Combine(PreviewsDir, "deadbeefdeadbeef.mp4")));
        Assert.Empty(Directory.GetFiles(Path.Combine(PreviewsDir, "tmp")));
    }

    [Fact]
    public async Task Regenerating_changes_the_version_for_cache_busting()
    {
        var service = Create(inputFile: _ => "in.mov");
        service.SetCatalog([Clip("a")]);
        var v1 = await service.GenerateAsync("a", PreviewKind.Hover);
        _time.Advance(TimeSpan.FromSeconds(5));
        var v2 = await service.GenerateAsync("a", PreviewKind.Hover, force: true);

        Assert.Equal(v1.FileName, v2.FileName);
        Assert.NotEqual(v1.Url, v2.Url);
    }

    // ---- cancellation --------------------------------------------------------------------

    [Fact]
    public async Task Cancel_kills_the_running_ffmpeg_and_records_nothing()
    {
        var service = Create(inputFile: _ => "in.mov", workers: 1);
        service.SetCatalog([Clip("a")]);
        _ffmpeg.BlockUntilCancelled = true;
        var updates = 0;
        service.PreviewUpdated += (_, _) => Interlocked.Increment(ref updates);

        service.Start(backgroundFill: false);
        service.Request(["a"], PreviewKind.Hover);
        await _ffmpeg.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PreviewStatus.Pending, service.Get("a", PreviewKind.Hover).Status);

        service.Cancel(["a"]);
        await _ffmpeg.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => service.Get("a", PreviewKind.Hover).Status == PreviewStatus.Missing);

        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task Dispose_kills_running_work()
    {
        var service = Create(inputFile: _ => "in.mov", workers: 1);
        service.SetCatalog([Clip("a")]);
        _ffmpeg.BlockUntilCancelled = true;

        service.Start();
        await _ffmpeg.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var sw = Stopwatch.StartNew();
        service.Dispose();
        Assert.True(_ffmpeg.Cancelled.Task.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static (string, PreviewKind) Take(MediaPreviewService service)
    {
        var next = service.TryTakeNext();
        Assert.NotNull(next);
        return (next.Value.Item.Key, next.Value.Item.Kind);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }
}

/// <summary>The real <see cref="ProcessRunner"/>: kills the whole tree on cancel and on timeout. Uses cmd + ping, not ffmpeg.</summary>
public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Cancellation_kills_the_process_tree()
    {
        var runner = new ProcessRunner();
        var pid = new TaskCompletionSource<int>();
        runner.Started += p => pid.TrySetResult(p);
        using var cts = new CancellationTokenSource();

        // cmd starts ping as a child: killing only cmd would orphan it.
        var run = runner.RunAsync(Cmd, ["/c", "ping", "-n", "30", "127.0.0.1"], TimeSpan.FromMinutes(1), cts.Token);
        var id = await pid.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var children = await ChildrenOf(id);
        Assert.NotEmpty(children);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(IsRunning(id));
        Assert.All(children, child => Assert.False(IsRunning(child)));
    }

    [Fact]
    public async Task Timeout_kills_and_reports()
    {
        var runner = new ProcessRunner();
        var sw = Stopwatch.StartNew();

        var result = await runner.RunAsync(Cmd, ["/c", "ping", "-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Exit_code_and_stderr_are_captured()
    {
        var result = await new ProcessRunner().RunAsync(Cmd, ["/c", "echo oops 1>&2 & exit 3"], TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.StdErr);
        Assert.False(result.Succeeded);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>ping's process id, by parent — polled briefly because cmd starts it asynchronously.</summary>
    private static async Task<List<int>> ChildrenOf(int parent)
    {
        for (var i = 0; i < 50; i++)
        {
            var found = Process.GetProcessesByName("ping")
                               .Where(p => ParentOf(p.Id) == parent)
                               .Select(p => p.Id)
                               .ToList();
            if (found.Count > 0)
                return found;
            await Task.Delay(50);
        }
        return [];
    }

    private static int? ParentOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var info = new NativeProcessInfo();
            return NtQueryInformationProcess(p.Handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeProcessInfo
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref NativeProcessInfo info, int size, out int returned);
}
