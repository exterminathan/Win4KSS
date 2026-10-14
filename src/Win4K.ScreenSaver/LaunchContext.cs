using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// Tells the saver whether it appeared because the machine went idle, or because the user
/// deliberately triggered it — which changes how long input is ignored for.
///
/// The complication: /now does not run the saver itself. It posts SC_SCREENSAVE and Windows
/// then launches the .scr with /s, exactly as the idle timer would, so by the time the
/// full-screen process starts there is nothing in its arguments to say a human just pressed
/// Ctrl+Alt+S a moment ago. Without that knowledge the key release lands inside the normal
/// grace period only by luck.
///
/// So /now leaves a short-lived marker file, and the /s process consumes it. The marker can
/// also name a clip to open with: the clip browser's "Play now".
/// </summary>
public static class LaunchContext
{
    /// <summary>Grace period for a saver the system started on idle.</summary>
    public const int SystemGraceMs = 1000;

    /// <summary>Grace period when the user just triggered it by hand.</summary>
    public const int ManualGraceMs = 1500;

    /// <summary>
    /// How recent a marker has to be to count as "this launch". Covers Start now waiting up to
    /// 5 s for Windows before running the saver itself.
    /// </summary>
    private static readonly TimeSpan MarkerWindow = TimeSpan.FromSeconds(15);

    private static string MarkerPath => Path.Combine(AppPaths.Root, "manual-launch.marker");

    private static int? _cached;
    private static string? _startWith;

    /// <summary>Written by /now immediately before it asks Windows to start the saver.</summary>
    /// <param name="startWithKey">A clip key to play first ("Play now"), or null for the rotation as usual.</param>
    public static void MarkManualLaunch(string? startWithKey = null)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(MarkerPath, Format(DateTime.UtcNow, startWithKey));
        }
        catch (Exception ex)
        {
            // Only costs us the longer grace period; not worth failing the launch over.
            Log.Warn($"LaunchContext: could not write marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Grace period for this process. Consumes the marker, so a later idle-triggered run
    /// does not inherit the longer window.
    /// </summary>
    public static int GraceMilliseconds
    {
        get
        {
            if (_cached is not null)
                return _cached.Value;

            var manual = ConsumeMarker(out _startWith);
            _cached = manual ? ManualGraceMs : SystemGraceMs;

            Log.Info($"LaunchContext: {(manual ? "manual" : "system")} launch, grace {_cached}ms" +
                     (_startWith is null ? "" : $", starting with {_startWith}"));
            return _cached.Value;
        }
    }

    /// <summary>The clip "Play now" asked for, if this launch came from it. Consumes the marker.</summary>
    public static string? StartWithKey
    {
        get
        {
            _ = GraceMilliseconds;
            return _startWith;
        }
    }

    /// <summary>The marker's content: the time it was written, then the clip key if there is one.</summary>
    internal static string Format(DateTime utcNow, string? startWithKey) =>
        string.IsNullOrWhiteSpace(startWithKey)
            ? utcNow.ToString("o")
            : utcNow.ToString("o") + "\n" + startWithKey.Trim();

    /// <summary>The clip key in a marker's content, or null.</summary>
    internal static string? ParseStartWith(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 1 ? lines[1] : null;
    }

    private static bool ConsumeMarker(out string? startWith)
    {
        startWith = null;
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
                return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            var content = File.ReadAllText(path);
            File.Delete(path);

            if (age < TimeSpan.Zero || age > MarkerWindow)
                return false;

            startWith = ParseStartWith(content);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
