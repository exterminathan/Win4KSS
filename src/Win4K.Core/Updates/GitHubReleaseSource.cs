using System.Globalization;
using System.Text.Json;

namespace Win4K.Core.Updates;

/// <summary>Where <see cref="UpdateChecker"/> gets release information from. A fake in tests; <see cref="GitHubReleaseSource"/> in real runs.</summary>
public interface IUpdateSource
{
    /// <summary>Null on any failure (offline, non-success status, unparsable body) — never throws.</summary>
    Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET /repos/exterminathan/Win4KSS/releases/latest</c> — unauthenticated (no token is ever
/// embedded; the public API allows 60 requests/hour/IP, which <see cref="UpdateChecker"/>'s 24h
/// throttle stays well under). This endpoint already excludes drafts and prereleases by GitHub's
/// own semantics, but <see cref="UpdateChecker.Evaluate"/> checks the returned object's own
/// <c>draft</c>/<c>prerelease</c> fields too rather than trusting that alone.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient? http = null, TimeSpan? timeout = null) : IUpdateSource
{
    public const string ApiUrl = "https://api.github.com/repos/exterminathan/Win4KSS/releases/latest";

    private readonly HttpClient _http = http ?? Http.Client;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            // Http.Client already sets a User-Agent for every request (GitHub's API 403s
            // without one), but this is set here too in case a caller (a test, or future code)
            // supplies a different HttpClient that does not.
            request.Headers.UserAgent.ParseAdd("Win4KSS-UpdateChecker");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Updates: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var release = Parse(document.RootElement);
            if (release is null)
                Log.Warn("Updates: GitHub API response could not be parsed as a release");
            return release;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Updates: GitHub API request timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw; // the caller's own cancellation (shutdown); let it propagate
        }
        catch (Exception ex)
        {
            Log.Warn($"Updates: GitHub API request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tolerant: unknown fields are ignored, missing optional fields become null/empty. Internal for GitHubReleaseSourceTests.</summary>
    internal static ReleaseInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var tag = Str(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in array.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) ? bytes : 0L;
                assets.Add(new ReleaseAsset(name, size, url));
            }
        }

        DateTime? published = DateTime.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;

        return new ReleaseInfo(tag!, Str(root, "name"), Str(root, "body"), Bool(root, "draft"), Bool(root, "prerelease"), assets, published);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
