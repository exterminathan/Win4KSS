using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// "Start now": the one way to start the saver on demand that honours "On resume, display
/// logon screen". Shared by /now (the walk-away shortcut) and the browser's Start now button.
/// </summary>
internal static class SaverLauncher
{
    /// <summary>
    /// Leaves the manual-launch marker (so the /s process Windows is about to start gives
    /// itself the longer input grace, see <see cref="LaunchContext"/>), then asks Windows to
    /// start the configured screen saver, exactly as the idle timer would.
    /// </summary>
    /// <returns>False if Windows did not accept the request.</returns>
    public static bool StartNow()
    {
        LaunchContext.MarkManualLaunch();

        if (NativeMethods.StartConfiguredScreenSaver())
        {
            Log.Info("StartNow: asked Windows to start the configured screen saver");
            return true;
        }

        Log.Warn("StartNow: SC_SCREENSAVE broadcast was not accepted");
        return false;
    }
}
