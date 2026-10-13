namespace Win4K.Core;

/// <summary>
/// Every on-disk location the app uses. Centralised so tests can redirect the whole tree
/// with one environment variable instead of touching the real user profile.
/// </summary>
public static class AppPaths
{
    /// <summary>Overrides the app data root. Set by tests; not used in normal runs.</summary>
    public const string HomeOverrideVariable = "WIN4K_HOME";

    private static readonly AsyncLocal<string?> ScopedRoot = new();

    /// <summary>
    /// Redirects <see cref="Root"/> for the current async flow only, until disposed.
    ///
    /// Tests use this instead of setting <see cref="HomeOverrideVariable"/>: the environment
    /// is process-wide, so one test class flipping it (and deleting the folder on dispose)
    /// pulled the rug from under every test running in parallel — CaBundleTests failed
    /// intermittently that way. An AsyncLocal scope is invisible to other tests.
    /// </summary>
    public static IDisposable UseRoot(string path)
    {
        var prior = ScopedRoot.Value;
        ScopedRoot.Value = path;
        return new Restore(() => ScopedRoot.Value = prior);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static string Root
    {
        get
        {
            var scoped = ScopedRoot.Value;
            if (!string.IsNullOrWhiteSpace(scoped))
                return scoped;

            var overridden = Environment.GetEnvironmentVariable(HomeOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Win4KScreensavers");
        }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Cached copy of Apple's manifest, so we work offline and survive Apple 404ing.</summary>
    public static string ManifestFile => Path.Combine(Root, "manifest.json");

    /// <summary>
    /// Catalogues refreshed in-app by <see cref="Catalogs.CatalogRefresher"/>. A file here
    /// overrides the copy embedded from assets/registry.
    /// </summary>
    public static string CatalogsDirectory => Path.Combine(Root, "catalogs");

    public static string DefaultCacheDirectory => Path.Combine(Root, "cache");

    /// <summary>
    /// Clip-browser previews (<see cref="Media.MediaPreviewService"/>): &lt;hash&gt;.jpg stills,
    /// &lt;hash&gt;.mp4 hover clips and index.json. LRU-capped on its own, apart from the video cache.
    /// </summary>
    public static string PreviewsDirectory => Path.Combine(Root, "previews");

    /// <summary>Where libmpv-2.dll is extracted to when running from a single-file .scr.</summary>
    public static string RuntimeDirectory => Path.Combine(Root, "runtime");

    public static string LogFile => Path.Combine(Root, "win4k.log");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
