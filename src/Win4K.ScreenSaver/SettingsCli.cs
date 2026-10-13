using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.ScreenSaver.Browser;

namespace Win4K.ScreenSaver;

/// <summary>
/// <c>Win4KSS.exe --export-settings &lt;file&gt;</c> and <c>--import-settings &lt;file&gt; [--include-machine]</c>:
/// <see cref="SettingsPortability"/> without a window, for an installer or script. Exit 0 on
/// success, 1 on a usage or file problem (message on stderr), 2 on an unexpected error (Program).
/// Output goes to the parent console when there is one; redirect or `start /wait` to capture it.
/// </summary>
internal static class SettingsCli
{
    public static ShortcutOptions Merge(ShortcutOptions current, PortableShortcuts? wanted)
    {
        if (wanted is null)
            return current;
        // Shortcuts report "Alt+Ctrl+S" and the file says "CTRL+ALT+S": the same hotkey must not
        // count as a change, or importing would rewrite the .lnk files for nothing.
        var hotkey = current.Hotkey;
        if (wanted.Hotkey is { } text && ShortcutManager.NormalizeHotkey(text) is { } normalized &&
            !string.Equals(normalized, ShortcutManager.NormalizeHotkey(current.Hotkey) ?? "", StringComparison.OrdinalIgnoreCase))
        {
            hotkey = normalized.Length == 0 ? null : normalized;
        }
        return new ShortcutOptions(wanted.Desktop ?? current.Desktop, wanted.StartMenu ?? current.StartMenu, hotkey);
    }

    /// <summary>The shortcuts as the file stores them: hotkey in the normalised "CTRL+ALT+S" form, "" for none.</summary>
    public static PortableShortcuts ToPortable(ShortcutOptions s) =>
        new(s.Desktop, s.StartMenu, ShortcutManager.NormalizeHotkey(s.Hotkey) ?? s.Hotkey ?? "");

    public static int Export(string? path)
    {
        if (path is null)
            return Fail("usage: Win4KSS.exe --export-settings <file>");
        var settings = AppSettings.Load();
        var catalogue = ClipLibrary.LoadAsync(settings).GetAwaiter().GetResult();
        SettingsMigrator.Prepare(settings, catalogue); // in memory only
        var model = SettingsPortability.Build(settings, catalogue, ToPortable(ShortcutManager.ReadOptions()),
            typeof(SettingsCli).Assembly.GetName().Version?.ToString(), DateTime.UtcNow);
        SettingsPortability.WriteFile(model, path);
        return Say($"Exported settings to {Path.GetFullPath(path)}");
    }

    public static int Import(string? path, bool includeMachine)
    {
        if (path is null)
            return Fail("usage: Win4KSS.exe --import-settings <file> [--include-machine]");
        if (!SettingsPortability.TryParseFile(path, out var file, out var error))
            return Fail(error ?? "could not read the file");

        var settings = AppSettings.Load();
        var catalogue = ClipLibrary.LoadAsync(settings).GetAwaiter().GetResult();
        SettingsMigrator.Prepare(settings, catalogue);
        SettingsPortability.BackUp();

        var applied = SettingsPortability.Apply(settings, file!, includeMachine);
        var warning = "";
        if (applied.PinKeys is { } pins)
        {
            // No downloads here; the browser (or saver) queues pins the next time it starts.
            var known = catalogue.Where(c => !c.IsLocal && pins.Contains(c.Key)).ToList();
            var bytes = known.Sum(c => c.SizeBytes ?? PinManager.UnknownClipBytes);
            if (bytes <= settings.CacheBudgetBytes)
                settings.KeptOffline = known.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            else
                warning = $" Kept-offline clips were NOT changed: they need {bytes >> 30} GB, over the {settings.CacheBudgetGb:0.#} GB limit.";
        }

        var cur = ShortcutManager.ReadOptions();
        if (Merge(cur, file!.Shortcuts) is var target && target != cur && !ShortcutManager.Apply(target))
            warning += " The shortcuts could not be updated.";

        BrowserBridge.SaveMerged(settings);
        return Say($"Imported settings from {Path.GetFullPath(path)}: {settings.RotationKeys.Count} rotation clips, " +
                   $"{settings.KeptOffline.Count} kept offline. Previous settings: {SettingsPortability.BackupFileName}.{warning}");
    }

    private static int Say(string text)
    {
        NativeMethods.AttachConsole(uint.MaxValue); // ATTACH_PARENT_PROCESS
        Console.Out.WriteLine(text);
        Log.Info("Settings CLI: " + text);
        return 0;
    }

    private static int Fail(string text)
    {
        NativeMethods.AttachConsole(uint.MaxValue);
        Console.Error.WriteLine("Win4KSS: " + text);
        Log.Warn("Settings CLI: " + text);
        return 1;
    }
}
