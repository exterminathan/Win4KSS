using System.Drawing;
using Win4K.Core;

namespace Win4K.ScreenSaver.Browser;

/// <summary>
/// What <see cref="BrowserBridge"/> needs from the window around the page: the native preview
/// panel, the Start now path, the walk-away shortcuts and the shell. <see cref="BrowserForm"/>
/// implements it; tests use a fake. Every member is called on the UI thread.
/// </summary>
public interface IBrowserHost
{
    /// <summary>Plays <paramref name="clip"/> (looped, muted) from <paramref name="playUrl"/> in the native panel, or stops and hides it when null.</summary>
    void ShowClip(Clip? clip, string? playUrl);

    /// <summary>Where the page wants the native panel, in CSS pixels relative to the page viewport.</summary>
    void SetPanelRect(PanelRect rect);

    /// <summary>Asks Windows to start the configured saver (<see cref="SaverLauncher.StartNow"/>). False if refused.</summary>
    /// <param name="startWithKey">A clip to play first ("Play now"); null for the rotation as usual.</param>
    bool StartNow(string? startWithKey);

    /// <summary>Which Start Screen Saver shortcuts exist. The hotkey is the bridge's (settings), not the host's.</summary>
    ShortcutOptions ReadShortcuts();

    /// <summary>Creates or removes the shortcuts; <see cref="ShortcutOptions.Hotkey"/> is ignored.</summary>
    /// <returns>False if any shortcut could not be written.</returns>
    bool ApplyShortcuts(ShortcutOptions options);

    /// <summary>
    /// Puts a hotkey already saved to settings.json into effect (<see cref="HotkeyAgent.Apply"/>):
    /// the resident listener re-reads it, or is started, or stops for "".
    /// </summary>
    /// <returns>A problem to tell the user (the combination is taken), or null.</returns>
    string? ApplyHotkey(string hotkey, string? previous);

    /// <summary>A native folder picker. Null when cancelled.</summary>
    string? PickFolder(string title, string? initialPath);

    /// <summary>A native Save dialog (<paramref name="filter"/> is the WinForms "name|pattern|…" form). Null when cancelled.</summary>
    string? PickSaveFile(string title, string suggestedName, string filter);

    /// <summary>A native Open dialog. Null when cancelled.</summary>
    string? PickOpenFile(string title, string filter);

    void OpenFolder(string path);

    /// <summary>
    /// The user clicked Install and the downloaded, verified installer has just been launched
    /// (detached — nothing here waits on it). That installer's own [Run] step
    /// (<c>install-scr.ps1</c>'s <c>Stop-NonSaverInstances</c>) force-stops any
    /// <c>Win4KSS.exe</c>/<c>.scr</c> that is not running as <c>/s</c> — including this very
    /// browser process — so the host must shut itself down cleanly (flush settings, dispose the
    /// download queue/cache/previews, close the WebView2) before that step can reach it, rather
    /// than being killed mid-operation. <see cref="BrowserForm"/> implements this as closing the
    /// window, which runs through the exact same shutdown path as closing it normally.
    /// </summary>
    void PrepareForInstallerHandoff();
}

/// <summary>
/// The detail pane's video rect as the page reports it: CSS pixels relative to the WebView's
/// viewport (what <c>getBoundingClientRect()</c> returns), so scrolling is already applied.
/// </summary>
public readonly record struct PanelRect(double X, double Y, double Width, double Height, bool Visible)
{
    public static PanelRect Hidden { get; } = new(0, 0, 0, 0, false);

    /// <summary>
    /// The rect in device pixels, relative to the WebView's top-left corner. One CSS pixel is
    /// <c>zoomFactor × dpi / 96</c> device pixels: WebView2 rasterises at the monitor's scale
    /// (the process is PerMonitorV2) and then applies its own zoom on top. Edges are rounded
    /// independently so adjacent rects do not gap. Empty when hidden or degenerate.
    /// </summary>
    public Rectangle ToDevicePixels(double zoomFactor, int dpi)
    {
        if (!Visible || Width <= 0 || Height <= 0 || zoomFactor <= 0 || dpi <= 0 ||
            !double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height))
        {
            return Rectangle.Empty;
        }

        var scale = zoomFactor * dpi / 96.0;
        var left = (int)Math.Round(X * scale);
        var top = (int)Math.Round(Y * scale);
        var right = (int)Math.Round((X + Width) * scale);
        var bottom = (int)Math.Round((Y + Height) * scale);
        return right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }
}
