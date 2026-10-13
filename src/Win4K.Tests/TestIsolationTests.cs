using Win4K.Core;

namespace Win4K.Tests;

/// <summary>
/// Guards the one rule every test that touches app paths depends on: nothing in the suite
/// may resolve to the real %LOCALAPPDATA%\Win4KScreensavers, where the installed saver
/// keeps its settings, cache, log and ca-bundle.pem.
/// </summary>
public class TestIsolationTests
{
    private static readonly string RealRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win4KScreensavers");

    [Fact]
    public void App_data_root_is_redirected_for_the_whole_run()
    {
        Assert.NotEqual(RealRoot, AppPaths.Root, StringComparer.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeOverrideVariable)));
    }

    [Fact]
    public void A_scoped_root_is_visible_only_inside_its_scope()
    {
        var outer = AppPaths.Root;
        var scoped = Path.Combine(Path.GetTempPath(), "win4k-scope-" + Guid.NewGuid().ToString("N")[..8]);

        using (AppPaths.UseRoot(scoped))
            Assert.Equal(scoped, AppPaths.Root);

        Assert.Equal(outer, AppPaths.Root);
    }

    [Fact]
    public void Ca_bundle_is_written_under_the_test_root()
    {
        Assert.StartsWith(AppPaths.Root, CaBundle.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RealRoot, CaBundle.Path, StringComparison.OrdinalIgnoreCase);
    }
}
