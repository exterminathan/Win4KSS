using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Win4K.Core.Media;

/// <summary>
/// A plain-HTTP server on 127.0.0.1 that fetches what ffmpeg asks for through our own
/// <see cref="HttpClient"/>, Range requests included. ffmpeg never opens a connection to the
/// internet itself, for two reasons:
/// <list type="bullet">
/// <item>The bundled ffmpeg uses Schannel, which trusts only the Windows root store — and Apple
/// Root CA is not in it, so it cannot reach sylvan.apple.com. Our client pins Apple's root
/// (<see cref="AppleTrust"/>).</item>
/// <item>A per-application firewall that allowed Win4KSS.exe has never been asked about
/// ffmpeg.exe. All traffic stays on the one executable the user already let through.</item>
/// </list>
/// Only URLs registered with <see cref="UrlFor"/> are served, under unguessable tokens; nothing
/// listens beyond loopback.
/// </summary>
public sealed class LoopbackRelay : IDisposable
{
    private const int MaxRequestHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, string> _urls = new();
    private readonly Task _acceptLoop;

    public LoopbackRelay(HttpClient? http = null)
    {
        _http = http ?? Http.Client;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>A loopback URL that serves <paramref name="remoteUrl"/>. Stable per URL.</summary>
    public string UrlFor(string remoteUrl)
    {
        var token = _urls.FirstOrDefault(p => p.Value == remoteUrl).Key;
        if (token is null)
        {
            token = Guid.NewGuid().ToString("N");
            _urls[token] = remoteUrl;
        }

        // The file name rides along only so ffmpeg's logs and format guessing see a real one.
        var name = Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "";
        return $"http://127.0.0.1:{Port}/{token}/{Uri.EscapeDataString(name)}";
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Relay: accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                    return;

                await RelayAsync(stream, request.Value.Method, request.Value.Target, request.Value.Range).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // ffmpeg hung up (it does, on every seek) or we are shutting down.
            }
            catch (Exception ex)
            {
                Log.Warn($"Relay: request failed: {ex.Message}");
            }
        }
    }

    private readonly record struct Request(string Method, string Target, string? Range);

    private async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(HeaderTimeout);

        var buffer = new byte[MaxRequestHeaderBytes];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
                return null;

            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0)
                return null;
            length += read;

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;

            var lines = text[..end].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
                return null;

            string? range = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                    range = line[(colon + 1)..].Trim();
            }

            return new Request(parts[0].ToUpperInvariant(), parts[1], range);
        }
    }

    private async Task RelayAsync(NetworkStream client, string method, string target, string? range)
    {
        var token = target.TrimStart('/').Split('/', 2)[0];
        if ((method != "GET" && method != "HEAD") || !_urls.TryGetValue(token, out var url))
        {
            await WriteHeadAsync(client, 404, "Not Found", [("Content-Length", "0")]).ConfigureAwait(false);
            return;
        }

        using var upstream = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, url);
        if (range is not null && RangeHeaderValue.TryParse(range, out var parsed))
            upstream.Headers.Range = parsed;

        using var response = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);

        var headers = new List<(string, string)> { ("Accept-Ranges", "bytes") };
        var content = response.Content.Headers;
        if (content.ContentType is { } type)
            headers.Add(("Content-Type", type.ToString()));
        if (content.ContentLength is { } contentLength)
            headers.Add(("Content-Length", contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (content.ContentRange is { } contentRange)
            headers.Add(("Content-Range", contentRange.ToString()));

        await WriteHeadAsync(client, (int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString(), headers)
            .ConfigureAwait(false);

        if (method == "GET")
        {
            await using var body = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
            await body.CopyToAsync(client, _stop.Token).ConfigureAwait(false);
        }
    }

    private async Task WriteHeadAsync(NetworkStream client, int status, string reason, IEnumerable<(string Name, string Value)> headers)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        foreach (var (name, value) in headers)
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await client.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}
