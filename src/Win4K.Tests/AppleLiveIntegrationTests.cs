using Win4K.Core;
using Win4K.Core.Apple;

namespace Win4K.Tests;

/// <summary>
/// Hits sylvan.apple.com for real. Inert unless WIN4K_LIVE_TESTS=1, so the normal suite
/// stays offline and fast, but the network path is still exercisable on demand:
///
///     $env:WIN4K_LIVE_TESTS="1"; dotnet test
///
/// xunit 2.9 has no dynamic Assert.Skip, so a gated test returns early and reports as
/// passed. <see cref="Gate_is_visible_in_the_run"/> makes which mode you are in obvious
/// rather than letting a whole class quietly no-op.
/// </summary>
public class AppleLiveIntegrationTests : IDisposable
{
    private static bool LiveEnabled => Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") == "1";

    private readonly string _home;
    private readonly IDisposable _scope;

    public AppleLiveIntegrationTests()
    {
        // Redirect the app-data tree into a temp dir for this test only. Scoped through
        // AppPaths.UseRoot (an AsyncLocal) rather than WIN4K_HOME: flipping the process-wide
        // variable here, and deleting the folder on dispose, pulled the root out from under
        // CaBundleTests running in parallel — the intermittent ca-bundle.pem failure.
        _home = Path.Combine(Path.GetTempPath(), "win4k-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _scope = AppPaths.UseRoot(_home);
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_scoped_root_reaches_the_test_body()
    {
        Assert.Equal(_home, AppPaths.Root);
    }

    [Fact]
    public void Gate_is_visible_in_the_run()
    {
        Console.WriteLine(LiveEnabled
            ? "WIN4K_LIVE_TESTS=1 -> network integration tests ARE running"
            : "WIN4K_LIVE_TESTS unset -> network integration tests are INERT");
    }

    [Fact]
    public async Task Probe_finds_the_newest_published_tarball()
    {
        if (!LiveEnabled) return;

        var url = await new AppleManifestClient().ProbeTarballUrlAsync();

        Assert.NotNull(url);
        Assert.StartsWith("https://sylvan.apple.com/Aerials/resources", url);
        Assert.EndsWith(".tar", url);
    }

    [Fact]
    public async Task Live_manifest_downloads_extracts_and_caches()
    {
        if (!LiveEnabled) return;

        var client = new AppleManifestClient();
        var manifest = await client.GetAsync(forceRefresh: true);

        Assert.True(manifest.Assets.Count >= 100,
            $"Expected >= 100 assets from the live manifest, got {manifest.Assets.Count}");
        Assert.Equal("sylvan.apple.com", client.LastResolvedFrom);

        // Having fetched it, the on-disk cache must exist and be preferred next time.
        Assert.True(File.Exists(AppPaths.ManifestFile));

        var second = new AppleManifestClient();
        var cached = await second.GetAsync();

        Assert.Equal(manifest.Assets.Count, cached.Assets.Count);
        Assert.Equal("disk cache", second.LastResolvedFrom);
    }

    [Fact]
    public async Task First_clip_url_is_reachable_and_really_is_4K()
    {
        if (!LiveEnabled) return;

        var manifest = await new AppleManifestClient().GetAsync();
        var clip = AppleAerialSource.ToClips(manifest, VideoQuality.Uhd4KSdr)[0];

        using var request = new HttpRequestMessage(HttpMethod.Head, clip.Url);
        using var response = await Http.Client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"HEAD {clip.Url} returned {(int)response.StatusCode}");
        Assert.Equal("video/quicktime", response.Content.Headers.ContentType?.MediaType);

        // ~346 MB each; anything small means we were handed an error document instead.
        Assert.True(response.Content.Headers.ContentLength > 50_000_000,
            $"Unexpectedly small asset: {response.Content.Headers.ContentLength} bytes");
    }

    [Fact]
    public async Task Falls_back_to_the_embedded_copy_when_the_network_is_unusable()
    {
        // Runs offline too: an unresolvable override URL stands in for Apple being unreachable.
        var client = new AppleManifestClient(tarballUrlOverride: "https://invalid.invalid/nope.tar");
        var manifest = await client.GetAsync(forceRefresh: true);

        Assert.Equal(114, manifest.Assets.Count);
        Assert.Equal("embedded fallback", client.LastResolvedFrom);
    }
}
