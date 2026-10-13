using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// Regression cover for a silent failure that is easy to reintroduce.
///
/// Windows registers scrfile's "open" verb as `"%1" /S`. Explorer launches shortcuts through
/// the shell, so a shortcut whose target is the installed .scr has its own arguments thrown
/// away and /S substituted. The screen saver still appears, so it looks like it worked — but
/// it was started directly rather than by Windows, and "On resume, display logon screen" is
/// therefore never applied. The walk-away hotkey stops locking the machine.
///
/// Measured with an actual .lnk against this build:
///   target .scr + /now  ->  Launched with verb 'FullScreen'
///   target .exe + /now  ->  Launched with verb 'StartNow'
///
/// Hence: shortcut targets must always be .exe.
/// </summary>
public class ShortcutTargetTests
{
    [Fact]
    public void Shortcut_target_is_never_the_screen_saver_file()
    {
        var target = ShortcutManager.ResolveTarget();

        Assert.False(target.EndsWith(".scr", StringComparison.OrdinalIgnoreCase),
            $"shortcut would target '{target}', whose arguments the shell discards");
    }

    [Fact]
    public void Shortcut_target_is_an_executable()
    {
        Assert.EndsWith(".exe", ShortcutManager.ResolveTarget(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Companion_path_sits_under_local_appdata_programs()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Win4KSS", "Win4KSS.exe");

        Assert.Equal(expected, ShortcutManager.CompanionExePath);
    }

    [Fact]
    public void Shortcut_file_name_is_stable()
    {
        // The installer and the settings dialog both create and remove this by name; if they
        // ever disagree, unchecking the box in settings would silently leave one behind.
        Assert.Equal("Start Screen Saver.lnk", ShortcutManager.ShortcutFileName);
        Assert.EndsWith(ShortcutManager.ShortcutFileName, ShortcutManager.DesktopPath);
        Assert.EndsWith(ShortcutManager.ShortcutFileName, ShortcutManager.StartMenuPath);
    }
}
