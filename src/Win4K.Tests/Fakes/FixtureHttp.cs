using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Win4K.Core.Catalogs;

namespace Win4K.Tests.Fakes;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a URL → response table, so the
/// harvesters run their real request logic against small fixture files and never touch the
/// network. Unknown URLs get a 404, which is also how a test asserts "never requested".
/// </summary>
internal sealed class FixtureHttp : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<(HttpMethod Method, string Url)> Requests { get; } = new();

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    /// <summary>Serves a fixture file's contents as JSON.</summary>
    public FixtureHttp Json(string url, string fixtureName) =>
        Text(url, Fixture(fixtureName));

    public FixtureHttp Text(string url, string body)
    {
        _routes[Normalize(url)] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return this;
    }

    public FixtureHttp Status(string url, HttpStatusCode status)
    {
        _routes[Normalize(url)] = () => new HttpResponseMessage(status);
        return this;
    }

    /// <summary>Answers HEAD (and GET) with a Content-Length and no body worth reading.</summary>
    public FixtureHttp Length(string url, long length)
    {
        _routes[Normalize(url)] = () =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        return this;
    }

    public FixtureHttp Respond(string url, Func<HttpResponseMessage> respond)
    {
        _routes[Normalize(url)] = respond;
        return this;
    }

    public int CountOf(string url) => Requests.Count(r => r.Url == Normalize(url));

    public HttpClient Client() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A HarvestHttp over this handler whose retry back-off does not actually wait.</summary>
    public HarvestHttp Harvest() => new(Client(), TimeSpan.FromSeconds(10), (_, _) => Task.CompletedTask);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue((request.Method, url));
        return Task.FromResult(_routes.TryGetValue(url, out var respond)
            ? respond()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Routes are matched on the escaped absolute URI, which is what the handler sees.</summary>
    private static string Normalize(string url) => new Uri(url).AbsoluteUri;
}
