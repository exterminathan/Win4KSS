using System.Runtime.InteropServices;

namespace Win4K.Core;

/// <summary>Where this process found its native DLLs.</summary>
public enum NativeLocationKind
{
    /// <summary>libmpv-2.dll sits beside the running exe: the loose install, or a dev build. Nothing to do.</summary>
    BesideExe,

    /// <summary>
    /// Not beside the exe, but the user-scope install (<see cref="AppPaths.CompanionDirectory"/>)
    /// has them: the one-file .scr in System32. The companion dir is added to the native search.
    /// </summary>
    Companion,

    /// <summary>libmpv-2.dll is nowhere to be found. Playback cannot work; the install needs repairing.</summary>
    Missing,
}

/// <param name="Kind">Which case applies.</param>
/// <param name="Directory">The directory the natives load from (for <see cref="NativeLocationKind.Missing"/>, the companion dir that was looked in).</param>
/// <param name="MissingLibraries">For the companion and missing cases, the required DLLs the companion dir lacks.</param>
public sealed record NativeLocation(NativeLocationKind Kind, string Directory, IReadOnlyList<string> MissingLibraries)
{
    /// <summary>libmpv can load, so the saver can play.</summary>
    public bool CanPlay => Kind != NativeLocationKind.Missing;

    /// <summary>WebView2Loader can load too, so the clip browser can open.</summary>
    public bool CanBrowse => Kind == NativeLocationKind.BesideExe ||
                             (Kind == NativeLocationKind.Companion && MissingLibraries.Count == 0);
}

/// <summary>
/// Finds the native DLLs (libmpv-2.dll, WebView2Loader.dll) for a process that may be running
/// from a directory that does not hold them.
///
/// The Screen Saver Settings dropdown only lists .scr files in System32, so that is where one
/// copy of Win4KSS.scr goes — a single-file bundle of the managed assemblies only. The natives
/// are deliberately NOT copied there: names like libmpv-2.dll and WebView2Loader.dll in System32
/// would shadow other applications' own copies through the DLL search path. Instead the System32
/// .scr borrows them from the full user-scope install, which must therefore exist.
///
/// <see cref="Initialize"/> runs first thing in Program.Main, before anything P/Invokes.
/// </summary>
public static partial class NativeDependencies
{
    public const string LibMpv = "libmpv-2.dll";
    public const string WebView2Loader = "WebView2Loader.dll";

    /// <summary>What the companion dir must hold for the System32 .scr to work fully.</summary>
    public static IReadOnlyList<string> RequiredLibraries { get; } = [LibMpv, WebView2Loader];

    /// <summary>The result of <see cref="Initialize"/>; null until it has run.</summary>
    public static NativeLocation? Current { get; private set; }

    /// <summary>
    /// The decision, free of side effects. Natives beside the exe win; otherwise the companion
    /// dir is used if it has libmpv; otherwise they are missing.
    /// </summary>
    public static NativeLocation Locate(string exeDirectory, string companionDirectory, Func<string, bool> fileExists)
    {
        if (fileExists(Path.Combine(exeDirectory, LibMpv)))
            return new NativeLocation(NativeLocationKind.BesideExe, exeDirectory, []);

        var missing = RequiredLibraries.Where(name => !fileExists(Path.Combine(companionDirectory, name))).ToList();

        if (SameDirectory(exeDirectory, companionDirectory) || missing.Contains(LibMpv))
            return new NativeLocation(NativeLocationKind.Missing, companionDirectory, RequiredLibraries.ToList());

        return new NativeLocation(NativeLocationKind.Companion, companionDirectory, missing);
    }

    /// <summary>
    /// Decides where the natives are and, if that is the companion dir, puts it on the process's
    /// DLL search path. Idempotent. Logs the outcome; never throws.
    /// </summary>
    public static NativeLocation Initialize()
    {
        if (Current is { } done)
            return done;

        NativeLocation location;
        try
        {
            location = Locate(AppContext.BaseDirectory, AppPaths.CompanionDirectory, File.Exists);
        }
        catch (Exception ex)
        {
            Log.Error("Natives: could not work out where libmpv-2.dll is", ex);
            location = new NativeLocation(NativeLocationKind.Missing, AppPaths.CompanionDirectory, RequiredLibraries.ToList());
        }

        switch (location.Kind)
        {
            case NativeLocationKind.Companion:
                // Covers every ordinary LoadLibrary("x.dll") in the process, which is what .NET's
                // default DllImport probing falls back to. MpvLoader and the WebView2 loader are
                // also pointed here explicitly, so this is the belt rather than the braces.
                if (!SetDllDirectory(location.Directory))
                    Log.Warn($"Natives: SetDllDirectory({location.Directory}) failed, error {Marshal.GetLastPInvokeError()}");

                Log.Info($"Natives: none beside {AppContext.BaseDirectory}; using the install at {location.Directory}");
                if (location.MissingLibraries.Count > 0)
                    Log.Error($"Natives: {location.Directory} lacks {string.Join(", ", location.MissingLibraries)}; " +
                              "the clip browser will not open. Reinstall Win4KSS.");
                break;

            case NativeLocationKind.Missing:
                Log.Error($"Natives: {LibMpv} is not beside {AppContext.BaseDirectory} and the Win4KSS install at " +
                          $"{location.Directory} is missing or incomplete. The System32 screen saver needs the " +
                          "full user-scope install; reinstall Win4KSS.");
                break;
        }

        Current = location;
        return location;
    }

    /// <summary>
    /// The full path of an already-loaded module, or null if it is not loaded. For logging
    /// which copy of a DLL actually won.
    /// </summary>
    public static string? LoadedModulePath(string moduleName)
    {
        try
        {
            var module = GetModuleHandle(moduleName);
            if (module == IntPtr.Zero)
                return null;

            var buffer = new char[1024];
            var length = GetModuleFileName(module, buffer, (uint)buffer.Length);
            return length == 0 ? null : new string(buffer, 0, (int)length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectory(string? path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetModuleFileName(IntPtr module, [Out] char[] fileName, uint size);
}
