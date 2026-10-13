using System.Net;
using System.Text.Json;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

/// <summary>Progress from a catalogue refresh: which source, what it is doing, how far.</summary>
public sealed record CatalogRefreshProgress(string SourceId, string Stage, int Done, int Total);

/// <summary>
/// What one harvester produced.
/// </summary>
/// <param name="Assets">Catalogue rows in Apple's schema with v2 extension fields filled.</param>
/// <param name="Categories">Category tree to publish alongside (tvOS 26's), else empty.</param>
/// <param name="FailedUnits">
/// Items (archive.org identifier, NASA id, or feed id) that could not be fetched even after
/// retries. The refresher carries their rows over from the previous catalogue rather than
/// letting one flaky request delete clips the user may have in rotation.
/// </param>
/// <param name="Notes">Human-readable remarks for the refresh report (e.g. "ffprobe not found").</param>
public sealed record HarvestResult(
    List<AppleAsset> Assets,
    List<AppleCategory> Categories,
    IReadOnlySet<string> FailedUnits,
    IReadOnlyList<string> Notes)
{
    /// <summary>The unit an asset belongs to, for matching against <see cref="FailedUnits"/>.</summary>
    public static string? UnitOf(AppleAsset asset) =>
        !string.IsNullOrWhiteSpace(asset.Feed) ? asset.Feed : NullIfEmpty(asset.ShotId);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>One per auto-harvested source in sources.json.</summary>
public interface ICatalogHarvester
{
    /// <summary>The sources.json id this harvester refreshes.</summary>
    string SourceId { get; }

    Task<HarvestResult> HarvestAsync(SourceDefinition source, IProgress<CatalogRefreshProgress>? progress, CancellationToken ct);
}

/// <summary>
/// GET-with-retry for the harvesters. Every request goes through the shared
/// <see cref="Http.Client"/> by default, so AppleTrust applies to sylvan.apple.com links too.
/// Retries 5xx, 429, timeouts and connection failures (1 s, then 4 s); a 4xx is final.
/// </summary>
public sealed class HarvestHttp(HttpClient? http = null, TimeSpan? timeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int Attempts = 3;

    private readonly HttpClient _http = http ?? Http.Client;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                                                .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                var retryable = status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
                if (!retryable || attempt >= Attempts)
                    throw new HttpRequestException($"GET {url} -> {status}", null, response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Attempts && ex is HttpRequestException { StatusCode: null } or OperationCanceledException or IOException)
            {
                // Connection reset, DNS blip, or our own per-request timeout: try again.
            }

            await _delay(Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], ct).ConfigureAwait(false);
        }
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) =>
        JsonDocument.Parse(await GetStringAsync(url, ct).ConfigureAwait(false));

    /// <summary>Content-Length from a HEAD, or null if the server will not say.</summary>
    public async Task<long?> GetLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                                            .ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Small JSON helpers shared by the harvesters: archive.org is loose about types.</summary>
internal static class Json
{
    /// <summary>A string, or the first string of an array (archive.org returns either for title/creator).</summary>
    public static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => NullIfBlank(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => value.EnumerateArray()
                                        .Where(v => v.ValueKind == JsonValueKind.String)
                                        .Select(v => NullIfBlank(v.GetString()))
                                        .FirstOrDefault(v => v is not null),
            _ => null,
        };
    }

    /// <summary>Every string under a property, whether it is a string or an array of them.</summary>
    public static List<string> Strings(JsonElement element, string name)
    {
        var result = new List<string>();
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return result;

        if (value.ValueKind == JsonValueKind.String)
            result.Add(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array)
            result.AddRange(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));

        return result.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
            return (long)d;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return (long)parsed;
        return null;
    }

    public static double? Double(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
            return d;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
