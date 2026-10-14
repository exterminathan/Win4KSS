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

    /// <summary>
    /// The clip browser's WebView2 user data folder (cookies, cache, GPU shader cache). Kept
    /// under our root rather than next to the exe, which may be read-only (System32).
    /// </summary>
    public static string WebView2Directory => Path.Combine(Root, "webview2");

    /// <summary>Last-resort place MpvLoader looks for libmpv-2.dll (a leftover of the old self-extracting publish).</summary>
    public static string RuntimeDirectory => Path.Combine(Root, "runtime");

    /// <summary>Overrides <see cref="CompanionDirectory"/>. A test seam; not used in normal runs.</summary>
    public const string CompanionOverrideVariable = "WIN4K_COMPANION_DIR";

    /// <summary>
    /// The full user-scope install, %LOCALAPPDATA%\Programs\Win4KSS: Win4KSS.exe (what shortcuts
    /// target) beside every DLL the app needs. The one-file .scr in System32 loads its native
    /// DLLs from here; see <see cref="NativeDependencies"/>.
    /// </summary>
    public static string CompanionDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(CompanionOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Win4KSS");
        }
    }

    public static string LogFile => Path.Combine(Root, "win4k.log");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
