using System.Reflection;
using System.Runtime.InteropServices;

namespace Win4K.Core.Playback;

/// <summary>
/// Finds libmpv-2.dll at runtime.
///
/// Normally the default probing rules are enough — the DLL sits beside the executable. The
/// awkward case is the installed screen saver: Windows only lists .scr files found in
/// System32, so the shipped artefact is a single-file publish and the native library is
/// extracted by the host to a temp directory. That usually resolves itself, but when it does
/// not (a locked-down temp, an AV quarantining the extraction) the fallbacks below give us
/// somewhere predictable to look, and a clear log line rather than a bare DllNotFoundException.
/// </summary>
public static class MpvLoader
{
    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>Probed in order, after the default resolver has already failed.</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return AppPaths.RuntimeDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "native", "mpv");

        // Running from the source tree (dotnet run, tests): bin/<cfg>/<tfm>/<rid>/ is four
        // levels below the project, and native/mpv sits at the repository root.
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        for (var up = 3; up <= 6; up++)
        {
            var dir = baseDir;
            for (var i = 0; i < up && dir is not null; i++)
                dir = Path.GetDirectoryName(dir);

            if (dir is not null)
                yield return Path.Combine(dir, "native", "mpv");
        }
    }

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            _registered = true;

            NativeLibrary.SetDllImportResolver(typeof(MpvLoader).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(MpvInterop.Library, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // not ours; let the default resolver handle it

        // Try the ordinary rules first so a DLL next to the exe still wins.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        foreach (var dir in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var path = Path.Combine(dir, "libmpv-2.dll");
            if (!File.Exists(path))
                continue;

            if (NativeLibrary.TryLoad(path, out var found))
            {
                Log.Info($"mpv: loaded {path}");
                return found;
            }

            Log.Warn($"mpv: found but failed to load {path}");
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Registers the resolver and confirms the library actually loads, turning the failure
    /// into something explainable instead of a DllNotFoundException from a random call site.
    /// </summary>
    public static void EnsureLoadable()
    {
        Register();

        try
        {
            _ = MpvInterop.mpv_client_api_version();
        }
        catch (DllNotFoundException ex)
        {
            var searched = string.Join(Environment.NewLine + "  ", CandidateDirectories().Distinct());
            throw new InvalidOperationException(
                "libmpv-2.dll could not be loaded. Run scripts/fetch-libmpv.ps1 to download it." +
                Environment.NewLine + "Searched:" + Environment.NewLine + "  " + searched, ex);
        }
    }
}
