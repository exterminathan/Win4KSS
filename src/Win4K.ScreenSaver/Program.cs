using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Win4K.Core;
using Win4K.Core.Playback;
using Win4K.ScreenSaver.Browser;

namespace Win4K.ScreenSaver;

internal static class Program
{
    /// <summary>
    /// The Windows screen saver contract, plus two of our own:
    ///
    ///   Win4KSS.scr            open the clip browser (settings live in it)
    ///   Win4KSS.scr /c         open the clip browser; a second /c activates the open one
    ///   Win4KSS.scr /c:HWND    same; the owner window Windows packs in is parsed but not used
    ///   Win4KSS.scr /p HWND    render the preview thumbnail as a child of HWND
    ///   Win4KSS.scr /s         run full screen
    ///   Win4KSS.scr /w         run in a normal resizable window   (ours)
    ///   Win4KSS.scr /now       start the configured saver at once (ours)
    ///   Win4KSS.exe --export-settings FILE                     write the readable settings export; no window (ours)
    ///   Win4KSS.exe --import-settings FILE [--include-machine] apply one; FILE must directly follow the verb (ours)
    ///
    /// Run as Win4KSS.exe with no arguments it opens the window, because double-clicking
    /// an exe to get a settings dialog is surprising. Same binary, two extensions.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // First, before anything P/Invokes: the one-file .scr in System32 has no native DLLs
        // beside it and has to borrow the user-scope install's. See NativeDependencies.
        var natives = NativeDependencies.Initialize();

        ApplicationConfiguration.Initialize();
        MpvLoader.Register();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
            Log.Error("UI thread exception", e.Exception);

        var (verb, argument) = ParseArguments(args);
        Log.Info($"Launched with verb '{verb}'{(argument is null ? "" : $" arg '{argument}'")}");

        switch (GateOnNatives(verb, natives))
        {
            case NativeGate.ExitQuietly:
                // A screen saver that crashes or hangs can leave the session's saver state
                // stuck; exiting at once just ends this run of it. The reason is in the log.
                Log.Warn($"{verb}: native DLLs missing; exiting without showing anything");
                return 0;

            case NativeGate.AskToReinstall:
                MessageBox.Show(
                    "Win4KSS cannot find the files it needs (libmpv-2.dll and WebView2Loader.dll).\n\n" +
                    $"They belong in:\n{natives.Directory}\n\n" +
                    "Reinstall Win4KSS to put them back.",
                    "Win4KSS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
        }

        try
        {
            switch (verb)
            {
                case Verb.Configure:
                    return RunBrowser(argument, natives);

                case Verb.Preview:
                    return RunPreview(argument);

                case Verb.FullScreen:
                    return RunFullScreen();

                case Verb.Window:
                    Application.Run(new SaverForm(SaverMode.Window));
                    return 0;

                case Verb.StartNow:
                    return StartNow();

                case Verb.ExportSettings:
                    return SettingsCli.Export(argument);

                case Verb.ImportSettings:
                    return SettingsCli.Import(argument, args.Contains("--include-machine", StringComparer.OrdinalIgnoreCase));

                default:
                    MessageBox.Show($"Unrecognised argument: {argument}", "Win4KSS",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Fatal error handling verb {verb}", ex);
            return 2;
        }
    }

    // ---- argument parsing ---------------------------------------------------------------

    internal enum Verb
    {
        Configure,
        Preview,
        FullScreen,
        Window,
        StartNow,
        ExportSettings,
        ImportSettings,
        Unknown,
    }

    /// <summary>
    /// Pulled out and made internal so the odd forms are directly testable. Windows is
    /// inconsistent here: it passes "/c:12345" packed with a colon in some paths and
    /// "/p 12345" as two arguments in others, and casing is not guaranteed.
    /// </summary>
    internal static (Verb Verb, string? Argument) ParseArguments(string[] args)
    {
        if (args.Length == 0)
        {
            // .exe means a human ran it; .scr means Windows did.
            var invokedAsExe = Environment.ProcessPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ?? false;
            return (invokedAsExe ? Verb.Window : Verb.Configure, null);
        }

        var first = args[0].Trim();
        string? second = args.Length > 1 ? args[1].Trim() : null;

        // "/c:12345" and "/P:12345" — split on the separator rather than assuming a length.
        var separator = first.IndexOfAny([':', '=']);
        if (separator > 0)
        {
            second = first[(separator + 1)..].Trim();
            first = first[..separator];
        }

        var verb = first.TrimStart('/', '-', '\\').ToLowerInvariant() switch
        {
            "c" or "config" or "configure" or "settings" => Verb.Configure,
            "p" or "preview" => Verb.Preview,
            "s" or "show" => Verb.FullScreen,
            "w" or "window" or "windowed" => Verb.Window,
            "now" or "start" or "lock" => Verb.StartNow,
            "export-settings" => Verb.ExportSettings,
            "import-settings" => Verb.ImportSettings,
            _ => Verb.Unknown,
        };

        return (verb, string.IsNullOrWhiteSpace(second) ? null : second);
    }

    // ---- native DLLs ----------------------------------------------------------------------

    internal enum NativeGate
    {
        Proceed,
        /// <summary>/s: nothing to show and nobody to tell; end this run of the saver cleanly.</summary>
        ExitQuietly,
        /// <summary>A human opened a window: say the install needs repairing.</summary>
        AskToReinstall,
    }

    /// <summary>
    /// What a verb does when its native DLLs cannot be found. Preview (no video), /now (it only
    /// asks Windows to start the saver, which runs its own check) and the settings verbs need
    /// none; the rest need libmpv, and the clip browser also WebView2Loader.
    /// </summary>
    internal static NativeGate GateOnNatives(Verb verb, NativeLocation natives) => verb switch
    {
        Verb.FullScreen when !natives.CanPlay => NativeGate.ExitQuietly,
        Verb.Window when !natives.CanPlay => NativeGate.AskToReinstall,
        Verb.Configure when !natives.CanBrowse => NativeGate.AskToReinstall,
        _ => NativeGate.Proceed,
    };

    /// <summary>
    /// Whether this verb may ever check for app updates: a network call, a background timer, or
    /// a dialog about one. Update checks are surfaced only in the clip browser — never in
    /// <see cref="Verb.FullScreen"/> (/s), <see cref="Verb.Preview"/> (/p, the Settings dialog's
    /// preview child), <see cref="Verb.Window"/> (/w, the saver itself, windowed) or
    /// <see cref="Verb.StartNow"/> (/now). A screen saver that stalls on a network call or pops a
    /// dialog over the desktop defeats the entire point of it; see CLAUDE.md "Never during the
    /// saver". This is the single chokepoint every update-related code path is expected to
    /// consult before doing anything — <see cref="Browser.BrowserForm"/> calls it when deciding
    /// whether to construct an <see cref="Core.Updates.UpdateChecker"/> at all, rather than that
    /// decision being implicit in "BrowserForm only ever runs for /c".
    /// </summary>
    internal static bool AllowsUpdateCheck(Verb verb) => verb == Verb.Configure;

    /// <summary>
    /// Points WebView2 at the companion dir's WebView2Loader.dll. Must run before any other
    /// WebView2 API. Run from the one-file .scr with none of these (nor SetDllDirectory), the
    /// first WebView2 call fails with "Unable to load DLL 'WebView2Loader.dll'" and the browser
    /// reports the runtime as missing. Measured from a temp copy of the .scr: SetDllDirectory,
    /// SetLoaderDllFolderPath and the resolver below each fix it on their own; all three are
    /// kept so no single one of them is load-bearing.
    /// </summary>
    private static void UseCompanionWebView2Loader(string directory)
    {
        var loader = Path.Combine(directory, NativeDependencies.WebView2Loader);
        try
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser: SetLoaderDllFolderPath({directory}) failed: {ex.Message}");
        }

        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(CoreWebView2Environment).Assembly,
                (name, _, _) =>
                    Path.GetFileNameWithoutExtension(name).Equals("WebView2Loader", StringComparison.OrdinalIgnoreCase) &&
                    System.Runtime.InteropServices.NativeLibrary.TryLoad(loader, out var handle)
                        ? handle
                        : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // The assembly already has a resolver of its own; SetLoaderDllFolderPath covers it.
        }
    }

    // ---- verbs ----------------------------------------------------------------------------

    /// <summary>The single-instance mutex of the clip browser, per session.</summary>
    /// <remarks>Scoped by the data root only when WIN4K_HOME overrides it, so a test or smoke run on a copy of the profile is its own instance.</remarks>
    internal static string BrowserMutexName =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeOverrideVariable))
            ? @"Local\Win4KSS.Browser"
            : @"Local\Win4KSS.Browser." + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..8];

    internal const string WebView2RuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static int RunBrowser(string? ownerArgument, NativeLocation natives)
    {
        if (natives.Kind == NativeLocationKind.Companion)
            UseCompanionWebView2Loader(natives.Directory);

        // Windows' Screen Saver Settings passes its own HWND ("/c:1234"). The browser is a full
        // window of its own rather than a dialog modal to that one, so it is only logged.
        if (ownerArgument is not null)
            Log.Info($"Browser: opened from Screen Saver Settings (owner {ownerArgument})");

        // One browser at a time: two would fight over previews/index.json and both edit
        // settings.json. A second /c brings the first to the front instead.
        using var mutex = new Mutex(initiallyOwned: true, BrowserMutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Info("Browser: already open; activating it");
            NativeMethods.BroadcastActivation(BrowserForm.ActivateMessage);
            return 0;
        }

        if (!IsWebView2RuntimeInstalled(out var reason))
        {
            Log.Error($"Browser: WebView2 runtime missing ({reason})");
            var open = MessageBox.Show(
                "The clip browser needs the Microsoft Edge WebView2 Runtime, which is not installed.\n\n" +
                $"Download it from:\n{WebView2RuntimeUrl}\n\nOpen the download page now?",
                "Win4KSS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (open == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(WebView2RuntimeUrl) { UseShellExecute = true });
            return 1;
        }

        Application.Run(new BrowserForm());
        GC.KeepAlive(mutex);
        return 0;
    }

    private static bool IsWebView2RuntimeInstalled(out string? reason)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            reason = version is null ? "no version reported" : null;
            return version is not null;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static int StartNow()
    {
        // SaverLauncher leaves a breadcrumb so the /s process that Windows is about to launch
        // knows a human triggered this and gives itself a longer input grace period.
        if (SaverLauncher.StartNow())
            return 0;

        // A full-screen exclusive app can swallow the broadcast. Falling back to running
        // ourselves loses "on resume, display logon screen", so say so in the log rather
        // than pretending the walk-away lock happened.
        Log.Warn("StartNow: SC_SCREENSAVE broadcast failed; falling back to /s (no resume lock)");
        return RunFullScreen();
    }

    private static int RunPreview(string? handleArgument)
    {
        if (!long.TryParse(handleArgument, out var raw) || raw == 0)
        {
            Log.Warn("Preview: no usable window handle supplied");
            return 1;
        }

        // Windows creates and destroys the thumbnail rapidly as the user scrolls the list, so
        // the parent can already be gone by the time this process gets here.
        if (!NativeMethods.IsWindow(new IntPtr(raw)))
        {
            Log.Info($"Preview: parent 0x{raw:X} is already gone; exiting");
            return 0;
        }

        // Deliberately no video in the preview thumbnail. Windows creates and destroys this
        // window rapidly as the user scrolls the screen saver list, and spinning up a 4K
        // decoder each time is both slow and pointless at 150x100 pixels.
        var form = new SaverForm(SaverMode.Preview, showVideo: false)
        {
            PreviewParent = new IntPtr(raw),
        };

        // SaverForm polls the parent and ends this loop when it disappears; see PreviewLifetime.
        Application.Run(form);
        Log.Info("Preview: message loop ended");
        return 0;
    }

    private static int RunFullScreen()
    {
        var settings = AppSettings.Load();
        var screens = Screen.AllScreens;

        Log.Info($"FullScreen: {screens.Length} display(s), mode {settings.MultiMonitorMode}");

        var forms = new List<SaverForm>();

        if (settings.MultiMonitorMode == MultiMonitorMode.SpanAll && screens.Length > 1)
        {
            forms.Add(new SaverForm(SaverMode.FullScreen, UnionBounds(screens)));
        }
        else
        {
            foreach (var screen in screens)
            {
                // In MainOnly every display is still covered, but only the primary decodes
                // video — the rest are plain black. Leaving them uncovered would show the
                // desktop on the other monitors, which defeats the point.
                var showVideo = settings.MultiMonitorMode != MultiMonitorMode.MainOnly || screen.Primary;

                // Only the primary window populates the cache. Without this, three monitors
                // means three simultaneous downloads of three different 346 MB clips.
                forms.Add(new SaverForm(SaverMode.FullScreen, screen.Bounds, showVideo,
                                        shouldCache: screen.Primary));
            }
        }

        // Any one window closing dismisses the whole saver, so a mouse move on the second
        // monitor does not leave the first one still covered.
        foreach (var form in forms)
            form.FormClosing += (_, _) => Application.Exit();

        foreach (var form in forms)
            form.Show();

        NativeMethods.AllowMonitorSleep();

        Application.Run();
        Log.Info("FullScreen: message loop ended");
        return 0;
    }

    private static Rectangle UnionBounds(Screen[] screens)
    {
        var bounds = screens[0].Bounds;
        foreach (var screen in screens.Skip(1))
            bounds = Rectangle.Union(bounds, screen.Bounds);
        return bounds;
    }

    [Conditional("DEBUG")]
    internal static void DebugOnly(Action action) => action();
}
