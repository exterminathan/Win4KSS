using System.Net;
using System.Net.Http.Headers;
using Win4K.Core;
using Win4K.Core.Caching;

namespace Win4K.Tests;

/// <summary>
/// Download hardening against a scripted HTTP origin: retries with backoff, Retry-After,
/// timeouts, resume after a dropped connection, and the two ways an error page used to get
/// cached as video. Delays are recorded rather than slept.
/// </summary>
public class VideoCacheDownloadTests : IDisposable
{
    private readonly string _root;
    private readonly List<TimeSpan> _delays = [];

    public VideoCacheDownloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "win4k-dlt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] Video = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7)).ToArray();

    private static readonly Clip Clip =
        new("ia:x", "Test clip", "Test", TimeOfDay.Unknown, new Uri("https://archive.example/LA%2001.mp4"), false);

    private VideoCache Cache(ScriptedHandler handler)
    {
        var cache = new VideoCache(
            new AppSettings { CacheEnabled = true, CacheDirectory = _root, CacheBudgetGb = 1 },
            new HttpClient(handler))
        {
            Delay = (d, _) =>
            {
                _delays.Add(d);
                return Task.CompletedTask;
            },
        };
        return cache;
    }

    private static HttpResponseMessage Ok(byte[]? body = null, string mediaType = "video/mp4")
    {
        var content = new ByteArrayContent(body ?? Video);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("") };

    // ---- retries ------------------------------------------------------------------------------

    [Fact]
    public async Task Server_errors_are_retried_with_backoff_and_then_succeed()
    {
        var handler = new ScriptedHandler(
            _ => Status(HttpStatusCode.BadGateway),
            _ => Status(HttpStatusCode.ServiceUnavailable),
            _ => Ok());
        var cache = Cache(handler);

        Assert.True(await cache.DownloadAsync(Clip));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)], _delays);
        Assert.Equal(Video, await File.ReadAllBytesAsync(cache.PathFor(Clip.Url)));
    }

    [Fact]
    public async Task Persistent_server_errors_give_up_after_the_last_backoff()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.InternalServerError));
        var cache = Cache(handler);

        Assert.False(await cache.DownloadAsync(Clip));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15)], _delays);
        Assert.False(cache.IsCached(Clip.Url));
    }

    [Fact]
    public async Task Retry_after_is_honoured_when_longer_than_the_backoff()
    {
        var handler = new ScriptedHandler(
            _ =>
            {
                var r = Status(HttpStatusCode.ServiceUnavailable);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
                return r;
            },
            _ =>
            {
                var r = Status(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2)); // capped
                return r;
            },
            _ => Ok());
        var cache = Cache(handler);

        Assert.True(await cache.DownloadAsync(Clip));

        Assert.Equal([TimeSpan.FromSeconds(9), cache.MaxRetryAfter], _delays);
    }

    [Fact]
    public async Task A_timeout_waiting_for_headers_is_retried()
    {
        var handler = new ScriptedHandler(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Ok();
            },
            _ => Task.FromResult(Ok()));
        var cache = Cache(handler);
        cache.ResponseTimeout = TimeSpan.FromMilliseconds(100);

        Assert.True(await cache.DownloadAsync(Clip));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
    }

    [Fact]
    public async Task Not_found_is_not_retried()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.NotFound));
        var cache = Cache(handler);

        Assert.False(await cache.DownloadAsync(Clip));

        Assert.Single(handler.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_with_a_range_request()
    {
        const int cut = 20_000;
        var handler = new ScriptedHandler(
            _ =>
            {
                var content = new StreamContent(new FailingStream(Video, cut));
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                content.Headers.ContentLength = Video.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
            request =>
            {
                var from = request.Headers.Range!.Ranges.Single().From!.Value;
                var content = new ByteArrayContent(Video[(int)from..]);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, Video.Length - 1, Video.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            });
        var cache = Cache(handler);

        Assert.True(await cache.DownloadAsync(Clip));

        Assert.Equal(cut, handler.Requests[1].Headers.Range!.Ranges.Single().From);
        Assert.Equal(Video, await File.ReadAllBytesAsync(cache.PathFor(Clip.Url)));
    }

    // ---- never cache an error page --------------------------------------------------------------

    [Fact]
    public async Task An_html_response_is_rejected_and_never_cached()
    {
        var page = "<html><body>503 Service Temporarily Unavailable</body></html>"u8.ToArray();
        var handler = new ScriptedHandler(_ => Ok(page, "text/html"));
        var cache = Cache(handler);

        Assert.False(await cache.DownloadAsync(Clip));

        Assert.Single(handler.Requests);
        Assert.False(cache.IsCached(Clip.Url));
        Assert.Empty(Directory.GetFiles(cache.PartialDirectory));
    }

    [Fact]
    public async Task A_body_shorter_than_its_content_length_is_rejected()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var r = Ok(Video[..1000]);
            r.Content.Headers.ContentLength = Video.Length;
            return r;
        });
        var cache = Cache(handler);

        Assert.False(await cache.DownloadAsync(Clip));

        Assert.False(cache.IsCached(Clip.Url));
        Assert.Empty(Directory.GetFiles(cache.PartialDirectory));
        Assert.Empty(Directory.GetFiles(cache.VideosDirectory));
    }

    [Fact]
    public async Task A_truncated_body_is_retried_and_the_good_copy_kept()
    {
        var handler = new ScriptedHandler(
            _ =>
            {
                var r = Ok(Video[..1000]);
                r.Content.Headers.ContentLength = Video.Length;
                return r;
            },
            _ => Ok());
        var cache = Cache(handler);

        Assert.True(await cache.DownloadAsync(Clip));

        Assert.Equal(Video, await File.ReadAllBytesAsync(cache.PathFor(Clip.Url)));
    }

    [Fact]
    public async Task The_request_goes_out_with_its_escaping_intact()
    {
        var handler = new ScriptedHandler(_ => Ok());
        var cache = Cache(handler);

        await cache.DownloadAsync(Clip);

        Assert.Equal("https://archive.example/LA%2001.mp4", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal(Clip.Url.AbsoluteUri, Assert.Single(cache.Entries()).Url);
    }

    // ---- fakes ----------------------------------------------------------------------------------

    /// <summary>Replies with each scripted response in turn, repeating the last.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _script;

        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) =>
            _script = script.Select(f => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)
                                         ((r, _) => Task.FromResult(f(r)))).ToArray();

        public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] script) =>
            _script = script.Select(f => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)
                                         ((_, ct) => f(ct))).ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var step = _script[Math.Min(Requests.Count - 1, _script.Length - 1)];
            return step(request, ct);
        }
    }

    /// <summary>Serves the first <c>cut</c> bytes, then fails like a reset connection.</summary>
    private sealed class FailingStream(byte[] data, int cut) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cut)
                throw new IOException("connection reset by peer");

            var n = Math.Min(count, cut - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
