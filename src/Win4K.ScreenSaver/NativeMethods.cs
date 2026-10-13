using System.Runtime.InteropServices;

namespace Win4K.ScreenSaver;

internal static partial class NativeMethods
{
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;

    internal const int WM_SYSCOMMAND = 0x0112;
    internal const int SC_SCREENSAVE = 0xF140;
    internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    private const uint ES_CONTINUOUS = 0x80000000;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowNative(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GA_PARENT = 1;

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    private const uint ASFW_ANY = unchecked((uint)-1);

    /// <summary>
    /// Asks every top-level window to handle <paramref name="message"/> (a registered message,
    /// so only ours react), letting the receiver take the foreground — this process has it,
    /// because the user just launched it, and may hand it on.
    /// </summary>
    internal static bool BroadcastActivation(uint message)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        return message != 0 && PostMessage(HWND_BROADCAST, message, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    /// <summary>
    /// Reparents our window into the tiny monitor thumbnail that Screen Saver Settings hands
    /// us via /p. Making it a WS_CHILD is what makes it die with the settings dialog.
    ///
    /// Must be called from OnLoad or later: WinForms resets a top-level form's parent after
    /// OnHandleCreated, which used to undo this and leave the preview a child of the desktop
    /// that nothing ever destroyed. SaverForm also polls <see cref="PreviewLifetime"/> as a
    /// backstop. Returns false if the parent was already gone or SetParent refused.
    /// </summary>
    internal static bool AttachToPreview(IntPtr child, IntPtr parent, Form form)
    {
        if (!IsWindow(parent))
            return false;

        if (SetParent(child, parent) == IntPtr.Zero)
            return false;

        SetWindowLong(child, GWL_STYLE, GetWindowLong(child, GWL_STYLE) | WS_CHILD);

        if (GetClientRect(parent, out var rect))
        {
            form.Location = new Point(0, 0);
            form.Size = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        return true;
    }

    internal static bool IsWindow(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindowNative(hWnd);

    /// <summary>The real parent window (the desktop window for a top-level one), never the owner.</summary>
    internal static IntPtr ParentOf(IntPtr hWnd) => GetAncestor(hWnd, GA_PARENT);

    /// <summary>
    /// Asks Windows to start the configured screen saver, exactly as the idle timer would.
    ///
    /// This is what /now uses, and the distinction matters: a screen saver the *system*
    /// starts honours "On resume, display logon screen", whereas one launched directly as
    /// `saver.scr /s` does not lock on exit. For a walk-away hotkey the lock is the point.
    /// </summary>
    internal static bool StartConfiguredScreenSaver() =>
        PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_SCREENSAVE, IntPtr.Zero);

    /// <summary>
    /// Clears any display-required lock. mpv asserts one while playing video, which would
    /// otherwise stop the monitor ever sleeping — the screen saver would keep a 4K panel lit
    /// all night. cdima/aerial did the same thing for the same reason.
    /// </summary>
    internal static void AllowMonitorSleep() => SetThreadExecutionState(ES_CONTINUOUS);

    internal static Point CursorPosition =>
        GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;
}
