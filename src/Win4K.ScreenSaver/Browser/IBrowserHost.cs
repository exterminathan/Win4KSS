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
    bool StartNow();

    ShortcutOptions ReadShortcuts();

    /// <returns>False if any shortcut could not be written.</returns>
    bool ApplyShortcuts(ShortcutOptions options);

    /// <summary>A native folder picker. Null when cancelled.</summary>
    string? PickFolder(string title, string? initialPath);

    /// <summary>A native Save dialog (<paramref name="filter"/> is the WinForms "name|pattern|…" form). Null when cancelled.</summary>
    string? PickSaveFile(string title, string suggestedName, string filter);

    /// <summary>A native Open dialog. Null when cancelled.</summary>
    string? PickOpenFile(string title, string filter);

    void OpenFolder(string path);
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
