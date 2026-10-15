using Win4K.Core;
using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// The resident hotkey listener (<see cref="HotkeyAgent"/>): parsing the stored hotkey into
/// RegisterHotKey arguments, the names other processes find it by (the installer hard-codes
/// them), its HKCU Run value, and moving the hotkey off the old Start Screen Saver shortcuts.
/// None of this touches the registry, the real shortcuts or a real hotkey.
/// </summary>
public class HotkeyAgentTests
{
    [Theory]
    [InlineData("CTRL+ALT+S", HotkeyAgent.MOD_CONTROL | HotkeyAgent.MOD_ALT, 0x53u)]
    [InlineData("Alt+Ctrl+S", HotkeyAgent.MOD_CONTROL | HotkeyAgent.MOD_ALT, 0x53u)] // what a .lnk reports
    [InlineData("ctrl+shift+F12", HotkeyAgent.MOD_CONTROL | HotkeyAgent.MOD_SHIFT, 0x7Bu)]
    [InlineData("Ctrl+Alt+Digit1", HotkeyAgent.MOD_CONTROL | HotkeyAgent.MOD_ALT, 0x31u)]
    [InlineData("CTRL+ALT+SHIFT+KeyK", HotkeyAgent.MOD_CONTROL | HotkeyAgent.MOD_ALT | HotkeyAgent.MOD_SHIFT, 0x4Bu)]
    [InlineData("ALT+F4", HotkeyAgent.MOD_ALT, 0x73u)]
    public void Hotkeys_parse_into_RegisterHotKey_arguments(string text, uint modifiers, uint virtualKey)
    {
        Assert.True(HotkeyAgent.TryParse(text, out var mods, out var vk));
        Assert.Equal(modifiers, mods);
        Assert.Equal(virtualKey, vk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S")]          // no modifier: it would steal the key globally
    [InlineData("CTRL+ALT")]   // no key
    [InlineData("CTRL+ALT+Bogus")]
    [InlineData("CTRL+65")]
    public void Unusable_hotkeys_do_not_parse(string? text)
    {
        Assert.False(HotkeyAgent.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData(null, "CTRL+ALT+S")]   // never set: the default
    [InlineData("", "")]               // turned off
    [InlineData("Alt+Ctrl+K", "CTRL+ALT+K")]
    [InlineData("garbage", "")]
    public void The_hotkey_in_force_comes_from_the_stored_value(string? stored, string expected)
    {
        Assert.Equal(expected, HotkeyAgent.Effective(stored));
    }

    [Theory]
    [InlineData("CTRL+ALT+S", "Ctrl+Alt+S")]
    [InlineData("CTRL+SHIFT+D1", "Ctrl+Shift+1")]
    [InlineData("ALT+F12", "Alt+F12")]
    public void Hotkeys_display_as_people_write_them(string hotkey, string expected)
    {
        Assert.Equal(expected, HotkeyAgent.Display(hotkey));
    }

    [Fact]
    public void The_normal_data_root_uses_the_names_the_installer_expects()
    {
        var root = Path.Combine(Path.GetTempPath(), "Win4KScreensavers");
        var scope = HotkeyAgent.Scope(root, root);

        Assert.Equal("", scope);
        // installer/Win4KSS.iss (StopHotkeyListener) and scripts/install-scr.ps1 hard-code these.
        Assert.Equal(@"Local\Win4KSS.Hotkey", HotkeyAgent.MutexNameFor(scope));
        Assert.Equal("Win4KSS", HotkeyAgent.RunValueNameFor(scope));
        Assert.Equal("", HotkeyAgent.Scope(root.ToUpperInvariant() + Path.DirectorySeparatorChar, root));
    }

    [Fact]
    public void Another_data_root_gets_its_own_instance_and_run_value()
    {
        var normal = Path.Combine(Path.GetTempPath(), "Win4KScreensavers");
        var scope = HotkeyAgent.Scope(Path.Combine(Path.GetTempPath(), "smoke-profile"), normal);

        Assert.Matches(@"^\.[0-9A-F]{8}$", scope);
        Assert.Equal(scope, HotkeyAgent.Scope(Path.Combine(Path.GetTempPath(), "SMOKE-PROFILE"), normal));
        Assert.NotEqual(scope, HotkeyAgent.Scope(Path.Combine(Path.GetTempPath(), "other-profile"), normal));
        Assert.Equal(@"Local\Win4KSS.Hotkey" + scope, HotkeyAgent.MutexNameFor(scope));
        Assert.Equal("Win4KSS-test-" + scope[1..], HotkeyAgent.RunValueNameFor(scope));
    }

    [Fact]
    public void The_events_are_named_after_the_mutex_and_a_test_run_never_uses_the_real_names()
    {
        Assert.Equal(HotkeyAgent.MutexName + ".Stop", HotkeyAgent.StopEventName);
        Assert.Equal(HotkeyAgent.MutexName + ".Reload", HotkeyAgent.ReloadEventName);
        // The test run's data root (TestEnvironment) is never the real one.
        Assert.NotEqual(@"Local\Win4KSS.Hotkey", HotkeyAgent.MutexName);
        Assert.NotEqual("Win4KSS", HotkeyAgent.RunValueName);
    }

    [Fact]
    public void The_run_value_starts_the_exe_with_the_hotkey_verb()
    {
        var exe = @"C:\Users\Someone\AppData\Local\Programs\Win4KSS\Win4KSS.exe";
        Assert.Equal("\"" + exe + "\" /hotkey", HotkeyAgent.RunCommand(exe));
        Assert.Equal("Hotkey", Program.ParseArguments(["/hotkey"]).Verb.ToString());
    }

    [Theory]
    [InlineData(@"D:\dev\publish\Win4KSS.exe", @"D:\dev\publish\Win4KSS.exe")]      // this build's own exe
    [InlineData(@"C:\Windows\System32\Win4KSS.scr", @"C:\companion\Win4KSS.exe")]  // never a .scr
    [InlineData(null, @"C:\companion\Win4KSS.exe")]
    public void The_listener_runs_as_an_exe(string? processPath, string expected)
    {
        Assert.Equal(expected, HotkeyAgent.AgentExe(processPath, @"C:\companion\Win4KSS.exe"));
    }

    // ---- migration off the .lnk Hotkey field ---------------------------------------------------

    private const string Desktop = @"C:\Desktop\Start Screen Saver.lnk";
    private const string StartMenu = @"C:\Programs\Start Screen Saver.lnk";

    private static (bool Changed, List<string> Cleared) Migrate(AppSettings settings, bool existed, Dictionary<string, string?> shortcuts)
    {
        var cleared = new List<string>();
        var changed = HotkeyAgent.MigrateShortcutHotkeys(settings, existed, [Desktop, StartMenu],
            shortcuts.ContainsKey, p => shortcuts[p], cleared.Add);
        return (changed, cleared);
    }

    [Fact]
    public void A_shortcut_hotkey_is_adopted_and_cleared_from_every_shortcut()
    {
        var settings = new AppSettings();
        var (changed, cleared) = Migrate(settings, true, new() { [Desktop] = "Alt+Ctrl+K", [StartMenu] = "Alt+Ctrl+K" });

        Assert.True(changed);
        Assert.Equal("CTRL+ALT+K", settings.StartNowHotkey);
        Assert.Equal([Desktop, StartMenu], cleared);
    }

    [Fact]
    public void Shortcuts_without_a_hotkey_on_an_existing_install_mean_it_was_turned_off()
    {
        var settings = new AppSettings();
        var (changed, cleared) = Migrate(settings, true, new() { [Desktop] = null });

        Assert.True(changed);
        Assert.Equal("", settings.StartNowHotkey);
        Assert.Empty(cleared);
    }

    [Fact]
    public void A_fresh_install_gets_the_default_hotkey_whatever_the_shortcuts_say()
    {
        var settings = new AppSettings();
        Assert.True(Migrate(settings, false, new() { [Desktop] = null, [StartMenu] = null }).Changed);
        Assert.Equal(ShortcutManager.DefaultHotkey, settings.StartNowHotkey);

        var noShortcuts = new AppSettings();
        Assert.True(Migrate(noShortcuts, true, new()).Changed);
        Assert.Equal(ShortcutManager.DefaultHotkey, noShortcuts.StartNowHotkey);
    }

    [Fact]
    public void A_hotkey_already_in_settings_wins_but_the_old_field_is_still_cleared()
    {
        var settings = new AppSettings { StartNowHotkey = "CTRL+SHIFT+X" };
        var (changed, cleared) = Migrate(settings, true, new() { [Desktop] = "Alt+Ctrl+S" });

        Assert.False(changed);
        Assert.Equal("CTRL+SHIFT+X", settings.StartNowHotkey);
        Assert.Equal([Desktop], cleared);
    }

    [Fact]
    public void Once_migrated_nothing_changes()
    {
        var settings = new AppSettings { StartNowHotkey = "CTRL+ALT+S" };
        var (changed, cleared) = Migrate(settings, true, new() { [Desktop] = null, [StartMenu] = null });

        Assert.False(changed);
        Assert.Empty(cleared);
    }

    // ---- a process that loaded its settings earlier must not undo the migration ----------------

    [Fact]
    public void Saving_a_never_set_hotkey_keeps_the_one_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory("win4k-hotkey-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var stale = AppSettings.Load(path);          // loaded before the listener migrated
            new AppSettings { StartNowHotkey = "CTRL+ALT+K" }.Save(path);

            stale.Shuffle = false;
            stale.Save(path);

            var reloaded = AppSettings.Load(path);
            Assert.Equal("CTRL+ALT+K", reloaded.StartNowHotkey);
            Assert.False(reloaded.Shuffle);

            // "" is a real choice (off), and does overwrite.
            reloaded.StartNowHotkey = "";
            reloaded.Save(path);
            Assert.Equal("", AppSettings.Load(path).StartNowHotkey);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
