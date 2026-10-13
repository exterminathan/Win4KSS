using System.Diagnostics;
using System.Text;

namespace Win4K.Core;

/// <summary>
/// Minimal append-only file log.
///
/// A screen saver has nowhere to print: it is launched by the idle timer with no console,
/// and any MessageBox during /s would itself dismiss the saver. cdima/aerial relied on
/// Trace.WriteLine, which is invisible outside a debugger — so failures in the field were
/// undiagnosable. This writes to %LOCALAPPDATA%\Win4KScreensavers\win4k.log as well.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static bool Enabled { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Trace.WriteLine(line);

        if (!Enabled)
            return;

        try
        {
            lock (Gate)
            {
                AppPaths.EnsureRoot();
                var path = AppPaths.LogFile;

                // Truncate rather than rotate: this is a debugging aid, not an audit trail,
                // and an unbounded log on a screen saver that runs nightly is a slow leak.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the reason playback fails.
        }
    }
}
