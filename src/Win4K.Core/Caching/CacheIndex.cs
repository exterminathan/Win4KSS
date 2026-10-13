using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win4K.Core.Caching;

public sealed class CacheEntry
{
    /// <summary>File name under videos\ — <see cref="VideoCache.KeyFor"/> of the URL, so one per rendition.</summary>
    public string Key { get; set; } = "";

    /// <summary>The origin URL, always escaped (<see cref="Uri.AbsoluteUri"/>).</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// The clip this file is a rendition of (<see cref="Clip.Id"/>), which is what pins are
    /// keyed by. Null for files cached before v2 and for files adopted from disk; it is filled
    /// in the next time the clip is resolved (<see cref="VideoCache.Resolve"/>).
    /// </summary>
    public string? ClipKey { get; set; }

    public string Title { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CachedUtc { get; set; }

    /// <summary>Drives LRU eviction: the least recently played clip is dropped first.</summary>
    public DateTime LastPlayedUtc { get; set; }

    public CacheEntry Clone() => (CacheEntry)MemberwiseClone();
}

/// <summary>
/// Metadata for the cached videos, kept beside them.
///
/// The files on disk remain the source of truth — the index is a convenience, and anything
/// it claims that the filesystem contradicts is discarded on load. That means deleting files
/// out from under us by hand is a supported thing to do.
///
/// Format history:
///  v1  key, url, title, sizeBytes, cachedUtc, lastPlayedUtc.
///  v2  adds clipKey. Cache keys stay URL-based (one file per rendition). v1 files load
///      unchanged with no clip keys, and any URL stored unescaped is re-escaped on load.
/// </summary>
public sealed class CacheIndex
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string FileName = "cache-index.json";

    public static string PathFor(string cacheDirectory) =>
        System.IO.Path.Combine(cacheDirectory, FileName);

    public static CacheIndex Load(string cacheDirectory) => LoadWithText(cacheDirectory).Index;

    /// <summary>The index and the exact text it was parsed from ("" when there was no file).</summary>
    internal static (CacheIndex Index, string Text) LoadWithText(string cacheDirectory)
    {
        var path = PathFor(cacheDirectory);
        string text;
        try
        {
            if (SharedFile.Read(path) is not { } read)
                return (new CacheIndex(), "");
            text = read;
        }
        catch (Exception ex)
        {
            Log.Error("Cache: index read failed", ex);
            return (new CacheIndex(), "");
        }

        return (Parse(text), text);
    }

    internal static CacheIndex Parse(string text)
    {
        try
        {
            var index = JsonSerializer.Deserialize<CacheIndex>(text, Options) ?? new CacheIndex();
            index.Normalise();
            return index;
        }
        catch (Exception ex)
        {
            // A corrupt index costs us LRU ordering, not the cached files themselves.
            Log.Error("Cache: index load failed, rebuilding", ex);
            return new CacheIndex();
        }
    }

    /// <summary>Brings a loaded index (any version) to the current shape.</summary>
    private void Normalise()
    {
        // The deserialiser builds its own dictionary with the default (case-sensitive) comparer.
        var entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in Entries ?? [])
        {
            if (entry is null)
                continue;

            if (string.IsNullOrWhiteSpace(entry.Key))
                entry.Key = name;

            // Before the escaping fix some writers stored Uri.ToString(), which unescapes.
            if (!string.IsNullOrWhiteSpace(entry.Url) && Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
                entry.Url = uri.AbsoluteUri;

            if (string.IsNullOrWhiteSpace(entry.ClipKey))
                entry.ClipKey = null;

            entries[entry.Key] = entry;
        }

        Entries = entries;
        Version = CurrentVersion;
    }

    public void Save(string cacheDirectory) => SaveText(cacheDirectory);

    /// <summary>Writes temp-then-move and returns the text written, or null if the write failed.</summary>
    internal string? SaveText(string cacheDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);

            Version = CurrentVersion;
            var text = JsonSerializer.Serialize(this, Options);
            SharedFile.Write(PathFor(cacheDirectory), text);
            return text;
        }
        catch (Exception ex)
        {
            Log.Error("Cache: index save failed", ex);
            return null;
        }
    }

    public CacheIndex Clone() => new()
    {
        Version = Version,
        Entries = Entries.ToDictionary(e => e.Key, e => e.Value.Clone(), StringComparer.OrdinalIgnoreCase),
    };
}
