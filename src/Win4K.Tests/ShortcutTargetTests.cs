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

    [Fact]
    public void Test_host_is_not_a_single_file_build()
    {
        // Sanity check for the next test's premise: xunit runs out of an ordinary loose test
        // assembly, so Assembly.Location is never empty here. This is exactly the shape the
        // shipped app has now too, with libmpv-2.dll/WebView2Loader.dll loose beside the exe
        // instead of bundled inside a single-file host.
        Assert.False(ShortcutManager.IsSingleFileBuild);
    }

    [Fact]
    public void Not_single_file_and_no_companion_logs_instead_of_copying()
    {
        // With a loose multi-file publish, Environment.ProcessPath (the test host) has none of
        // Win4KSS's sibling DLLs beside it. Copying it to the destination would produce an exe
        // that cannot run — so when there is no companion there already, this must log and
        // report failure rather than create one.
        var destination = Path.Combine(Path.GetTempPath(),
            "win4k-companion-test-" + Guid.NewGuid().ToString("N")[..8], "Win4KSS.exe");

        try
        {
            Assert.False(File.Exists(destination));

            var result = ShortcutManager.EnsureCompanionExe(destination);

            Assert.False(result);
            Assert.False(File.Exists(destination));
            Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        }
        finally
        {
            var dir = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Not_single_file_and_existing_companion_is_left_alone()
    {
        // A real companion already sitting at the destination (put there by install-scr.ps1 or
        // the installer, which copy the whole publish folder) must be reported as present, with
        // nothing rewritten.
        var dir = Path.Combine(Path.GetTempPath(), "win4k-companion-test-" + Guid.NewGuid().ToString("N")[..8]);
        var destination = Path.Combine(dir, "Win4KSS.exe");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(destination, "not a real exe, just needs to exist");
            var before = File.ReadAllText(destination);

            var result = ShortcutManager.EnsureCompanionExe(destination);

            Assert.True(result);
            Assert.Equal(before, File.ReadAllText(destination));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
