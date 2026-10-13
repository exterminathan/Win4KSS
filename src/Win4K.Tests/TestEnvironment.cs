using System.Runtime.CompilerServices;
using Win4K.Core;

namespace Win4K.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Points the app-data root at a temp folder for the whole test run. Without this, tests
    /// that exercise the playlist write "playlist stalled" and "watchdog" lines into the real
    /// win4k.log — the same log a soak test reads to prove there were no stalls.
    /// </summary>
#pragma warning disable CA2255 // a test assembly is exactly where a module initializer belongs
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RedirectAppData()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeOverrideVariable)))
            return;

        var home = Path.Combine(Path.GetTempPath(), "win4k-tests-home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(AppPaths.HomeOverrideVariable, home);
    }
}
