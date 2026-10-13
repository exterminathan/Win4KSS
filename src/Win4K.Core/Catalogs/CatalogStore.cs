using System.Reflection;
using System.Text.Json;
using Win4K.Core.Apple;

namespace Win4K.Core.Catalogs;

public enum CatalogOrigin
{
    None = 0,
    /// <summary>%LOCALAPPDATA%\Win4KScreensavers\catalogs\catalog-&lt;id&gt;.json, written by a refresh.</summary>
    Local = 1,
    /// <summary>The copy of assets/registry/catalog-&lt;id&gt;.json compiled into Win4K.Core.</summary>
    Embedded = 2,
}

/// <summary>
/// Where harvested catalogues live. Load order is local (refreshed in-app) → embedded
/// (shipped), the same fall-through <see cref="AppleManifestClient"/> uses for Apple's
/// manifest, so a fresh install and a failed refresh both still have clips.
/// </summary>
public sealed class CatalogStore(string? directory = null)
{
    /// <summary>Catalogue schema the harvesters write. v1 (scripts/harvest.py) still loads.</summary>
    public const int CurrentSchemaVersion = 2;

    public string Directory => directory ?? AppPaths.CatalogsDirectory;

    public string PathFor(string catalogId) => Path.Combine(Directory, $"catalog-{catalogId}.json");

    public (AppleManifest? Manifest, CatalogOrigin Origin) Load(string catalogId)
    {
        var local = LoadLocal(catalogId);
        if (local is { Assets.Count: > 0 })
            return (local, CatalogOrigin.Local);

        var embedded = LoadEmbedded(catalogId);
        return embedded is null ? (null, CatalogOrigin.None) : (embedded, CatalogOrigin.Embedded);
    }

    public AppleManifest? LoadLocal(string catalogId)
    {
        var path = PathFor(catalogId);
        try
        {
            if (!File.Exists(path))
                return null;
            return AppleJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // A corrupt refresh must not cost the user their clips: fall through to embedded.
            Log.Error($"Catalogues: local '{catalogId}' unreadable, using the embedded copy", ex);
            return null;
        }
    }

    public static AppleManifest? LoadEmbedded(string catalogId)
    {
        var json = ReadEmbedded($"catalog-{catalogId}.json");
        return json is null ? null : AppleJson.Deserialize(json);
    }

    /// <summary>Write-then-move, so a crash mid-write never leaves a truncated catalogue that would be trusted.</summary>
    public void Save(string catalogId, AppleManifest manifest)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(catalogId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(manifest));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>When the local catalogue was last refreshed; null if there is none.</summary>
    public DateTime? LastRefreshedUtc(string catalogId)
    {
        var path = PathFor(catalogId);
        if (!File.Exists(path))
            return null;

        return LoadLocal(catalogId)?.GeneratedUtc ?? File.GetLastWriteTimeUtc(path);
    }

    /// <summary>
    /// Same contract as <see cref="AppleJsonContext.Default"/>, plus indentation and readable
    /// non-ASCII ("Cap Méchant", not unicode-escaped). Options handed to a context
    /// constructor become read-only, so they are built fresh here rather than copied.
    /// </summary>
    private static readonly AppleJsonContext IndentedContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Indented, so a catalogue diff in assets/registry stays reviewable.</summary>
    public static string Serialize(AppleManifest manifest) =>
        JsonSerializer.Serialize(manifest, IndentedContext.AppleManifest);

    public static string? ReadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase)
                                                || n.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
