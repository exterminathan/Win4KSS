using System.Runtime.InteropServices;

namespace Win4K.Core;

/// <summary>
/// Display metrics via Win32 rather than System.Windows.Forms, so Core stays UI-free and
/// can be exercised from tests without a message loop.
/// </summary>
public static partial class Display
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Primary display width in physical pixels. The process is PerMonitorV2 aware, so this
    /// is not scaled down by DPI — which is what we want when choosing 4K vs 1080 assets.
    /// </summary>
    public static int PrimaryWidth
    {
        get
        {
            try
            {
                var w = GetSystemMetrics(SM_CXSCREEN);
                return w > 0 ? w : 1920;
            }
            catch
            {
                return 1920;
            }
        }
    }

    public static int PrimaryHeight
    {
        get
        {
            try
            {
                var h = GetSystemMetrics(SM_CYSCREEN);
                return h > 0 ? h : 1080;
            }
            catch
            {
                return 1080;
            }
        }
    }
}
