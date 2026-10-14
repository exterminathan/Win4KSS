using System.Diagnostics;
using Microsoft.Win32;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// "Start now": shared by /now (the walk-away shortcut and hotkey) and the browser's Start now
/// button. Prefers asking Windows to start the configured saver, because only a saver the
/// system starts honours "On resume, display logon screen". If Windows does not start one —
/// no saver enabled, ours not selected, the request ignored — it runs ours directly and locks
/// the workstation itself afterwards when that setting is on. Start now never does nothing.
/// </summary>
internal static class SaverLauncher
{
    /// <summary>Passed with /s by a direct run: lock the workstation when the saver is dismissed.</summary>
    public const string LockOnExitFlag = "--lock-on-exit";

    private static readonly TimeSpan SystemStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Blocks up to <see cref="SystemStartTimeout"/>; call it off the UI thread.</summary>
    /// <param name="startWithKey">A clip to play first (the browser's "Play now"); null for the rotation.</param>
    /// <returns>False only if no saver could be started at all.</returns>
    public static bool StartNow(string? startWithKey = null)
    {
        // The /s process about to start reads this for the longer input grace, and the clip to open with.
        LaunchContext.MarkManualLaunch(startWithKey);

        if (NativeMethods.IsScreenSaverRunning())
        {
            Log.Info("StartNow: a screen saver is already running");
            return true;
        }

        if (startWithKey is not null)
            Log.Info($"StartNow: playing {startWithKey} first");

        if (IsOursTheSystemSaver())
        {
            if (NativeMethods.StartConfiguredScreenSaver() && WaitForSystemSaver())
            {
                Log.Info("StartNow: Windows started the configured screen saver");
                return true;
            }

            Log.Warn($"StartNow: Windows did not start the screen saver within {SystemStartTimeout.TotalSeconds:0}s; running it directly");
        }
        else
        {
            Log.Info("StartNow: Win4KSS is not the enabled system screen saver; running it directly");
        }

        return RunDirectly();
    }

    /// <summary>After a direct run's saver is dismissed: what Windows would have done for its own.</summary>
    public static void LockAfterDirectRun()
    {
        Log.Info("StartNow: locking the workstation (\"On resume, display logon screen\" is on)");
        if (!NativeMethods.LockWorkStation())
            Log.Warn("StartNow: LockWorkStation failed");
    }

    /// <summary>Screen savers enabled for this session, and the selected one is Win4KSS.</summary>
    private static bool IsOursTheSystemSaver()
    {
        if (!NativeMethods.IsScreenSaverActive())
            return false;

        var selected = DesktopValue("SCRNSAVE.EXE");
        return selected is not null &&
               Path.GetFileNameWithoutExtension(selected).Equals("Win4KSS", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Environment.ExpandEnvironmentVariables(selected));
    }

    private static bool WaitForSystemSaver()
    {
        var deadline = DateTime.UtcNow + SystemStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (NativeMethods.IsScreenSaverRunning())
                return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static bool RunDirectly()
    {
        var companion = Path.Combine(AppPaths.CompanionDirectory, CompanionRelay.ExeName);
        var exe = File.Exists(companion) ? companion : Environment.ProcessPath;
        if (exe is null)
        {
            Log.Error("StartNow: no executable to run the saver from");
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add("/s");
        if (DesktopValue("ScreenSaverIsSecure") == "1")
            start.ArgumentList.Add(LockOnExitFlag);

        try
        {
            using var process = Process.Start(start);
            Log.Info($"StartNow: started {exe} {string.Join(' ', start.ArgumentList)}");
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"StartNow: could not start {exe}", ex);
            return false;
        }
    }

    private static string? DesktopValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
