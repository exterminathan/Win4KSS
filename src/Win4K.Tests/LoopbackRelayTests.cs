using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Win4K.Core;
using Win4K.Core.Media;

namespace Win4K.Tests;

/// <summary>
/// <see cref="LoopbackRelay"/>: ffmpeg reads previews through it, so it must honour Range (every
/// seek is one) and serve only registered URLs.
/// </summary>
public class LoopbackRelayTests
{
    private const string Remote = "https://example.test/videos/clip one.mov";
    private static readonly byte[] Payload = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>A tiny origin that honours single byte ranges, like the CDNs do.</summary>
    private sealed class RangeOrigin : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ranges.Add(request.Headers.Range);
            if (request.RequestUri!.AbsoluteUri != new Uri(Remote).AbsoluteUri)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (request.Headers.Range?.Ranges.SingleOrDefault() is { } r)
            {
                var from = r.From ?? 0;
                var to = Math.Min(r.To ?? Payload.Length - 1, Payload.Length - 1);
                var slice = Payload[(int)from..((int)to + 1)];
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Payload.Length);
                partial.Content.Headers.ContentType = new MediaTypeHeaderValue("video/quicktime");
                return Task.FromResult(partial);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            full.Content.Headers.ContentType = new MediaTypeHeaderValue("video/quicktime");
            return Task.FromResult(full);
        }
    }

    private static HttpClient Plain() => new(new SocketsHttpHandler { UseProxy = false });

    [Fact]
    public async Task Serves_a_registered_url_whole()
    {
        using var relay = new LoopbackRelay(new HttpClient(new RangeOrigin()));
        var url = relay.UrlFor(Remote);
        Assert.StartsWith($"http://127.0.0.1:{relay.Port}/", url);

        using var client = Plain();
        var bytes = await client.GetByteArrayAsync(url);
        Assert.Equal(Payload, bytes);
    }

    [Fact]
    public async Task Passes_a_range_through_and_answers_206_with_content_range()
    {
        var origin = new RangeOrigin();
        using var relay = new LoopbackRelay(new HttpClient(origin));
        using var client = Plain();

        using var request = new HttpRequestMessage(HttpMethod.Get, relay.UrlFor(Remote));
        request.Headers.Range = new RangeHeaderValue(990, null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(990, response.Content.Headers.ContentRange?.From);
        Assert.Equal(1000, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(Payload[990..], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(990, origin.Ranges.Single()?.Ranges.Single().From);
    }

    [Fact]
    public async Task Refuses_anything_not_registered()
    {
        using var relay = new LoopbackRelay(new HttpClient(new RangeOrigin()));
        relay.UrlFor(Remote);
        using var client = Plain();

        using var response = await client.GetAsync($"http://127.0.0.1:{relay.Port}/{Guid.NewGuid():N}/x.mov");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_same_remote_url_gets_the_same_loopback_url()
    {
        using var relay = new LoopbackRelay(new HttpClient(new RangeOrigin()));
        Assert.Equal(relay.UrlFor(Remote), relay.UrlFor(Remote));
        Assert.NotEqual(relay.UrlFor(Remote), relay.UrlFor(Remote + "?v=2"));
    }

    /// <summary>
    /// The real thing: the bundled Schannel ffmpeg cannot verify sylvan.apple.com on its own, so
    /// a frame from Apple's CDN proves the relay (and AppleTrust behind it) is what makes previews
    /// work. Live only: $env:WIN4K_LIVE_TESTS="1"; needs scripts/fetch-ffmpeg.ps1 to have run.
    /// </summary>
    [Fact]
    public async Task Live_bundled_ffmpeg_takes_a_frame_from_apples_cdn_through_the_relay()
    {
        if (Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") != "1")
            return;

        var ffmpeg = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "ffmpeg", "ffmpeg.exe"));
        Assert.True(File.Exists(ffmpeg), $"run scripts/fetch-ffmpeg.ps1 first ({ffmpeg})");

        using var relay = new LoopbackRelay(Http.Client);
        var output = Path.Combine(Path.GetTempPath(), $"win4k-relay-{Guid.NewGuid():N}.jpg");
        var input = relay.UrlFor("https://sylvan.apple.com/Aerials/2x/Videos/BO_A018_C029_SDR_20190812_SDR_4K_HEVC.mov");

        var hover = Path.ChangeExtension(output, ".mp4");
        try
        {
            await RunAsync(ffmpeg, PreviewCommands.StillFromVideo(input, output, 8, caFile: null));
            Assert.True(new FileInfo(output).Length > 1000, "no frame written");

            await RunAsync(ffmpeg, PreviewCommands.Hover(input, hover, 8, caFile: null));
            Assert.True(new FileInfo(hover).Length > 10_000, "no hover clip written");
        }
        finally
        {
            File.Delete(output);
            File.Delete(hover);
        }
    }

    private static async Task RunAsync(string exe, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(process.ExitCode == 0, stderr);
    }
}
