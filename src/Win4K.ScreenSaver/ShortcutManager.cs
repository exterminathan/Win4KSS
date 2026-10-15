using System.Reflection;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// Creates the optional "Start Screen Saver" shortcuts on the Desktop and in the Start Menu.
///
/// They are launchers only. The global hotkey is held by the resident listener
/// (<see cref="HotkeyAgent"/>), not by a Hotkey field on these .lnk files as it was until
/// v1.2.6: Explorer only binds that field for shortcuts in those two places, so turning a
/// shortcut off turned the hotkey off, and a field left on a shortcut would make Explorer grab
/// the same combination and start the saver twice. So shortcuts are always written without
/// one, and <see cref="ClearHotkey"/> strips it from old ones.
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
    public static string CompanionExePath => Path.Combine(AppPaths.CompanionDirectory, "Win4KSS.exe");

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
    /// Reports whether the companion .exe exists for shortcuts to target, logging if not.
    ///
    /// It never creates one. Only the installers (install-scr.ps1, the setup exe) do, because
    /// they copy the whole loose publish together. Copying the running image would be wrong
    /// both ways round: from a loose install it yields an exe missing its sibling DLLs, and
    /// from the one-file .scr in System32 it would overwrite the companion Win4KSS.exe with
    /// the single-file bundle (and, through the hard link, the user-scope Win4KSS.scr too).
    /// </summary>
    public static bool EnsureCompanionExe() => EnsureCompanionExe(CompanionExePath);

    /// <summary>
    /// Testable core of <see cref="EnsureCompanionExe()"/>, taking the path explicitly so tests
    /// can point it at a throwaway location instead of the real %LOCALAPPDATA%\Programs\Win4KSS.
    /// </summary>
    internal static bool EnsureCompanionExe(string companionExe)
    {
        if (File.Exists(companionExe))
            return true;

        Log.Warn($"Shortcut: no companion exe at {companionExe}. Shortcuts need the user-scope " +
                 "install; run the Win4KSS installer (or scripts/install-scr.ps1) to create it.");
        return false;
    }

    /// <summary>
    /// Writes (or rewrites) the shortcut. Returns false with a reason if it could not.
    /// </summary>
    public static bool Create(string shortcutPath, string? target = null, string? arguments = "/now")
    {
        try
        {
            target ??= ResolveTarget();

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

            WithShortcut(shortcutPath, (type, shortcut) =>
            {
                Set(type, shortcut, "TargetPath", target);
                Set(type, shortcut, "Arguments", arguments ?? "");
                Set(type, shortcut, "Description", "Start the screen saver now (locks on resume if configured)");
                Set(type, shortcut, "WorkingDirectory", Path.GetDirectoryName(target) ?? "");
                var icon = Path.Combine(Path.GetDirectoryName(target) ?? "", "win4kss.ico");
                Set(type, shortcut, "IconLocation", (File.Exists(icon) ? icon : target) + ",0");

                // Never a hotkey on the shortcut (the listener holds it); clears one an old version left.
                Set(type, shortcut, "Hotkey", "");

                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return 0;
            });

            Log.Info($"Shortcut: wrote {shortcutPath} -> {target} {arguments}");
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

    /// <summary>
    /// Removes the Hotkey field from an existing shortcut, leaving everything else as it is.
    /// Explorer releases the combination as soon as the file is saved (measured).
    /// </summary>
    public static void ClearHotkey(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return;

            WithShortcut(shortcutPath, (type, shortcut) =>
            {
                Set(type, shortcut, "Hotkey", "");
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return 0;
            });
            Log.Info($"Shortcut: cleared the old hotkey field on {shortcutPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Shortcut: could not clear the hotkey on {shortcutPath}", ex);
        }
    }

    /// <summary>Reads the hotkey a shortcut carries (only old ones do), or null if it has none.</summary>
    public static string? ReadHotkey(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return null;

            var value = WithShortcut(shortcutPath, (type, shortcut) =>
                type.InvokeMember("Hotkey", BindingFlags.GetProperty, null, shortcut, null) as string);

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What the browser's settings panel shows: which shortcuts exist, plus the hotkey in force
    /// (from settings, see <see cref="HotkeyAgent.Effective"/>; the shortcuts do not carry it).
    /// </summary>
    public static ShortcutOptions ReadOptions(string? hotkey) => new(DesktopShortcutExists, StartMenuShortcutExists, hotkey);

    /// <summary>
    /// Creates or removes the Desktop and Start Menu shortcuts to match <paramref name="options"/>.
    /// The hotkey is not theirs and is ignored here. Only a shortcut whose presence changes is
    /// touched. Shortcuts target the companion .exe, never the .scr.
    /// </summary>
    /// <returns>False if any shortcut could not be written or removed (details are logged).</returns>
    public static bool Apply(ShortcutOptions options)
    {
        // Shortcuts must target an .exe, never the installed .scr — see CompanionExePath.
        if (options.Desktop || options.StartMenu)
            EnsureCompanionExe();

        var ok = Sync(DesktopPath, options.Desktop);
        ok &= Sync(StartMenuPath, options.StartMenu);
        return ok;

        static bool Sync(string path, bool wanted) =>
            wanted ? File.Exists(path) || Create(path) : Remove(path);
    }

    /// <summary>
    /// Normalises a hotkey typed or captured by the browser page into the "CTRL+ALT+S" form
    /// settings store (the same form the old .lnk Hotkey field used). Accepts WinForms key names ("S", "F12", "D1") and the DOM's
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

    /// <summary>
    /// Opens (or starts) a .lnk through WScript.Shell and releases both COM objects before
    /// returning. Left to the finalizer, they kept the windowless hotkey listener from ever
    /// exiting: its STA was gone by then, and releasing them waited on it (measured: the process
    /// outlived its "exiting" log line until killed).
    /// </summary>
    private static T WithShortcut<T>(string shortcutPath, Func<Type, object, T> use)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Could not create WScript.Shell.");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                       ?? throw new InvalidOperationException("CreateShortcut returned nothing.");
            return use(shortcut.GetType(), shortcut);
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
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

/// <summary>The Start now options as the settings panel edits them.</summary>
/// <param name="Desktop">A shortcut on the Desktop.</param>
/// <param name="StartMenu">A shortcut in the Start Menu's Programs folder.</param>
/// <param name="Hotkey">"CTRL+ALT+S" form, or null/empty for none. Held by <see cref="HotkeyAgent"/>, independent of the shortcuts.</param>
public sealed record ShortcutOptions(bool Desktop, bool StartMenu, string? Hotkey);
