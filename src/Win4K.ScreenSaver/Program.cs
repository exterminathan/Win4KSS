using System.Diagnostics;
using Win4K.Core;
using Win4K.Core.Playback;

namespace Win4K.ScreenSaver;

internal static class Program
{
    /// <summary>
    /// The Windows screen saver contract, plus two of our own:
    ///
    ///   Win4KSS.scr            show settings
    ///   Win4KSS.scr /c         show settings, modal to the foreground window
    ///   Win4KSS.scr /c:HWND    same, with the owner window packed into the argument
    ///   Win4KSS.scr /p HWND    render the preview thumbnail as a child of HWND
    ///   Win4KSS.scr /s         run full screen
    ///   Win4KSS.scr /w         run in a normal resizable window   (ours)
    ///   Win4KSS.scr /now       start the configured saver at once (ours)
    ///
    /// Run as Win4KSS.exe with no arguments it opens the window, because double-clicking
    /// an exe to get a settings dialog is surprising. Same binary, two extensions.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        MpvLoader.Register();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
            Log.Error("UI thread exception", e.Exception);

        var (verb, argument) = ParseArguments(args);
        Log.Info($"Launched with verb '{verb}'{(argument is null ? "" : $" arg '{argument}'")}");

        try
        {
            switch (verb)
            {
                case Verb.Configure:
                    Application.Run(new SettingsForm());
                    return 0;

                case Verb.Preview:
                    return RunPreview(argument);

                case Verb.FullScreen:
                    return RunFullScreen();

                case Verb.Window:
                    Application.Run(new SaverForm(SaverMode.Window));
                    return 0;

                case Verb.StartNow:
                    return StartNow();

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
            _ => Verb.Unknown,
        };

        return (verb, string.IsNullOrWhiteSpace(second) ? null : second);
    }

    // ---- verbs ----------------------------------------------------------------------------

    private static int StartNow()
    {
        // Leave a breadcrumb so the /s process that Windows is about to launch knows a human
        // triggered this and gives itself a longer input grace period.
        LaunchContext.MarkManualLaunch();

        if (NativeMethods.StartConfiguredScreenSaver())
        {
            Log.Info("StartNow: asked Windows to start the configured screen saver");
            return 0;
        }

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
