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
/// So /now leaves a short-lived marker file, and the /s process consumes it.
/// </summary>
public static class LaunchContext
{
    /// <summary>Grace period for a saver the system started on idle.</summary>
    public const int SystemGraceMs = 1000;

    /// <summary>Grace period when the user just triggered it by hand.</summary>
    public const int ManualGraceMs = 1500;

    /// <summary>How recent a marker has to be to count as "this launch".</summary>
    private static readonly TimeSpan MarkerWindow = TimeSpan.FromSeconds(5);

    private static string MarkerPath => Path.Combine(AppPaths.Root, "manual-launch.marker");

    private static int? _cached;

    /// <summary>Written by /now immediately before it asks Windows to start the saver.</summary>
    public static void MarkManualLaunch()
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
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

            var manual = ConsumeMarker();
            _cached = manual ? ManualGraceMs : SystemGraceMs;

            Log.Info($"LaunchContext: {(manual ? "manual" : "system")} launch, grace {_cached}ms");
            return _cached.Value;
        }
    }

    private static bool ConsumeMarker()
    {
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
                return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            File.Delete(path);

            return age >= TimeSpan.Zero && age <= MarkerWindow;
        }
        catch
        {
            return false;
        }
    }
}
