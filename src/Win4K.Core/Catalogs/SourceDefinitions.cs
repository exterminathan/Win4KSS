using System.Text.Json;

namespace Win4K.Core.Catalogs;

/// <summary>One archive.org search, e.g. the "scenic" group: query → items → files.</summary>
public sealed record ArchiveGroup(string Name, string Scene, string Query);

/// <summary>One aerial-manifests feed: a manifest in Apple's schema plus an optional strings file.</summary>
public sealed record FeedDefinition(string Id, string Url, string? StringsUrl);

/// <summary>
/// A source as described by assets/registry/sources.json. Only the fields the harvesters
/// need; the "verified" block is documentation for humans and is not read.
/// </summary>
public sealed record SourceDefinition(
    string Id,
    string DisplayName,
    string Harvest,
    IReadOnlyList<ArchiveGroup> Groups,
    IReadOnlyList<string> Queries,
    IReadOnlyList<FeedDefinition> Feeds)
{
    public bool IsAuto => Harvest.Equals("auto", StringComparison.OrdinalIgnoreCase);
}

public static class SourceDefinitions
{
    /// <summary>The registry compiled into Win4K.Core.</summary>
    public static IReadOnlyList<SourceDefinition> LoadEmbedded()
    {
        var json = CatalogStore.ReadEmbedded("sources.json")
                   ?? throw new InvalidOperationException("Embedded sources.json is missing from Win4K.Core.");
        return Parse(json);
    }

    public static IReadOnlyList<SourceDefinition> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<SourceDefinition>();
        if (!document.RootElement.TryGetProperty("sources", out var sources))
            return result;

        foreach (var entry in sources.EnumerateArray())
        {
            var id = Str(entry, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var groups = new List<ArchiveGroup>();
            if (entry.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in g.EnumerateArray())
                {
                    var query = Str(group, "query");
                    if (!string.IsNullOrWhiteSpace(query))
                        groups.Add(new ArchiveGroup(Str(group, "name") ?? query, Str(group, "scene") ?? "mixed", query));
                }
            }

            var queries = new List<string>();
            if (entry.TryGetProperty("queries", out var q) && q.ValueKind == JsonValueKind.Array)
                queries.AddRange(q.EnumerateArray().Select(x => x.GetString()).OfType<string>());

            var feeds = new List<FeedDefinition>();
            if (entry.TryGetProperty("feeds", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var feed in f.EnumerateArray())
                {
                    var feedId = Str(feed, "id");
                    var url = Str(feed, "url");
                    if (!string.IsNullOrWhiteSpace(feedId) && !string.IsNullOrWhiteSpace(url))
                        feeds.Add(new FeedDefinition(feedId, url, Str(feed, "strings")));
                }
            }

            result.Add(new SourceDefinition(
                id,
                Str(entry, "displayName") ?? id,
                Str(entry, "harvest") ?? "",
                groups, queries, feeds));
        }
        return result;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
