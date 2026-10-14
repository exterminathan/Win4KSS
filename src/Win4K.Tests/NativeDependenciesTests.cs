using Win4K.Core;
using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// Where the native DLLs come from. The one-file Win4KSS.scr in System32 carries none (libmpv-2.dll
/// and WebView2Loader.dll in System32 would shadow other apps' copies), so it borrows the
/// user-scope install's; a loose install or a dev build has them beside the exe.
/// </summary>
public sealed class NativeDependenciesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "win4k-natives-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IDisposable _home;
    private readonly string _exeDir;
    private readonly string _companionDir;

    public NativeDependenciesTests()
    {
        _home = AppPaths.UseRoot(Path.Combine(_root, "home"));
        _exeDir = Path.Combine(_root, "System32");
        _companionDir = Path.Combine(_root, "Programs", "Win4KSS");
        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_companionDir);
    }

    public void Dispose()
    {
        _home.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Put(string dir, params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Combine(dir, name), "");
    }

    private NativeLocation Locate() => NativeDependencies.Locate(_exeDir, _companionDir, File.Exists);

    [Fact]
    public void Natives_beside_the_exe_win_even_when_the_companion_has_them_too()
    {
        Put(_exeDir, NativeDependencies.LibMpv);
        Put(_companionDir, NativeDependencies.LibMpv, NativeDependencies.WebView2Loader);

        var location = Locate();

        Assert.Equal(NativeLocationKind.BesideExe, location.Kind);
        Assert.Equal(_exeDir, location.Directory);
        Assert.True(location.CanPlay);
        Assert.True(location.CanBrowse);
    }

    [Fact]
    public void Nothing_beside_the_exe_uses_a_complete_companion_dir()
    {
        Put(_companionDir, NativeDependencies.LibMpv, NativeDependencies.WebView2Loader);

        var location = Locate();

        Assert.Equal(NativeLocationKind.Companion, location.Kind);
        Assert.Equal(_companionDir, location.Directory);
        Assert.Empty(location.MissingLibraries);
        Assert.True(location.CanPlay);
        Assert.True(location.CanBrowse);
    }

    [Fact]
    public void Companion_without_the_WebView2_loader_can_play_but_not_browse()
    {
        Put(_companionDir, NativeDependencies.LibMpv);

        var location = Locate();

        Assert.Equal(NativeLocationKind.Companion, location.Kind);
        Assert.Equal([NativeDependencies.WebView2Loader], location.MissingLibraries);
        Assert.True(location.CanPlay);
        Assert.False(location.CanBrowse);
    }

    [Fact]
    public void Companion_without_libmpv_is_missing()
    {
        Put(_companionDir, NativeDependencies.WebView2Loader);

        var location = Locate();

        Assert.Equal(NativeLocationKind.Missing, location.Kind);
        Assert.False(location.CanPlay);
        Assert.False(location.CanBrowse);
    }

    [Fact]
    public void No_companion_dir_at_all_is_missing()
    {
        var location = NativeDependencies.Locate(_exeDir, Path.Combine(_root, "nowhere"), File.Exists);

        Assert.Equal(NativeLocationKind.Missing, location.Kind);
        Assert.Equal(Path.Combine(_root, "nowhere"), location.Directory);
        Assert.Contains(NativeDependencies.LibMpv, location.MissingLibraries);
    }

    [Fact]
    public void Companion_dir_that_is_the_exe_dir_is_not_a_second_chance()
    {
        // The loose install run from its own folder with libmpv deleted: "borrowing" from
        // itself would just fail later, so it is reported as missing.
        Put(_exeDir, NativeDependencies.WebView2Loader);

        var location = NativeDependencies.Locate(_exeDir, _exeDir + Path.DirectorySeparatorChar, File.Exists);

        Assert.Equal(NativeLocationKind.Missing, location.Kind);
    }

    [Fact]
    public void Companion_directory_defaults_to_the_user_scope_install()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.CompanionOverrideVariable)))
            return; // a smoke run pointed it elsewhere on purpose

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Win4KSS"),
            AppPaths.CompanionDirectory);
    }

    [Fact]
    public void Already_loaded_module_paths_are_reported_and_unloaded_ones_are_null()
    {
        var kernel32 = NativeDependencies.LoadedModulePath("kernel32.dll");

        Assert.NotNull(kernel32);
        Assert.True(Path.IsPathFullyQualified(kernel32));
        Assert.EndsWith("kernel32.dll", kernel32, StringComparison.OrdinalIgnoreCase);
        Assert.Null(NativeDependencies.LoadedModulePath("win4k-no-such-module.dll"));
    }

    // ---- what each verb does without them ------------------------------------------------

    private static readonly NativeLocation Missing =
        new(NativeLocationKind.Missing, @"C:\nowhere", [NativeDependencies.LibMpv, NativeDependencies.WebView2Loader]);

    private static readonly NativeLocation PlayOnly =
        new(NativeLocationKind.Companion, @"C:\install", [NativeDependencies.WebView2Loader]);

    private static readonly NativeLocation Complete = new(NativeLocationKind.Companion, @"C:\install", []);

    [Fact]
    public void Full_screen_without_libmpv_exits_quietly_instead_of_crashing()
    {
        Assert.Equal(Program.NativeGate.ExitQuietly, Program.GateOnNatives(Program.Verb.FullScreen, Missing));
        Assert.Equal(Program.NativeGate.Proceed, Program.GateOnNatives(Program.Verb.FullScreen, PlayOnly));
    }

    [Fact]
    public void Browser_without_its_loader_asks_the_user_to_reinstall()
    {
        Assert.Equal(Program.NativeGate.AskToReinstall, Program.GateOnNatives(Program.Verb.Configure, Missing));
        Assert.Equal(Program.NativeGate.AskToReinstall, Program.GateOnNatives(Program.Verb.Configure, PlayOnly));
        Assert.Equal(Program.NativeGate.Proceed, Program.GateOnNatives(Program.Verb.Configure, Complete));
    }

    [Fact]
    public void Windowed_without_libmpv_asks_the_user_to_reinstall()
    {
        Assert.Equal(Program.NativeGate.AskToReinstall, Program.GateOnNatives(Program.Verb.Window, Missing));
        Assert.Equal(Program.NativeGate.Proceed, Program.GateOnNatives(Program.Verb.Window, PlayOnly));
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("StartNow")]
    [InlineData("ExportSettings")]
    [InlineData("ImportSettings")]
    public void Verbs_that_need_no_natives_always_proceed(string verb)
    {
        Assert.Equal(Program.NativeGate.Proceed, Program.GateOnNatives(Enum.Parse<Program.Verb>(verb), Missing));
    }
}
