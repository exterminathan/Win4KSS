using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Win4K.Core;

namespace Win4K.Tests.Fakes;

/// <summary>
/// A video origin in memory: serves registered byte arrays, honours Range requests (206 with
/// Content-Range), records every request, and can hold requests at a gate so a test decides
/// when each download may proceed. Unknown URLs get a 404.
/// </summary>
internal sealed class FakeOrigin : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cutOnce = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(0);
    private int _inFlight;
    private int _maxInFlight;

    public ConcurrentQueue<(string Url, long? RangeFrom)> Requests { get; } = new();

    /// <summary>When true, every request waits at the gate until <see cref="Release"/>.</summary>
    public bool Gated { get; set; }

    /// <summary>Requests currently waiting at the gate (or otherwise in SendAsync).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public static byte[] Bytes(int length, int seed) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();

    public Clip Add(string key, string url, int length, int seed = 1, long? declaredSize = null)
    {
        var uri = new Uri(url);
        _files[uri.AbsoluteUri] = Bytes(length, seed);
        return new Clip(key, key, "Test", TimeOfDay.Unknown, uri, IsLocal: false, declaredSize);
    }

    public void Serve(string url, byte[] body) => _files[new Uri(url).AbsoluteUri] = body;

    public byte[] Body(Clip clip) => _files[clip.Url.AbsoluteUri];

    /// <summary>The next full (non-Range) response for this URL drops the connection after <paramref name="cut"/> bytes.</summary>
    public void CutOnce(Clip clip, int cut) => _cutOnce[clip.Url.AbsoluteUri] = cut;

    public void Release(int count = 1) => _gate.Release(count);

    public IReadOnlyList<string> Urls => Requests.Select(r => r.Url).ToList();

    public HttpClient Client() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        Requests.Enqueue((url, from));

        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight)) &&
               Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }

        try
        {
            if (Gated)
                await _gate.WaitAsync(ct);

            if (!_files.TryGetValue(url, out var data))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };

            var start = (int)(from ?? 0);
            var body = data[start..];

            HttpContent content = from is null && _cutOnce.TryRemove(url, out var cut)
                ? new StreamContent(new FailingStream(body, cut))
                : new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            content.Headers.ContentLength = body.Length;

            if (from is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

            content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
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

internal static class Eventually
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="timeoutMs"/>.</summary>
    public static async Task True(Func<bool> condition, string because, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out after {timeoutMs} ms waiting for: {because}");
            await Task.Delay(20);
        }
    }
}
