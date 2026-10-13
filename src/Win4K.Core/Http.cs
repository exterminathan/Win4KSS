using System.Net;

namespace Win4K.Core;

/// <summary>
/// One shared HttpClient for the whole process.
///
/// cdima/aerial created a WebClient per download inside a `using`, then called
/// DownloadFileAsync on it — disposing the client while the transfer was still running.
/// A single long-lived HttpClient avoids both that and socket exhaustion.
/// </summary>
public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            // sylvan.apple.com chains to Apple Root CA, which Windows does not trust.
            // See AppleTrust for why we pin it here rather than downgrading to http://
            // or touching the machine's certificate store.
            SslOptions = { RemoteCertificateValidationCallback = AppleTrust.Validate },
            AutomaticDecompression = DecompressionMethods.All,
            // Video files are large; the default 100s applies to the whole response including
            // body, so per-request timeouts are set explicitly where they matter instead.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Apple's CDN is content-agnostic, but GitHub's release API (used by the About tab)
        // returns 403 without a User-Agent, so set one globally.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Win4KScreensavers/1.0");

        return client;
    }
}
