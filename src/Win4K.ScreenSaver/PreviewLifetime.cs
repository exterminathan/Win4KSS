namespace Win4K.ScreenSaver;

/// <summary>
/// Decides when a /p preview process should give up and exit.
///
/// The preview window is a WS_CHILD of a thumbnail HWND owned by another process (the Screen
/// Saver Settings dialog). Windows is supposed to destroy cross-process children along with
/// their parent, but in practice /p processes were found alive long after their parent HWND
/// and its process were gone — each one a hidden, idle, leaked process. So the form polls this
/// once a second and closes itself rather than trusting the parent to take it down.
/// </summary>
internal static class PreviewLifetime
{
    public const int PollIntervalMs = 1000;

    /// <param name="expectedParent">The HWND passed on the command line with /p.</param>
    /// <param name="parentIsWindow">IsWindow(expectedParent) right now.</param>
    /// <param name="actualParent">Our window's real parent right now (GetAncestor GA_PARENT).</param>
    /// <returns>A reason to exit, or null to keep running.</returns>
    public static string? ExitReason(IntPtr expectedParent, bool parentIsWindow, IntPtr actualParent)
    {
        if (expectedParent == IntPtr.Zero)
            return "no preview parent";

        if (!parentIsWindow)
            return "preview parent window no longer exists";

        // Attach failed, or something reparented us away — either way we are now a stray
        // window that nothing will ever close.
        if (actualParent != expectedParent)
            return "no longer a child of the preview parent";

        return null;
    }
}
