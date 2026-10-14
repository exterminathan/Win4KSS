using System.Reflection;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// Creates the walk-away shortcut and its global hotkey.
///
/// The hotkey is a native field on the .lnk itself, which Explorer binds for shortcuts under
/// the Desktop or the Start Menu. That is why this is a shortcut rather than a tray app: no
/// background process, no RegisterHotKey agent, nothing running when the screen saver is not.
///
/// The shortcut points at /now rather than /s deliberately. /now asks Windows to start the
/// configured saver, which is the only route that honours "On resume, display logon screen" —
/// and a walk-away key that does not lock the machine is not much of a walk-away key.
/// </summary>
public static class ShortcutManager
{
    public const string ShortcutFileName = "Start Screen Saver.lnk";
    public const string DefaultHotkey = "CTRL+ALT+S";

    public static string DesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);

    public static string StartMenuPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName);

    public static bool DesktopShortcutExists => File.Exists(DesktopPath);
    public static bool StartMenuShortcutExists => File.Exists(StartMenuPath);

    /// <summary>
    /// Where the companion .exe lives. The shortcut must NOT point at the installed .scr.
    ///
    /// Windows registers scrfile's "open" verb as `"%1" /S`, so launching a .scr through the
    /// shell — which is what Explorer does for a shortcut — silently discards the shortcut's
    /// own arguments and substitutes /S. A shortcut aimed at the .scr therefore runs the
    /// saver directly instead of asking Windows to start it, which looks identical on screen
    /// but never honours "On resume, display logon screen". Verified empirically: the same
    /// shortcut with /now resolves to FullScreen when it targets the .scr and to StartNow
    /// when it targets the .exe.
    /// </summary>
    public static string CompanionExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Win4KSS", "Win4KSS.exe");

    /// <summary>
    /// The executable a shortcut should point at, always an .exe for the reason above.
    /// </summary>
    public static string ResolveTarget()
    {
        if (File.Exists(CompanionExePath))
            return CompanionExePath;

        var running = Environment.ProcessPath;
        if (running is not null && running.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return running;

        return CompanionExePath;
    }

    /// <summary>
    /// True when this assembly was published single-file (the old packaging:
    /// PublishSingleFile + IncludeNativeLibrariesForSelfExtract). A single-file host has no
    /// on-disk assembly file of its own to report, so <see cref="Assembly.Location"/> comes
    /// back empty for code running out of it. The normal case now is a loose multi-file
    /// publish — Win4KSS.exe sitting next to Win4K.Core.dll, libmpv-2.dll,
    /// WebView2Loader.dll, etc. — for which Location is the real path on disk.
    /// </summary>
    public static bool IsSingleFileBuild => string.IsNullOrEmpty(typeof(ShortcutManager).Assembly.Location);

    /// <summary>
    /// Makes sure a companion .exe exists for shortcuts to target, copying the running image
    /// if necessary. Lets the settings dialog create a working hotkey even when the app was
    /// started from the installed .scr and the install script was never run.
    /// </summary>
    public static bool EnsureCompanionExe() => EnsureCompanionExe(CompanionExePath);

    /// <summary>
    /// Testable core of <see cref="EnsureCompanionExe()"/>, taking the destination explicitly
    /// so tests can point it at a throwaway path instead of the real
    /// %LOCALAPPDATA%\Programs\Win4KSS.
    /// </summary>
    internal static bool EnsureCompanionExe(string destination)
    {
        try
        {
            if (!IsSingleFileBuild)
            {
                // Loose multi-file publish: Environment.ProcessPath is just Win4KSS.exe, and
                // it needs Win4K.Core.dll, libmpv-2.dll, WebView2Loader.dll etc. sitting right
                // beside it to run at all. Copying only the exe here would produce a companion
                // that looks right but cannot start — silently broken. Only install-scr.ps1 /
                // the installer, which copy the whole publish output together, may create this
                // companion now; this just reports whether they already have.
                if (File.Exists(destination))
                    return true;

                Log.Warn($"Shortcut: no companion exe at {destination}, and this build is not " +
                         "single-file, so one cannot be made by copying the running exe alone " +
                         "(it would be missing its sibling DLLs). Run scripts/install-scr.ps1 " +
                         "or the installer to create a working companion.");
                return false;
            }

            var source = Environment.ProcessPath;
            if (source is null || !File.Exists(source))
                return File.Exists(destination);

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                return true;

            if (File.Exists(destination) &&
                new FileInfo(destination).LastWriteTimeUtc >= new FileInfo(source).LastWriteTimeUtc)
            {
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);

            Log.Info($"Shortcut: companion exe at {destination}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Shortcut: could not create the companion exe", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes (or rewrites) the shortcut. Returns false with a reason if it could not.
    /// </summary>
    public static bool Create(string shortcutPath, string? hotkey = null, string? target = null, string? arguments = "/now")
    {
        try
        {
            target ??= ResolveTarget();

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                            ?? throw new InvalidOperationException("WScript.Shell is unavailable.");

            var shell = Activator.CreateInstance(shellType)
                        ?? throw new InvalidOperationException("Could not create WScript.Shell.");

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                  [shortcutPath])
                           ?? throw new InvalidOperationException("CreateShortcut returned nothing.");

            var type = shortcut.GetType();

            Set(type, shortcut, "TargetPath", target);
            Set(type, shortcut, "Arguments", arguments ?? "");
            Set(type, shortcut, "Description", "Start the screen saver now (locks on resume if configured)");
            Set(type, shortcut, "WorkingDirectory", Path.GetDirectoryName(target) ?? "");
            Set(type, shortcut, "IconLocation", target + ",0");

            if (!string.IsNullOrWhiteSpace(hotkey))
                Set(type, shortcut, "Hotkey", hotkey);

            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            Log.Info($"Shortcut: wrote {shortcutPath} -> {target} {arguments} ({hotkey})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Shortcut: could not create {shortcutPath}", ex);
            return false;
        }
    }

    public static bool Remove(string shortcutPath)
    {
        try
        {
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Shortcut: could not remove {shortcutPath}", ex);
            return false;
        }
    }

    /// <summary>Reads the hotkey currently bound to a shortcut, or null if it has none.</summary>
    public static string? ReadHotkey(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return null;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                  [shortcutPath]);
            if (shortcut is null) return null;

            var value = shortcut.GetType()
                                .InvokeMember("Hotkey", BindingFlags.GetProperty, null, shortcut, null) as string;

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What the browser's settings panel shows: which shortcuts exist and the hotkey on them
    /// (the Desktop one first, as that is the one most people make).
    /// </summary>
    public static ShortcutOptions ReadOptions() => new(
        DesktopShortcutExists,
        StartMenuShortcutExists,
        ReadHotkey(DesktopPath) ?? ReadHotkey(StartMenuPath) ?? DefaultHotkey);

    /// <summary>
    /// Creates or removes the Desktop and Start Menu shortcuts to match <paramref name="options"/>,
    /// all carrying the same hotkey. Shortcuts target the companion .exe, never the .scr.
    /// </summary>
    /// <returns>False if any shortcut could not be written or removed (details are logged).</returns>
    public static bool Apply(ShortcutOptions options)
    {
        var hotkey = string.IsNullOrWhiteSpace(options.Hotkey) ? null : options.Hotkey;

        // Shortcuts must target an .exe, never the installed .scr — see CompanionExePath.
        if (options.Desktop || options.StartMenu)
            EnsureCompanionExe();

        var ok = options.Desktop ? Create(DesktopPath, hotkey) : Remove(DesktopPath);
        ok &= options.StartMenu ? Create(StartMenuPath, hotkey) : Remove(StartMenuPath);
        return ok;
    }

    /// <summary>
    /// Normalises a hotkey typed or captured by the browser page into the "CTRL+ALT+S" form a
    /// .lnk expects. Accepts WinForms key names ("S", "F12", "D1") and the DOM's
    /// KeyboardEvent.code names ("KeyS", "Digit1"), with CTRL/ALT/SHIFT in any order and case.
    /// Null or empty means "no hotkey" and returns "". Returns null when it is not a usable
    /// hotkey (no modifier, or an unknown key).
    /// </summary>
    public static string? NormalizeHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var modifiers = Keys.None;
        Keys? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= Keys.Control;
                    continue;
                case "ALT":
                    modifiers |= Keys.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= Keys.Shift;
                    continue;
            }

            var name = raw;
            if (name.Length == 4 && name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
                name = name[3..];
            else if (name.Length == 6 && name.StartsWith("Digit", StringComparison.OrdinalIgnoreCase))
                name = "D" + name[5..];
            else if (name.Length == 1 && char.IsDigit(name[0]))
                name = "D" + name;

            // Enum.TryParse would also take "65" as Keys.A; only names count.
            if (!char.IsLetter(name[0]) || !Enum.TryParse<Keys>(name, ignoreCase: true, out var parsed) || key is not null)
                return null;
            key = parsed;
        }

        return key is { } k ? ToShortcutHotkey(k | modifiers) : null;
    }

    private static void Set(Type type, object instance, string property, string value) =>
        type.InvokeMember(property, BindingFlags.SetProperty, null, instance, [value]);

    /// <summary>
    /// Converts a WinForms key combination into the "CTRL+ALT+S" form a .lnk expects.
    /// </summary>
    public static string? ToShortcutHotkey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
            return null;

        var parts = new List<string>(4);
        if (keyData.HasFlag(Keys.Control)) parts.Add("CTRL");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("ALT");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("SHIFT");

        // Explorer will happily save a bare letter as a hotkey and then either never fire it
        // or steal that key globally. Require at least one modifier.
        if (parts.Count == 0)
            return null;

        parts.Add(key.ToString().ToUpperInvariant());
        return string.Join('+', parts);
    }
}

/// <summary>The walk-away shortcuts as the settings panel edits them.</summary>
/// <param name="Desktop">A shortcut on the Desktop.</param>
/// <param name="StartMenu">A shortcut in the Start Menu's Programs folder.</param>
/// <param name="Hotkey">"CTRL+ALT+S" form, or null/empty for none. Explorer binds it only on those two shortcuts.</param>
public sealed record ShortcutOptions(bool Desktop, bool StartMenu, string? Hotkey);
