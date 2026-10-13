using Win4K.Core;
using Win4K.Core.Catalogs;
using Xunit.Abstractions;

namespace Win4K.Tests;

/// <summary>
/// The real catalogue refresh against archive.org, NASA and the aerial-manifests feeds.
/// Inert unless WIN4K_LIVE_TESTS=1:
///
///     $env:WIN4K_LIVE_TESTS="1"; dotnet test --filter FullyQualifiedName~CatalogRefreshLiveTests
///
/// Writes into WIN4K_CATALOG_OUT when set (this is how the embedded fallback catalogues in
/// assets/registry are regenerated: point it at a scratch folder, review, then copy), else a
/// temp folder that is deleted afterwards. Never into %LOCALAPPDATA% or assets/registry.
/// </summary>
public class CatalogRefreshLiveTests(ITestOutputHelper output)
{
    private static bool LiveEnabled => Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") == "1";

    /// <summary>
    /// Floors from the 2026-08-12 harvest by scripts/harvest.py (1060 / 9 / 289 assets),
    /// lowered slightly to tolerate upstream drift; the exact numbers are printed.
    /// </summary>
    [Theory]
    [InlineData("archive-org", 1000)]
    [InlineData("nasa", 5)]
    [InlineData("aerial-manifests", 280)]
    public async Task Refresh_harvests_at_least_what_harvest_py_did(string sourceId, int floor)
    {
        if (!LiveEnabled) return;

        var outDir = Environment.GetEnvironmentVariable("WIN4K_CATALOG_OUT");
        var temp = string.IsNullOrWhiteSpace(outDir);
        var dir = temp ? Path.Combine(Path.GetTempPath(), "win4k-live-catalogs-" + Guid.NewGuid().ToString("N")[..8]) : outDir!;

        try
        {
            var store = new CatalogStore(dir);
            var refresher = new CatalogRefresher(store);
            var progress = new HarvesterTests.SyncProgress<CatalogRefreshProgress>(p =>
            {
                if (p.Done == p.Total)
                    output.WriteLine($"  [{p.SourceId}] {p.Stage}: {p.Done}/{p.Total}");
            });

            var started = DateTime.UtcNow;
            var report = await refresher.RefreshAsync([sourceId], progress);
            var outcome = report.Sources.Single();

            output.WriteLine($"{outcome}  in {(DateTime.UtcNow - started).TotalSeconds:0}s");
            foreach (var note in outcome.Notes)
                output.WriteLine($"  note: {note}");
            output.WriteLine($"  ffprobe: {FfprobeMediaProbe.Locate() ?? "not found"}");
            output.WriteLine($"  -> {outcome.Path}");

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.True(outcome.HarvestedCount >= floor, $"{sourceId}: harvested {outcome.HarvestedCount}, expected >= {floor}");

            var clips = CatalogClips.ToClipInfos(store.LoadLocal(sourceId)!, sourceId);
            Assert.Equal(outcome.AssetCount, clips.Count);
            Assert.All(clips, c => Assert.NotNull(c.ToClip(VideoQuality.Uhd4KSdr)));
        }
        finally
        {
            if (temp)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
