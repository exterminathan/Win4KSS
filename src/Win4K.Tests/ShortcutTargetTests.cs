using Win4K.Core;
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

    // EnsureCompanionExe only reports; it never creates or rewrites the companion. Only the
    // installers create it, by copying the whole loose publish. Copying the running image was
    // wrong both ways: from a loose install the copy lacked its sibling DLLs, and from the
    // one-file .scr in System32 it overwrote the companion Win4KSS.exe with the bundle (and,
    // through the hard link, the user-scope Win4KSS.scr).

    [Fact]
    public void Missing_companion_is_reported_and_not_created()
    {
        var root = Path.Combine(Path.GetTempPath(), "win4k-companion-test-" + Guid.NewGuid().ToString("N")[..8]);
        var companion = Path.Combine(root, "Programs", "Win4KSS", "Win4KSS.exe");
        using var scope = AppPaths.UseRoot(Path.Combine(root, "home"));

        try
        {
            Assert.False(ShortcutManager.EnsureCompanionExe(companion));

            Assert.False(File.Exists(companion));
            Assert.False(Directory.Exists(Path.GetDirectoryName(companion)));
            Assert.Contains("no companion exe at " + companion, File.ReadAllText(AppPaths.LogFile));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Existing_companion_is_reported_and_left_untouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "win4k-companion-test-" + Guid.NewGuid().ToString("N")[..8]);
        var companion = Path.Combine(root, "Win4KSS.exe");
        using var scope = AppPaths.UseRoot(Path.Combine(root, "home"));

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(companion, "stands in for the installed exe");
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(companion, stamp); // older than the running image: the old code recopied here

            Assert.True(ShortcutManager.EnsureCompanionExe(companion));

            Assert.Equal("stands in for the installed exe", File.ReadAllText(companion));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(companion));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Companion_path_follows_the_companion_directory()
    {
        Assert.Equal(Path.Combine(AppPaths.CompanionDirectory, "Win4KSS.exe"), ShortcutManager.CompanionExePath);
    }
}