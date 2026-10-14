using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// <see cref="CompanionRelay"/>: the System32 .scr must hand every verb to the installed exe —
/// a firewall that allowed that exe blocked the System32 copy's streaming on a fresh PC — while
/// the installed exe itself, and a dev build, run in place.
/// </summary>
public class CompanionRelayTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string Companion = @"C:\Users\x\AppData\Local\Programs\Win4KSS";

    private static Func<string, bool> Existing(params string[] files) =>
        path => files.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_system32_copy_relays_to_the_installed_exe()
    {
        var target = CompanionRelay.Target(System32, Companion, Existing(Companion + @"\Win4KSS.exe", Companion + @"\libmpv-2.dll"));
        Assert.Equal(Companion + @"\Win4KSS.exe", target);
    }

    [Fact]
    public void The_installed_exe_runs_in_place()
    {
        Assert.Null(CompanionRelay.Target(Companion, Companion, Existing(Companion + @"\Win4KSS.exe", Companion + @"\libmpv-2.dll")));
    }

    [Fact]
    public void A_build_with_natives_beside_it_runs_in_place()
    {
        const string dev = @"D:\src\publish";
        Assert.Null(CompanionRelay.Target(dev, Companion, Existing(dev + @"\libmpv-2.dll", Companion + @"\Win4KSS.exe")));
    }

    [Fact]
    public void With_no_install_to_relay_to_it_runs_in_place()
    {
        Assert.Null(CompanionRelay.Target(System32, Companion, Existing()));
    }

    [Fact]
    public void No_arguments_relay_as_configure_not_as_the_exes_window_mode()
    {
        Assert.Equal(["/c"], CompanionRelay.RelayedArguments([]));
        Assert.Equal(["/p", "1234"], CompanionRelay.RelayedArguments(["/p", "1234"]));
    }
}
